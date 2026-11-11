using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Minus700K {
// Only synthetic stores, signed fixture tokens, and loopback callbacks are used.
internal static class PlanRecoveryDiagnostics {
    static void Check(bool value,string message) { if(!value) throw new Exception("plan recovery test: "+message); }
    static void Wait(Task task) {
        Stopwatch elapsed=Stopwatch.StartNew(); while(!task.IsCompleted && elapsed.ElapsedMilliseconds<5000) Thread.Sleep(10);
        Check(task.IsCompleted,"operation completed within test deadline"); task.GetAwaiter().GetResult();
    }
    static void Canceled(Task task) { try { Wait(task); } catch(OperationCanceledException) { return; } throw new Exception("plan recovery test: expected cancellation"); }
    sealed class Attempt {
        internal readonly TaskCompletionSource<Uri> Opened=new TaskCompletionSource<Uri>();
        internal readonly Task<PlanAccount> Task;
        internal Attempt(ChatGptPlan plan,PlanDiagnostics.Issuer issuer,CancellationToken token) {
            Task=System.Threading.Tasks.Task.Run(()=>plan.SignInCurrent(delegate(Uri uri) {
                var values=PlanHttp.Query(uri.Query); issuer.Nonce=values["nonce"]; issuer.Challenge=values["code_challenge"]; issuer.Redirect=values["redirect_uri"]; Opened.SetResult(uri);
            },token));
        }
        internal Uri Authorization { get { Wait(Opened.Task); return Opened.Task.Result; } }
    }
    static int Reply(Uri authorization,string clientId,string stateOverride=null) {
        var values=PlanHttp.Query(authorization.Query);
        string callback=values["redirect_uri"]+"?"+PlanHttp.Form(new Dictionary<string,string> { {"code","fixture-code"},{"client_id",clientId},{"state",stateOverride??values["state"]} });
        Check(new Uri(callback).Host=="127.0.0.1","only loopback test requests");
        HttpWebRequest request=(HttpWebRequest)WebRequest.Create(callback); request.Proxy=null; request.Timeout=2000; request.AllowAutoRedirect=false; request.KeepAlive=false;
        try { using(var response=(HttpWebResponse)request.GetResponse()) return (int)response.StatusCode; }
        catch(WebException e) { if(e.Response==null) return 0; using(var response=(HttpWebResponse)e.Response) return (int)response.StatusCode; }
    }
    static void Pump(Func<bool> done) {
        Stopwatch elapsed=Stopwatch.StartNew(); while(!done() && elapsed.ElapsedMilliseconds<5000) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(),"UI operation completed within test deadline");
    }
    static void Invoke(PlanConnectionPanel panel,string method) { typeof(PlanConnectionPanel).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(panel,null); }
    static IEnumerable<Control> Descendants(Control parent) { foreach(Control child in parent.Controls) { yield return child; foreach(Control nested in Descendants(child)) yield return nested; } }
    internal static void Run(string directory,Dictionary<string,object> report) {
        string fixture=Path.Combine(directory,"recovery"); var store=new PlanStore(fixture);
        using(var issuer=new PlanDiagnostics.Issuer()) {
            Action<string> afterResponse=null;
            Func<string,string,string,string,CancellationToken,object> transport=(url,method,body,bearer,token)=> {
                object response=issuer.Request(url,method,body,bearer,token); if(afterResponse!=null) afterResponse(url);
                return JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(response));
            };
            var plan=new ChatGptPlan(store,transport);
            var saved=new PlanAccount { ClientId="retained",Subject="retained-subject",AccessToken="fixture-old",Scopes=issuer.Scope };
            var pending=new PlanAccount { ClientId="pending" };
            var selection=new PlanState { Accounts=new List<PlanAccount>{saved,pending} };
            Check(ChatGptPlan.LoginAccount(selection)==pending,"no active account reuses pending registration"); selection.ActiveClientId=saved.ClientId;
            Check(ChatGptPlan.LoginAccount(selection)==saved && !ChatGptPlan.NeedsConsent(saved),"active registration wins without forced consent");
            saved.Scopes="openid profile"; Check(ChatGptPlan.NeedsConsent(saved),"signed-in missing permission requests consent"); saved.ClearTokens(); Check(!ChatGptPlan.NeedsConsent(saved),"signed-out recovery is ordinary sign-in");
            Uri abandoned;
            using(var cancel=new CancellationTokenSource()) {
                var attempt=new Attempt(plan,issuer,cancel.Token); abandoned=attempt.Authorization; cancel.Cancel(); Canceled(attempt.Task);
                Check(Reply(abandoned,issuer.Client)==0 && store.Read().Active==null,"cancel stops listener without activating credentials");
            }
            var retry=new Attempt(plan,issuer,CancellationToken.None); Uri retryUri=retry.Authorization;
            var oldQuery=PlanHttp.Query(abandoned.Query); var newQuery=PlanHttp.Query(retryUri.Query);
            Check(newQuery["state"]!=oldQuery["state"] && newQuery["nonce"]!=oldQuery["nonce"] && newQuery["code_challenge"]!=oldQuery["code_challenge"],"retry regenerates state nonce and PKCE");
            int exchanges=issuer.Exchanges;
            Check(Reply(retryUri,issuer.Client,oldQuery["state"])==400 && issuer.Exchanges==exchanges,"old callback state cannot exchange or activate credentials");
            Check(Reply(retryUri,issuer.Client)==200,"new callback succeeds after stale callback rejection"); Wait(retry.Task);
            Check(store.Read().Active.Connected,"retry succeeds with a fresh cancellation lifetime");
            using(var cancel=new CancellationTokenSource()) {
                byte[] before=File.ReadAllBytes(Path.Combine(fixture,"chatgpt.dat"));
                afterResponse=url=> { if(url.EndsWith("/jwks.json")) cancel.Cancel(); };
                var attempt=new Attempt(plan,issuer,cancel.Token); Uri authorization=attempt.Authorization;
                Check(PlanHttp.Query(authorization.Query)["client_id"]==issuer.Client && !PlanHttp.Query(authorization.Query).ContainsKey("prompt"),"ordinary login reuses active client without consent prompt");
                Reply(authorization,issuer.Client); Canceled(attempt.Task); afterResponse=null;
                Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"chatgpt.dat"))),"cancel after token verification cannot commit credentials");
            }
            using(var cancel=new CancellationTokenSource()) {
                var attempt=new Attempt(plan,issuer,cancel.Token); Uri authorization=attempt.Authorization; Uri endpoint=new Uri(PlanHttp.Query(authorization.Query)["redirect_uri"]);
                using(var partial=new TcpClient()) {
                    partial.Connect(IPAddress.Loopback,endpoint.Port); byte[] bytes=Encoding.ASCII.GetBytes("GET /auth/callback?code=partial HTTP/1.1\r\n"); partial.GetStream().Write(bytes,0,bytes.Length);
                    Thread.Sleep(80); Stopwatch elapsed=Stopwatch.StartNew(); cancel.Cancel(); Canceled(attempt.Task); Check(elapsed.ElapsedMilliseconds<1500,"cancel interrupts a partial callback request");
                }
            }
            var timeout=new Attempt(new ChatGptPlan(store,transport,100),issuer,CancellationToken.None); Uri timeoutUri=timeout.Authorization;
            try { Wait(timeout.Task); throw new Exception("plan recovery test: expected timeout"); } catch(PlanException e) { Check(e.Code=="login_timeout","bounded timeout remains retryable"); }
            Check(Reply(timeoutUri,issuer.Client)==0,"timeout closes old callback listener");
            store.Locked(delegate { var state=store.ReadUnlocked(); state.Active.Scopes="openid profile resource.invoke"; state.Accounts.Add(new PlanAccount { ClientId="older-client",Subject="older-subject",Model="kept-model" }); store.SaveUnlocked(state); return true; });
            var consent=new Attempt(plan,issuer,CancellationToken.None); Uri consentUri=consent.Authorization;
            var consentQuery=PlanHttp.Query(consentUri.Query); Check(consentQuery["client_id"]==issuer.Client && consentQuery["prompt"]=="consent","missing permission recovery reuses issued client with consent");
            Check(Reply(consentUri,issuer.Client)==200,"consent retry callback"); Wait(consent.Task);
            Check(store.Read().Accounts.Count==2 && store.Read().Accounts.Any(a=>a.ClientId=="older-client" && a.Model=="kept-model"),"single-account UI preserves other stored accounts and preferences");
            // UI lifecycle: wait before mutation, cancel/retry controls, and a closing
            // panel still signal completion so runtime input is never left blocked.
            int opened=0,changing=0,updated=0; var busyStates=new List<bool>();
            using(var panel=new PlanConnectionPanel(new Settings(),false,plan,store,u=>Interlocked.Increment(ref opened))) {
                if(SynchronizationContext.Current==null) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                panel.ConnectionChanging+=delegate { changing++; }; panel.ConnectionUpdated+=delegate { updated++; }; panel.BusyStateChanged+=delegate { busyStates.Add(panel.IsBusy); };
                var modelPicker=Descendants(panel).OfType<ComboBox>().Single(); int modelIndex=modelPicker.Items.Add(new PlanModel { Slug="fixture-model",Name="Fixture model" }); modelPicker.SelectedIndex=modelIndex;
                Check(panel.HasModelChanges && store.Read().Active.Model!="fixture-model","choosing a model only changes the unsaved draft");
                panel.ApplyModelSelection(null); panel.AcceptModelSelection();
                Check(updated==0 && changing==0 && !panel.HasModelChanges && store.Read().Active.Model=="fixture-model","shared model save affects the next request without account transition events");
                panel.BeforeConnectionChange=async token=> { await Task.Delay(Timeout.Infinite,token); };
                Invoke(panel,"BeginLogin"); Check(panel.IsBusy && changing==1 && opened==0,"auth waits for runtime quiescence before browser or store mutations");
                var buttons=Descendants(panel).OfType<Button>().ToArray();
                Check(buttons.Any(b=>b.Text=="ログインを中止" && b.Enabled) && buttons.Any(b=>b.Text=="ログインをやり直す" && b.Enabled) && buttons.Any(b=>b.Text.StartsWith("Manage usage") && b.Enabled),"cancel retry and usage remain available during login");
                Check(!buttons.Any(b=>b.Text=="このアカウントを使用" || b.Text=="プランの利用を許可") && Descendants(panel).OfType<ComboBox>().Count()==1,"single account display has no duplicate consent or account picker");
                Invoke(panel,"CancelLogin"); Pump(()=>!panel.IsBusy); Check(opened==0 && updated==0,"cancel during quiescence keeps credentials and emits no update");
                panel.BeforeConnectionChange=null; Invoke(panel,"BeginLogin"); Pump(()=>opened==1);
                int restartStateStart=busyStates.Count; Invoke(panel,"RetryLogin"); Pump(()=>opened==2); Check(panel.IsBusy,"restart remains busy after new browser starts");
                Check(busyStates.Skip(restartStateStart).All(v=>v),"restart never releases runtime busy gate between attempts");
                panel.Dispose(); Pump(()=>!panel.IsBusy); Check(busyStates.Last()==false && updated==0,"dispose cancels login and releases runtime busy gate after completion");
            }
            var committedBrowser=new TaskCompletionSource<Uri>(); int committedUpdates=0; bool committedBusy=true;
            using(var panel=new PlanConnectionPanel(new Settings(),false,plan,store,uri=> {
                var query=PlanHttp.Query(uri.Query); issuer.Nonce=query["nonce"]; issuer.Challenge=query["code_challenge"]; issuer.Redirect=query["redirect_uri"]; committedBrowser.SetResult(uri);
            })) {
                panel.ConnectionUpdated+=delegate { committedUpdates++; }; panel.BusyStateChanged+=delegate { committedBusy=panel.IsBusy; };
                Invoke(panel,"BeginLogin"); Wait(committedBrowser.Task);
                Check(Reply(committedBrowser.Task.Result,issuer.Client)==200,"fixture login committed before UI cancellation");
                panel.Dispose(); Pump(()=>!panel.IsBusy);
                Check(committedUpdates==1 && !committedBusy,"committed credentials publish update even after panel disposal or cancellation");
            }
            var pendingStore=new PlanStore(Path.Combine(directory,"pending-recovery"));
            pendingStore.Locked(delegate { pendingStore.SaveUnlocked(new PlanState { Accounts=new List<PlanAccount> {new PlanAccount { ClientId=issuer.Client }} }); return true; });
            var resumePending=new Attempt(new ChatGptPlan(pendingStore,transport),issuer,CancellationToken.None); Uri pendingUri=resumePending.Authorization;
            Check(PlanHttp.Query(pendingUri.Query)["client_id"]==issuer.Client && !PlanHttp.Query(pendingUri.Query).ContainsKey("prompt"),"interrupted registration resumes issued client without forced consent");
            Check(Reply(pendingUri,issuer.Client)==200,"pending registration callback succeeds"); Wait(resumePending.Task);
            Check(pendingStore.Read().Accounts.Count==1 && pendingStore.Read().Active.Connected,"pending recovery never creates a second registration");
            report["recoveryChecks"]="single active/pending registration / conditional consent / cancel listener and partial callback / fresh retry state nonce PKCE / stale callback rejection / timeout cleanup / cancellation before credential commit / retained prior accounts / quiescence before auth / responsive login controls / disposal completion notification / committed-cancel race notification / pending registration recovery";
        }
    }
}
}

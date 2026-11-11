using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Minus700K {
// Hermetic OAuth tests. No browser, real account, inference, or production credentials.
internal static class PlanDiagnostics {
    static void Check(bool condition,string message) { if(!condition) throw new Exception("plan test: "+message); }
    static void Reject(Action action,string code) {
        try { action(); } catch(PlanException e) { Check(e.Code==code,"expected "+code+", got "+e.Code); return; }
        throw new Exception("plan test: expected rejection "+code);
    }
    internal sealed class Issuer : IDisposable {
        internal readonly RSACryptoServiceProvider Rsa=new RSACryptoServiceProvider(2048);
        internal string Client="test_client_one",Subject="test_subject_one",Nonce="",Scope="openid profile email offline_access resource.invoke chatgpt.tokens.use.direct",Failure="";
        internal string Challenge="",Redirect="",Refresh="fixture-refresh-1";
        internal int Exchanges,Renewals,Revocations;
        internal bool RevocationOffline;
        internal object Jwks { get { RSAParameters p=Rsa.ExportParameters(false); return JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(new { keys=new[]{new { kty="RSA",kid="fixture-key",alg="RS256",use="sig",n=ChatGptPlan.Base64Url(p.Modulus),e=ChatGptPlan.Base64Url(p.Exponent) }} })); } }
        internal Issuer() { Rsa.PersistKeyInCsp=false; }
        internal Dictionary<string,object> Claims() { return new Dictionary<string,object> { {"iss",PlanHttp.Issuer},{"sub",Subject},{"aud",Client},{"nonce",Nonce},{"iat",ChatGptPlan.Now},{"exp",ChatGptPlan.Now+3600},{"email","fixture@example.invalid"} }; }
        internal string Sign(object claims) {
            string content=ChatGptPlan.Base64Url(Encoding.UTF8.GetBytes(JsonData.Serializer().Serialize(new { alg="RS256",kid="fixture-key" })))+"."+ChatGptPlan.Base64Url(Encoding.UTF8.GetBytes(JsonData.Serializer().Serialize(claims)));
            byte[] hash; using(SHA256 sha=SHA256.Create()) hash=sha.ComputeHash(Encoding.ASCII.GetBytes(content));
            return content+"."+ChatGptPlan.Base64Url(Rsa.SignHash(hash,CryptoConfig.MapNameToOID("SHA256")));
        }
        internal object Request(string url,string method,string body,string bearer,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if(Failure.Length>0 && url.EndsWith("/oauth/token")) throw PlanException.FromCode(Failure,0);
            if(url.EndsWith("/jwks.json")) return Jwks;
            if(url.EndsWith("/openid-configuration")) return new { revocation_endpoint=PlanHttp.Issuer+"/api/accounts/oauth/revoke" };
            if(url.EndsWith("/oauth/token")) {
                Check(method=="POST" && bearer==null,"token exchange is form POST without bearer");
                var fields=PlanHttp.Query(body); Check(fields["client_id"]==Client && fields["resource"]==PlanHttp.Resource && !fields.ContainsKey("client_secret"),"issued client and resource, no client secret");
                if(fields["grant_type"]=="authorization_code") {
                    Check(fields["code"]=="fixture-code" && fields["redirect_uri"]==Redirect && ChatGptPlan.Challenge(fields["code_verifier"])==Challenge,"exact callback and PKCE verifier");
                    Exchanges++; return new { token_type="Bearer",access_token="fixture-access-1",refresh_token=Refresh,id_token=Sign(Claims()),scope=Scope,expires_in=3600 };
                }
                Check(fields["grant_type"]=="refresh_token" && fields["refresh_token"]==Refresh && !fields.ContainsKey("scope"),"serialized latest refresh token without scope");
                Renewals++; Thread.Sleep(80); Refresh="fixture-refresh-"+(Renewals+1);
                return new { token_type="Bearer",access_token="fixture-access-"+(Renewals+1),refresh_token=Refresh,expires_in=3600 };
            }
            if(url.EndsWith("/oauth/revoke")) {
                Revocations++; if(RevocationOffline) throw new PlanException("network_error","fixture offline");
                var fields=PlanHttp.Query(body); Check(fields["client_id"]==Client && fields["token"]==Refresh && fields["token_type_hint"]=="refresh_token","revoke latest refresh token");
                return new Dictionary<string,object>();
            }
            if(url==PlanHttp.Resource+"/models") { Check(!string.IsNullOrEmpty(bearer),"model catalog uses OAuth bearer"); return new { models=new[]{new {slug="hidden",display_name="Hidden",visibility="hide"},new {slug="eligible",display_name="Eligible",visibility="list"}} }; }
            throw new Exception("Unexpected fixture endpoint");
        }
        internal PlanAccount Login(ChatGptPlan plan,string selected=null,bool consent=false) {
            Task callback=null;
            PlanAccount result=plan.SignIn(selected,consent,delegate(Uri authorization) {
                Check(authorization.Host=="auth.openai.com" && authorization.Scheme=="https","official authorize endpoint");
                var query=PlanHttp.Query(authorization.Query); Nonce=query["nonce"]; Challenge=query["code_challenge"]; Redirect=query["redirect_uri"];
                Check(query["code_challenge_method"]=="S256" && query["client_id"]==(selected??"dynamic_agent_client") && query["ext_agent_host_id"].StartsWith("urn:uuid:"),"stable host and client registration");
                Check(selected==null?query["agent_name_hint"]==ChatGptPlan.AppName:!query.ContainsKey("agent_name_hint"),"name hint only on first registration");
                Check(consent?query["prompt"]=="consent":!query.ContainsKey("prompt"),"explicit consent only");
                if(selected!=null) Check(query.ContainsKey("id_token_hint"),"returning identity hint");
                string address=Redirect+"?"+PlanHttp.Form(new Dictionary<string,string> { {"code","fixture-code"},{"state",query["state"]},{"client_id",Client} });
                callback=Task.Run(delegate {
                    HttpWebRequest request=(HttpWebRequest)WebRequest.Create(address); request.Proxy=null; request.Timeout=15000;
                    using(WebResponse response=request.GetResponse()) using(StreamReader reader=new StreamReader(response.GetResponseStream())) Check(!reader.ReadToEnd().Contains("fixture-access"),"callback never includes credentials");
                });
            },CancellationToken.None);
            callback.GetAwaiter().GetResult(); return result;
        }
        public void Dispose() { Rsa.Dispose(); }
    }
    internal static void Run(Dictionary<string,object> report) {
        Check(ChatGptPlan.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")=="E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM","RFC 7636 PKCE vector");
        var callback=PlanHttp.Query("?code=x&state=expected&client_id=issued_client");
        Check(ChatGptPlan.CallbackClient(callback,"expected",null)=="issued_client","initial issued client");
        Reject(()=>ChatGptPlan.CallbackClient(callback,"wrong",null),"invalid_state");
        Reject(()=>ChatGptPlan.CallbackClient(callback,"expected","another_client"),"client_mismatch");
        Reject(()=>PlanHttp.Query("state=a&state=b"),"invalid_callback");
        Reject(()=>ChatGptPlan.CallbackClient(PlanHttp.Query("code=x&state=s"),"s",null),"missing_client");
        Reject(()=>ChatGptPlan.CallbackClient(PlanHttp.Query("error=access_denied&state=s"),"s",null),"login_declined");
        string temp=Path.GetFullPath(Path.GetTempPath()),directory=Path.GetFullPath(Path.Combine(temp,"Minus700K-plan-test-"+Guid.NewGuid().ToString("N")));
        Check(directory.StartsWith(temp.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture stays within temp");
        try {
            var store=new PlanStore(directory);
            using(Issuer issuer=new Issuer()) {
                issuer.Nonce="fixture-nonce";
                string jwt=issuer.Sign(issuer.Claims());
                Check(JsonData.Text(ChatGptPlan.ValidateIdToken(jwt,issuer.Client,issuer.Nonce,issuer.Subject,issuer.Jwks,ChatGptPlan.Now),"sub")==issuer.Subject,"verified RS256 ID token");
                foreach(string field in new[]{"iss","aud","nonce","sub","exp"}) {
                    var claims=issuer.Claims(); claims[field]=field=="exp"?(object)(ChatGptPlan.Now-120):"wrong";
                    Reject(()=>ChatGptPlan.ValidateIdToken(issuer.Sign(claims),issuer.Client,issuer.Nonce,issuer.Subject,issuer.Jwks,ChatGptPlan.Now),"invalid_identity");
                }
                string[] parts=jwt.Split('.'); byte[] signature=ChatGptPlan.Decode(parts[2]); signature[0]^=1;
                Reject(()=>ChatGptPlan.ValidateIdToken(parts[0]+"."+parts[1]+"."+ChatGptPlan.Base64Url(signature),issuer.Client,issuer.Nonce,issuer.Subject,issuer.Jwks,ChatGptPlan.Now),"invalid_identity");
                var plan=new ChatGptPlan(store,(url,method,body,bearer,token)=>JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(issuer.Request(url,method,body,bearer,token))));
                using(var canceled=new CancellationTokenSource()) { canceled.Cancel(); bool opened=false; try { plan.SignIn(null,false,u=>opened=true,canceled.Token); throw new Exception("missing cancellation"); } catch(OperationCanceledException) { Check(!opened,"canceled login never opens browser"); } }
                PlanAccount account=issuer.Login(plan); string host=store.Read().HostId;
                Check(account.Connected && account.Subject==issuer.Subject && store.Read().Active.Key==account.Key,"activate only verified account with granted scope");
                byte[] protectedBytes=File.ReadAllBytes(Path.Combine(directory,"chatgpt.dat"));
                Check(!Encoding.UTF8.GetString(protectedBytes).Contains("fixture-access") && !Encoding.Unicode.GetString(protectedBytes).Contains("fixture-refresh"),"DPAPI ciphertext excludes plaintext credentials");
                Check(new PlanStore(directory).Read().Active.AccessToken==account.AccessToken,"encrypted atomic store roundtrip");
                Check(plan.Models(account,CancellationToken.None).Select(m=>m.Slug).SequenceEqual(new[]{"eligible"}),"only eligible visible model catalog");
                issuer.Login(plan,account.ClientId,true); Check(store.Read().HostId==host && store.Read().Accounts.Count==1,"returning login reuses stable registration");
                store.Locked(delegate { var s=store.ReadUnlocked(); s.Active.ExpiresAt=0; store.SaveUnlocked(s); return true; });
                var refreshes=Enumerable.Range(0,5).Select(i=>Task.Run(()=>plan.Access(CancellationToken.None))).ToArray(); Task.WaitAll(refreshes);
                Check(issuer.Renewals==1 && refreshes.All(t=>t.Result.AccessToken=="fixture-access-2") && store.Read().Active.RefreshToken==issuer.Refresh,"one serialized refresh and rotation for concurrent requests");
                store.Locked(delegate { var s=store.ReadUnlocked(); s.Active.ExpiresAt=0; store.SaveUnlocked(s); return true; });
                issuer.Failure="network_error"; Reject(()=>plan.Access(CancellationToken.None),"network_error"); Check(store.Read().Active.RefreshToken==issuer.Refresh,"transient error preserves refresh token");
                issuer.Failure="invalid_grant"; Reject(()=>plan.Access(CancellationToken.None),"invalid_grant"); Check(store.Read().Active.AccessToken.Length==0 && store.Read().Active.RefreshToken.Length==0,"terminal refresh error clears unusable credentials"); issuer.Failure="";
                // Log in again after terminal refresh failure; the durable registration is preserved.
                issuer.Login(plan); Check(store.Read().Accounts.Count==1 && store.Read().HostId==host,"registration survives terminal errors");
                Check(plan.SignOut(issuer.Client,CancellationToken.None) && issuer.Revocations==1 && !store.Read().Active.Connected && store.Read().Active.Subject==issuer.Subject,"revocation and local sign-out preserve identity mapping");
                issuer.Login(plan); issuer.RevocationOffline=true;
                Check(!plan.SignOut(issuer.Client,CancellationToken.None) && !store.Read().Active.Connected,"offline sign-out still clears local tokens"); issuer.RevocationOffline=false;
                issuer.Client="test_client_two"; issuer.Subject="test_subject_two"; issuer.Scope="openid profile email resource.invoke";
                PlanAccount restricted=issuer.Login(plan); Check(!restricted.Connected && restricted.Subject==issuer.Subject,"identity retained without plan permission");
                Reject(()=>plan.Access(CancellationToken.None),"login_required");
                store.Select(account.ClientId); Check(store.Read().Active.Key==account.Key,"explicit account selection");
            }
            ProcessStartInfo start=new ProcessStartInfo { UseShellExecute=false };
            foreach(string key in new[]{"OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_BASE_URL"}) start.EnvironmentVariables[key]="fixture-other-billing";
            CodexPlan.Environment(start,new PlanAccount { AccessToken="fixture-plan-only" });
            Check(start.EnvironmentVariables[CodexPlan.TokenVariable]=="fixture-plan-only" && !start.EnvironmentVariables.ContainsKey("OPENAI_API_KEY") && !start.EnvironmentVariables.ContainsKey("CODEX_API_KEY") && !start.EnvironmentVariables.ContainsKey("CODEX_ACCESS_TOKEN") && !start.EnvironmentVariables.ContainsKey("OPENAI_BASE_URL"),"only explicit OAuth bearer, no API billing fallback");
            string options=string.Join(" ",CodexPlan.Options("disabled"));
            Check(options.Contains("https://api.openai.com/v1") && options.Contains("requires_openai_auth=false") && options.Contains("supports_websockets=false") && options.Contains("mcp_servers={}") && !options.Contains("forced_login_method"),"official public Responses provider and independent authentication");
            PlanRecoveryDiagnostics.Run(directory,report);
            report["checks"]="PKCE / callback state + registration / RS256 signature + claims / loopback auth exchange / scope gating / DPAPI atomic storage / refresh serialization + rotation / transient and terminal errors / revocation / account selection / API-key exclusion";
        } finally { if(Directory.Exists(directory) && directory.StartsWith(temp.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory,true); }
    }
    internal static void Transport(string root,Settings settings,Dictionary<string,object> report) {
        using(var deadline=new CancellationTokenSource(30000)) using(var client=new CodexClient(AssistantService.FindCodex(settings.CodexPath),root,deadline.Token)) {
            object config=JsonData.Get(client.Call("config/read",new { includeLayers=false,cwd=root },deadline.Token),"config");
            Check(JsonData.Text(config,"model_provider")=="minus700k_chatgpt_plan","explicit provider");
            Check(Path.GetFullPath(JsonData.Text(config,"sqlite_home"))==Path.GetFullPath(client.StorageHome),"native database uses this request's temporary home");
            Check(Path.GetFullPath(JsonData.Text(config,"log_dir"))==Path.GetFullPath(Path.Combine(client.StorageHome,"log")),"native logs use this request's guarded temporary home");
            var overrides=client.QuestionConfig(root,deadline.Token);
            Check(JsonData.Object(overrides["mcp_servers"]).Count==JsonData.Object(JsonData.Get(config,"mcp_servers")).Count,"all inherited MCP servers disabled");
            object result=client.Call("thread/start",new { model="gpt-5.5",modelProvider="minus700k_chatgpt_plan",cwd=root,approvalPolicy="never",sandbox="read-only",ephemeral=true,config=overrides },deadline.Token);
            Guid id; Check(Guid.TryParse(JsonData.Text(JsonData.Get(result,"thread"),"id"),out id),"native app-server initialized with plan provider");
            object servers=client.Call("mcpServerStatus/list",new { threadId=id.ToString(),limit=100 },deadline.Token);
            report["mcpStates"]=JsonData.Items(JsonData.Get(servers,"data")).Select(s=>new { runtimeStatus=JsonData.Get(s,"runtimeStatus"),tools=JsonData.Object(JsonData.Get(s,"tools")).Count }).ToArray();
            Check(JsonData.Items(JsonData.Get(servers,"data")).All(s=>JsonData.Object(JsonData.Get(s,"tools")).Count==0 && JsonData.Text(s,"runtimeStatus")=="disabled"),"question thread exposes no active MCP servers or tools");
            report["checks"]="native stdio initialize / custom plan provider / ephemeral thread start / no inherited MCP servers / no inference or account required";
        }
    }
    internal static void Status(Dictionary<string,object> report) {
        PlanState state=PlanStore.Default.Read(); PlanAccount active=state.Active;
        report["registeredAccounts"]=state.Accounts.Count;
        report["connected"]=active!=null && active.Connected;
        report["planPermission"]=active!=null && active.Permitted;
        report["expiresSoon"]=active!=null && active.ExpiresAt<ChatGptPlan.Now+120;
        report["model"]=active==null?"":active.Model;
    }
}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Minus700K {
internal static class ModelSettingsDiagnostics {
    static void Check(bool value,string message) { if(!value) throw new Exception("model settings: "+message); }
    static IEnumerable<Control> Descendants(Control parent) { foreach(Control child in parent.Controls) { yield return child; foreach(Control nested in Descendants(child)) yield return nested; } }
    static T Field<T>(object target,string name) { return (T)target.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(target); }
    static Button SaveButton(SettingsForm form) { return Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用"); }
    static string Selected(PlanConnectionPanel panel) { var model=Field<ComboBox>(panel,"models").SelectedItem as PlanModel; return model==null?null:model.Slug; }
    static void Select(PlanConnectionPanel panel,string slug) {
        ComboBox models=Field<ComboBox>(panel,"models");
        for(int i=0;i<models.Items.Count;i++) if(((PlanModel)models.Items[i]).Slug==slug) { models.SelectedIndex=i; return; }
        throw new Exception("Fixture model not in selector: "+slug);
    }
    static void LoadModels(Fixture fixture) {
        int calls=fixture.CatalogCalls;
        typeof(PlanConnectionPanel).GetMethod("LoadModels",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(fixture.Panel,null);
        Stopwatch timeout=Stopwatch.StartNew();
        while(fixture.Panel.IsBusy && timeout.ElapsedMilliseconds<5000) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
        Check(!fixture.Panel.IsBusy && fixture.CatalogCalls==calls+1,"mock catalog retrieval completed");
        var models=Field<ComboBox>(fixture.Panel,"models");
        Check(models.Items.Cast<PlanModel>().Any(model=>model.Slug=="model-b"),"mock catalog populates model choices");
    }
    sealed class Fixture : IDisposable {
        internal readonly string Root,ConfigPath,CredentialPath;
        internal readonly Settings Settings;
        internal readonly PlanStore Store;
        internal readonly PlanConnectionPanel Panel;
        internal readonly SettingsForm Form;
        internal int CatalogCalls;
        internal Fixture(string work,string name,string model="model-a") {
            Root=Path.Combine(work,name); Directory.CreateDirectory(Root);
            File.WriteAllBytes(Path.Combine(Root,"model.bin"),new byte[]{1});
            Settings=new Settings { WhisperModel="model.bin",KeepRequestFiles=false,SpeakerDevice="" };
            ConfigPath=Path.Combine(Root,"config.json"); File.WriteAllText(ConfigPath,JsonData.Serializer().Serialize(Settings),new UTF8Encoding(false));
            Store=new PlanStore(Path.Combine(Root,"connection")); CredentialPath=Path.Combine(Root,"connection","chatgpt.dat");
            Store.Locked(delegate {
                var account=new PlanAccount { ClientId="fixture-client",Subject="fixture-subject",AccessToken="fixture-access",RefreshToken="fixture-refresh",Scopes="resource.invoke chatgpt.tokens.use.direct",ExpiresAt=ChatGptPlan.Now+3600,Model=model };
                Store.SaveUnlocked(new PlanState { ActiveClientId=account.ClientId,Accounts=new List<PlanAccount> { account } }); return true;
            });
            var plan=new ChatGptPlan(Store,delegate(string url,string method,string body,string bearer,CancellationToken token) {
                token.ThrowIfCancellationRequested();
                Check(url==PlanHttp.Resource+"/models" && method=="GET","only the mocked model catalog may be requested");
                Check(!string.IsNullOrEmpty(bearer),"fixture model request carries the fixture token");
                Interlocked.Increment(ref CatalogCalls);
                var response=new { models=new[] { new { slug="model-a",display_name="Fixture A",visibility="list" },new { slug="model-b",display_name="Fixture B",visibility="list" } } };
                return JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(response));
            });
            Panel=new PlanConnectionPanel(Settings,false,plan,Store,delegate { throw new Exception("Browser must not open in model settings diagnostics."); });
            Form=new SettingsForm(Root,Settings,null,5,true,Panel);
            IntPtr unused=Form.Handle; // Install a WinForms context without showing a window.
        }
        internal void Mutate(Action<PlanState> action) { Store.Locked(delegate { PlanState state=Store.ReadUnlocked(); action(state); Store.SaveUnlocked(state); return true; }); }
        internal PlanAccount Account { get { return Store.Read().Active; } }
        public void Dispose() { Form.Dispose(); }
    }
    internal static void Run(string root,Dictionary<string,object> report) {
        Application.EnableVisualStyles();
        string work=Path.Combine(Path.GetTempPath(),"Minus700K-model-settings-"+Guid.NewGuid().ToString("N").Substring(0,12)); Directory.CreateDirectory(work);
        var checks=new List<string>(); int catalogCalls=0;
        using(var fixture=new Fixture(work,"model-only")) {
            byte[] config=File.ReadAllBytes(fixture.ConfigPath),credentials=File.ReadAllBytes(fixture.CredentialPath);
            IntPtr handle=fixture.Form.Handle; int events=0,prepared=0,commits=0,released=0;
            fixture.Panel.ModelSelectionChanged+=delegate { events++; };
            fixture.Form.BeginApply=delegate { return null; }; fixture.Form.EndApply=delegate { released++; };
            fixture.Form.PrepareApply=delegate { prepared++; return new SettingsRuntimeUpdate(delegate { commits++; },delegate { throw new Exception("Successful apply must not roll back."); }); };
            Check(!fixture.Panel.HasModelChanges && !SaveButton(fixture.Form).Enabled,"opening a settings form starts clean");
            LoadModels(fixture);
            Check(!fixture.Panel.HasModelChanges && !SaveButton(fixture.Form).Enabled && Selected(fixture.Panel)=="model-a","loading catalog retains the saved model without dirtying the parent");
            Check(credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)),"catalog loading never saves the model");
            Select(fixture.Panel,"model-b");
            Check(fixture.Panel.HasModelChanges && SaveButton(fixture.Form).Enabled && events>0,"selection marks the common save button dirty");
            Check(fixture.Account.Model=="model-a" && credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)) && config.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)),"selection changes only the draft");
            Check(!Descendants(fixture.Form).OfType<Button>().Any(button=>button.Text=="選択したモデルを保存"),"dedicated model save button is absent");
            LoadModels(fixture);
            Check(fixture.Panel.HasModelChanges && Selected(fixture.Panel)=="model-b" && SaveButton(fixture.Form).Enabled && fixture.Account.Model=="model-a","refreshing catalog preserves the unsaved selection");
            Check(fixture.Form.SaveAndApply(),"common save accepts a model-only change");
            Check(fixture.Account.Model=="model-b" && config.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)),"model-only apply updates the account and leaves config bytes intact");
            Check(fixture.Form.Handle==handle && !fixture.Form.IsDisposed && fixture.Form.Saved && !fixture.Panel.HasModelChanges && !SaveButton(fixture.Form).Enabled,"model-only save keeps the same window and resets dirty state");
            Check(prepared==1 && commits==1 && released==1,"model-only save runs resident preparation and commit");
            Select(fixture.Panel,"model-a"); ((NumericUpDown)fixture.Form.Inputs["SpeechVolume"]).Value=44;
            Check(fixture.Form.SaveAndApply() && fixture.Account.Model=="model-a" && Settings.Load(fixture.Root).SpeechVolume==44,"one save applies model and ordinary options together");
            Check(!SaveButton(fixture.Form).Enabled && prepared==2 && commits==2 && released==2,"combined save refreshes both baselines");
            catalogCalls+=fixture.CatalogCalls;
        }
        checks.Add("catalog does not save / selection is a draft / common save / refresh preserves draft / model-only config bytes / shared option save / same window");

        using(var fixture=new Fixture(work,"runtime-failure")) {
            LoadModels(fixture); Select(fixture.Panel,"model-b"); ((NumericUpDown)fixture.Form.Inputs["SpeechVolume"]).Value=42;
            byte[] before=File.ReadAllBytes(fixture.ConfigPath); IntPtr handle=fixture.Form.Handle;
            bool fail=true; int rollbacks=0,commits=0,reservations=0,releases=0;
            fixture.Form.BeginApply=delegate { reservations++; return null; }; fixture.Form.EndApply=delegate { releases++; };
            fixture.Form.PrepareApply=delegate {
                return new SettingsRuntimeUpdate(delegate {
                    commits++; Check(fixture.Store.ReadUnlocked().Active.Model=="model-b","runtime callback sees the pending account model");
                    if(fail) {
                        PlanState latest=fixture.Store.ReadUnlocked(); latest.Active.AccessToken="rotated-fixture-access"; latest.Active.RefreshToken="rotated-fixture-refresh"; fixture.Store.SaveUnlocked(latest);
                        throw new InvalidOperationException("fixture resident commit failure");
                    }
                },delegate { rollbacks++; });
            };
            Check(!fixture.Form.SaveAndApply() && !fixture.Form.Saved,"resident failure rejects the combined save");
            Check(before.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)) && fixture.Account.Model=="model-a","resident failure rolls back config and model");
            Check(fixture.Account.AccessToken=="rotated-fixture-access" && fixture.Account.RefreshToken=="rotated-fixture-refresh","model rollback preserves newly rotated credentials");
            Check(fixture.Panel.HasModelChanges && Selected(fixture.Panel)=="model-b" && SaveButton(fixture.Form).Enabled && fixture.Form.Draft.SpeechVolume==42,"failed save preserves the user's draft");
            Check(fixture.Form.Handle==handle && !fixture.Form.IsDisposed && rollbacks==1 && reservations==releases,"failed apply retains the window and releases resident reservation");
            fail=false;
            Check(fixture.Form.SaveAndApply() && fixture.Account.Model=="model-b" && Settings.Load(fixture.Root).SpeechVolume==42,"retry applies both pending changes");
            Check(!fixture.Panel.HasModelChanges && !SaveButton(fixture.Form).Enabled && commits==2 && rollbacks==1 && reservations==releases,"successful retry clears dirty state and commits exactly once more");
            catalogCalls+=fixture.CatalogCalls;
        }
        checks.Add("runtime failure restores both stores / rotated tokens preserved / draft retained / same-window retry");

        using(var fixture=new Fixture(work,"invalid-options")) {
            LoadModels(fixture); Select(fixture.Panel,"model-b"); ((TextBox)fixture.Form.Inputs["PushToTalkKey"]).Text=fixture.Form.Draft.ReplayKey;
            byte[] config=File.ReadAllBytes(fixture.ConfigPath),credentials=File.ReadAllBytes(fixture.CredentialPath);
            Check(!fixture.Form.SaveAndApply() && fixture.Account.Model=="model-a","invalid ordinary option cannot save the model");
            Check(config.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)) && credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)) && fixture.Panel.HasModelChanges,"validation failure keeps both files and draft intact");
            catalogCalls+=fixture.CatalogCalls;
        }
        using(var fixture=new Fixture(work,"discard")) {
            LoadModels(fixture); Select(fixture.Panel,"model-b"); ((NumericUpDown)fixture.Form.Inputs["SpeechVolume"]).Value=40;
            byte[] config=File.ReadAllBytes(fixture.ConfigPath),credentials=File.ReadAllBytes(fixture.CredentialPath);
            fixture.Form.Close();
            Check(fixture.Account.Model=="model-a" && config.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)) && credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)),"closing without save discards model and option drafts");
            catalogCalls+=fixture.CatalogCalls;
        }
        checks.Add("invalid options block model changes / closing without saving preserves both files");

        foreach(bool accountChanged in new[]{true,false}) using(var fixture=new Fixture(work,accountChanged?"account-conflict":"model-conflict")) {
            LoadModels(fixture); Select(fixture.Panel,"model-b"); ((NumericUpDown)fixture.Form.Inputs["SpeechVolume"]).Value=46;
            byte[] config=File.ReadAllBytes(fixture.ConfigPath);
            fixture.Mutate(delegate(PlanState state) { if(accountChanged) state.Active.Subject="changed-fixture-subject"; else state.Active.Model="external-model"; });
            byte[] credentials=File.ReadAllBytes(fixture.CredentialPath);
            Check(!fixture.Form.SaveAndApply(),"external account or model changes reject stale drafts");
            Check(config.SequenceEqual(File.ReadAllBytes(fixture.ConfigPath)) && credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)),"stale account or model cannot be overwritten and config is rolled back");
            Check(fixture.Panel.HasModelChanges && SaveButton(fixture.Form).Enabled,"conflict retains pending edits for review");
            catalogCalls+=fixture.CatalogCalls;
        }
        checks.Add("account-key conflict / external model conflict / config rollback");

        foreach(string model in new[]{"","outside-catalog"}) using(var fixture=new Fixture(work,model.Length==0?"automatic":"unlisted",model)) {
            byte[] credentials=File.ReadAllBytes(fixture.CredentialPath);
            Check(Selected(fixture.Panel)==model && !fixture.Panel.HasModelChanges,"saved automatic or unlisted model is displayed exactly");
            LoadModels(fixture);
            Check(Selected(fixture.Panel)==model && !fixture.Panel.HasModelChanges && !SaveButton(fixture.Form).Enabled && credentials.SequenceEqual(File.ReadAllBytes(fixture.CredentialPath)),"catalog loading does not replace automatic or unlisted saved model with its first entry");
            catalogCalls+=fixture.CatalogCalls;
        }
        checks.Add("automatic and unlisted saved models remain unchanged");
        report["fixtureRoot"]=work; report["checks"]=checks; report["mockCatalogCalls"]=catalogCalls;
        report["networkRequests"]=0; report["recordings"]=0;
    }
}
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Windows.Forms;

namespace Minus700K {
internal static class LanguageSwitchDiagnostics {
    static int checks;
    static readonly string[] TextFields={"CustomInstructionsText","WebSearchTriggersText","ApplicationInstructionsText"};
    static readonly string[] TextKeys={"defaults.customInstructions","defaults.searchTriggers","defaults.applicationInstructions"};
    static void Check(bool condition,string description) { checks++; if(!condition) throw new Exception("language synchronization: "+description); }
    static IEnumerable<Control> Descendants(Control control) { foreach(Control child in control.Controls) { yield return child; foreach(var nested in Descendants(child)) yield return nested; } }
    static SettingsCard Card(Control control) { while(control!=null && !(control is SettingsCard)) control=control.Parent; return (SettingsCard)control; }
    static void Choose(SettingsForm form,string field,string value) { var combo=(ComboBox)form.Inputs[field]; combo.SelectedItem=combo.Items.Cast<SettingsChoice>().Single(choice=>object.Equals(choice.Value,value)); }
    static string Value(Settings settings,string field) { return (string)typeof(Settings).GetField(field).GetValue(settings); }
    static void SetValue(Settings settings,string field,string value) { typeof(Settings).GetField(field).SetValue(settings,value); }
    static void WriteConfig(string fixture,Settings value) { File.WriteAllText(Path.Combine(fixture,"config.json"),SettingsStorage.Serializer().Serialize(value),new UTF8Encoding(false)); }
    static Settings Fresh(string language="ja") {
        var result=new Settings { WhisperModel="model.bin",AppLanguage=language,Language=language,SpeechVoice="fixture-explicit-voice" };
        for(int i=0;i<TextFields.Length;i++) SetValue(result,TextFields[i],L10n.For(language).Text(TextKeys[i]));
        return result;
    }
    static void Defaults(Settings value,string language,string stage) {
        Check(value.AppLanguage==language && value.Language==language && value.SpeechVoice=="",stage+": language and audio settings");
        for(int i=0;i<TextFields.Length;i++) Check(Value(value,TextFields[i])==L10n.For(language).Text(TextKeys[i]),stage+": "+TextFields[i]);
    }
    static void Editors(SettingsForm form,string language,string stage) {
        for(int i=0;i<TextFields.Length;i++) Check(form.Inputs[TextFields[i]].Text==SettingsTheme.EditorText(L10n.For(language).Text(TextKeys[i])),stage+": editor "+TextFields[i]);
    }
    static void SameText(Settings expected,Settings actual,string stage) {
        foreach(string field in TextFields) Check(Value(actual,field)==Value(expected,field),stage+": "+field);
    }
    internal static void Run(string root,Dictionary<string,object> report) {
        checks=0; string previous=L10n.Current.Language;
        string parent=Path.GetFullPath(Path.Combine(root,"runtime","language-switch-test-work"));
        string fixture=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
        Check(fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture containment");
        Directory.CreateDirectory(fixture); File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1});
        try {
            Application.EnableVisualStyles();
            CheckDefaultRecognition();
            CheckRoundTrip(fixture);
            CheckCancelAndRollback(fixture);
            CheckCustomAndDisabled(fixture);
            CheckUnsavedEdits(fixture);
            CheckManualOverrides(fixture);
            CheckInstalledVoices(report);
            report["languageSwitchChecks"]=checks; report["languageSwitchFixture"]=fixture;
        } finally { L10n.SetLanguage(previous); }
    }
    static void CheckDefaultRecognition() {
        foreach(string source in new[]{"ja","en"}) {
            string target=source=="ja"?"en":"ja";
            foreach(string newline in new[]{"\n","\r\n","\r"}) {
                var value=Fresh(source);
                for(int i=0;i<TextFields.Length;i++) SetValue(value,TextFields[i],L10n.For(source).Text(TextKeys[i]).Replace("\r\n","\n").Replace("\n",newline));
                LocalizedDefaults.ApplyLanguage(value,target); Defaults(value,target,source+" newline normalization");
            }
            var defaults=Fresh(source); defaults.CustomInstructionsEnabled=false; defaults.WebSearchTriggersEnabled=false;
            LocalizedDefaults.ApplyLanguage(defaults,target); Defaults(defaults,target,"disabled shipped defaults");
            Check(!defaults.CustomInstructionsEnabled && !defaults.WebSearchTriggersEnabled,"language switch preserves disabled flags");
            var modified=Fresh(source);
            modified.WebSearchTriggersText=modified.WebSearchTriggersText.Replace("\r\n","");
            modified.CustomInstructionsText=modified.CustomInstructionsText.Replace("\r\n","");
            string keywords=modified.WebSearchTriggersText,instructions=modified.CustomInstructionsText;
            LocalizedDefaults.ApplyLanguage(modified,target);
            Check(modified.WebSearchTriggersText==keywords && modified.CustomInstructionsText==instructions,"removed line breaks are user edits, not shipped defaults");
            var split=Fresh(source); split.WebSearchTriggersText=split.WebSearchTriggersText.Insert(2,"\n");
            string splitText=split.WebSearchTriggersText; LocalizedDefaults.ApplyLanguage(split,target);
            Check(split.WebSearchTriggersText==splitText,"inserted keyword line break is retained");
        }
        string ja=L10n.For("ja").Text("defaults.applicationInstructions"); int firstBreak=ja.IndexOf("\r\n",StringComparison.Ordinal);
        var legacy=Fresh(); legacy.ApplicationInstructionsText=ja.Remove(firstBreak,2);
        LocalizedDefaults.ApplyLanguage(legacy,"en"); Check(legacy.ApplicationInstructionsText==L10n.For("en").Text("defaults.applicationInstructions"),"known older application-instruction layout follows language");
    }
    static void CheckRoundTrip(string fixture) {
        var active=Fresh(); WriteConfig(fixture,active); string initial=SettingsStorage.Snapshot(active); byte[] baseline=File.ReadAllBytes(Path.Combine(fixture,"config.json"));
        using(var form=new SettingsForm(fixture,active,null,0,true)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Application.DoEvents();
            IntPtr handle=form.Handle; var controls=TextFields.Select(field=>form.Inputs[field]).ToArray();
            var engine=Card(form.Inputs["Language"]); var application=Card(form.Inputs["ApplicationInstructionsText"]);
            form.PrepareApply=value=>new SettingsRuntimeUpdate(delegate { active=SettingsStorage.Clone(value); },delegate { });
            Choose(form,"AppLanguage","en");
            Defaults(form.Draft,"en","unsaved English draft"); Editors(form,"en","unsaved English draft");
            Check(SettingsStorage.Snapshot(active)==initial && baseline.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"config.json"))) && L10n.Current.Language=="ja","draft switch leaves live state, disk, and current UI language unchanged");
            Check(engine.Parent==null && application.Parent==null && TextFields.Select((field,index)=>object.ReferenceEquals(form.Inputs[field],controls[index])).All(value=>value),"draft switch preserves detached advanced cards and editor objects");
            Check(form.SaveAndApply(),"save English switch"); Defaults(Settings.Load(fixture),"en","English disk"); Defaults(active,"en","English active settings");
            Check(form.Handle==handle && L10n.Current.Language=="en","successful save applies language in the same window");
            form.SelectTab(6); Descendants(form).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle").PerformClick(); Application.DoEvents();
            Check(engine.Visible && application.Visible,"hidden advanced controls can be revealed after synchronization");
            Choose(form,"AppLanguage","ja"); Defaults(form.Draft,"ja","Japanese draft return"); Editors(form,"ja","Japanese draft return");
            Check(form.SaveAndApply(),"save Japanese return"); Defaults(Settings.Load(fixture),"ja","Japanese disk"); Defaults(active,"ja","Japanese active settings");
            Check(form.Handle==handle && engine.Visible && application.Visible,"revealed advanced cards stay open across language return");
            form.Close();
        }
        foreach(string next in new[]{"en","ja"}) {
            var current=Settings.Load(fixture);
            using(var reopened=new SettingsForm(fixture,current,null,0,true)) {
                Defaults(reopened.Draft,current.AppLanguage,"reopened saved defaults"); Editors(reopened,current.AppLanguage,"reopened saved defaults");
                Choose(reopened,"AppLanguage",next); Check(reopened.SaveAndApply(),"switch from reopened window to "+next);
            }
            Defaults(Settings.Load(fixture),next,"reopened round trip disk");
        }
    }
    static void CheckCancelAndRollback(string fixture) {
        var active=Fresh(); WriteConfig(fixture,active); byte[] before=File.ReadAllBytes(Path.Combine(fixture,"config.json")); string snapshot=SettingsStorage.Snapshot(active);
        using(var canceled=new SettingsForm(fixture,active,null,0,true)) { Choose(canceled,"AppLanguage","en"); Defaults(canceled.Draft,"en","canceled draft"); canceled.Close(); }
        Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"config.json"))) && SettingsStorage.Snapshot(active)==snapshot && L10n.Current.Language=="ja","cancel discards synchronized draft without side effects");
        using(var form=new SettingsForm(fixture,active,null,0,true)) {
            Choose(form,"AppLanguage","en"); bool fail=true; int rollback=0;
            form.PrepareApply=value=>new SettingsRuntimeUpdate(delegate { if(fail) throw new InvalidOperationException("fixture apply failure"); active=SettingsStorage.Clone(value); },delegate { rollback++; });
            Check(!form.SaveAndApply() && rollback==1,"runtime failure reaches rollback");
            Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"config.json"))) && SettingsStorage.Snapshot(active)==snapshot && L10n.Current.Language=="ja","failed save restores disk and leaves live language unchanged");
            Defaults(form.Draft,"en","failed save keeps synchronized draft for retry"); Editors(form,"en","failed save keeps editor values");
            fail=false; Check(form.SaveAndApply(),"retry synchronized save"); Defaults(active,"en","retry live settings"); Defaults(Settings.Load(fixture),"en","retry disk settings");
        }
    }
    static void CheckCustomAndDisabled(string fixture) {
        for(int scenario=0;scenario<3;scenario++) {
            var original=Fresh(); original.CustomInstructionsEnabled=false; original.WebSearchTriggersEnabled=false;
            if(scenario==0) { original.CustomInstructionsText="  User instruction.\n日本語も保持。\r\n"; original.WebSearchTriggersText=""; }
            if(scenario==1) { original.CustomInstructionsText=""; original.WebSearchTriggersText="my keyword\n別のキーワード\r\n"; original.ApplicationInstructionsText="My app rule.\n別の指定。"; }
            if(scenario==2) original.ApplicationInstructionsText="";
            WriteConfig(fixture,original); var expected=ExpectedMixed(original,scenario,"en");
            using(var form=new SettingsForm(fixture,original,null,0,true)) {
                Choose(form,"AppLanguage","en"); SameText(expected,form.Draft,"mixed custom draft "+scenario);
                Check(!form.Draft.CustomInstructionsEnabled && !form.Draft.WebSearchTriggersEnabled,"OFF flags retained in mixed draft "+scenario);
                Check(form.SaveAndApply(),"save mixed custom scenario "+scenario); SameText(expected,Settings.Load(fixture),"mixed custom disk "+scenario);
            }
            using(var reopened=new SettingsForm(fixture,Settings.Load(fixture),null,0,true)) {
                SameText(expected,reopened.Draft,"mixed custom reopened "+scenario);
                var back=ExpectedMixed(original,scenario,"ja");
                Choose(reopened,"AppLanguage","ja"); SameText(back,reopened.Draft,"mixed custom back draft "+scenario);
                Check(reopened.SaveAndApply(),"save mixed custom return "+scenario); SameText(back,Settings.Load(fixture),"mixed custom back disk "+scenario);
            }
        }
    }
    static Settings ExpectedMixed(Settings original,int scenario,string language) {
        // These expectations describe the fixtures independently of the helper
        // under test, so accidental replacement of custom/empty text is detected.
        var expected=SettingsStorage.Clone(original);
        expected.AppLanguage=language; expected.Language=language; expected.SpeechVoice="";
        var locale=L10n.For(language);
        if(scenario==0) expected.ApplicationInstructionsText=locale.Text("defaults.applicationInstructions");
        if(scenario==2) {
            expected.CustomInstructionsText=locale.Text("defaults.customInstructions");
            expected.WebSearchTriggersText=locale.Text("defaults.searchTriggers");
        }
        return expected;
    }
    static void CheckUnsavedEdits(string fixture) {
        var original=Fresh(); WriteConfig(fixture,original);
        using(var form=new SettingsForm(fixture,original,null,0,true)) {
            foreach(string field in TextFields) ((TextBox)form.Inputs[field]).Text="Unsubmitted "+field+"\r\n日本語の編集を保持。";
            var expected=form.Draft;
            Choose(form,"AppLanguage","en"); SameText(expected,form.Draft,"unsaved text survives English switch");
            Choose(form,"AppLanguage","ja"); SameText(expected,form.Draft,"unsaved text survives Japanese switch");
            Check(form.SaveAndApply(),"save unsaved edits after round trip"); SameText(expected,Settings.Load(fixture),"unsaved text survives save");
        }
        original=Fresh(); original.WebSearchTriggersText=SearchTriggers.DefaultText.Replace("\r\n",""); WriteConfig(fixture,original);
        using(var form=new SettingsForm(fixture,original,null,0,true)) {
            Choose(form,"AppLanguage","en"); Check(form.Draft.WebSearchTriggersText==original.WebSearchTriggersText,"single joined keyword is not mistaken for multiline shipped keywords");
            Check(form.SaveAndApply() && Settings.Load(fixture).WebSearchTriggersText==original.WebSearchTriggersText,"joined custom keyword survives UI and disk");
        }
    }
    static void CheckManualOverrides(string fixture) {
        var original=Fresh(); original.Language="auto"; WriteConfig(fixture,original); byte[] before=File.ReadAllBytes(Path.Combine(fixture,"config.json"));
        using(var form=new SettingsForm(fixture,original,null,0,true)) {
            Check(form.Draft.Language=="auto" && form.Draft.SpeechVoice==original.SpeechVoice && before.SequenceEqual(File.ReadAllBytes(Path.Combine(fixture,"config.json"))),"opening does not resynchronize manual audio settings");
            Choose(form,"AppLanguage","ja"); Check(form.Draft.Language=="auto" && form.Draft.SpeechVoice==original.SpeechVoice,"reselecting current language leaves manual audio settings intact");
            ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=31; Check(form.SaveAndApply(),"unrelated save with manual audio settings");
            Check(Settings.Load(fixture).Language=="auto" && Settings.Load(fixture).SpeechVoice==original.SpeechVoice,"unrelated save does not resynchronize audio");
            Choose(form,"AppLanguage","en"); Defaults(form.Draft,"en","actual language change resets audio");
            Choose(form,"Language","ja"); Choose(form,"SpeechVoice",original.SpeechVoice);
            foreach(string field in TextFields) ((TextBox)form.Inputs[field]).Text="Manual change after language selection: "+field;
            var manual=form.Draft; Choose(form,"AppLanguage","en");
            SameText(manual,form.Draft,"reselect preserves post-switch manual text");
            Check(form.Draft.Language=="ja" && form.Draft.SpeechVoice==original.SpeechVoice,"reselect preserves post-switch manual audio");
            Check(form.SaveAndApply(),"save manual overrides after language change");
            var saved=Settings.Load(fixture); SameText(manual,saved,"manual overrides disk"); Check(saved.AppLanguage=="en" && saved.Language=="ja" && saved.SpeechVoice==original.SpeechVoice,"manual speech overrides persist after language change");
        }
        var persisted=Settings.Load(fixture);
        using(var reopened=new SettingsForm(fixture,persisted,null,0,true)) {
            SameText(persisted,reopened.Draft,"open preserves manually edited text");
            Check(reopened.Draft.Language==persisted.Language && reopened.Draft.SpeechVoice==persisted.SpeechVoice,"reopen preserves post-switch manual audio");
            ((NumericUpDown)reopened.Inputs["SpeechVolume"]).Value=32; Check(reopened.SaveAndApply(),"save unrelated value after reopening overrides");
            var after=Settings.Load(fixture); SameText(persisted,after,"unrelated save preserves manually edited text");
            Check(after.Language==persisted.Language && after.SpeechVoice==persisted.SpeechVoice,"unrelated save preserves post-switch manual audio");
        }
    }
    static void CheckInstalledVoices(Dictionary<string,object> report) {
        var selected=new Dictionary<string,object>();
        using(var installed=new SpeechSynthesizer()) {
            var enabled=installed.GetInstalledVoices().Where(voice=>voice.Enabled).Select(voice=>voice.VoiceInfo).ToArray();
            Check(enabled.Length>0,"installed enabled speech voices are available");
            foreach(string language in new[]{"ja","en"}) {
                var matches=enabled.Where(voice=>voice.Culture.TwoLetterISOLanguageName==language).ToArray();
                using(var automatic=Speech.Create(new Settings { AppLanguage=language,SpeechVoice="" })) {
                    Check(enabled.Any(voice=>voice.Name==automatic.Voice.Name),"automatic "+language+" voice is installed and enabled");
                    if(matches.Length>0) Check(matches.Any(voice=>voice.Name==automatic.Voice.Name),"automatic "+language+" voice matches requested language");
                    selected[language]=new { name=automatic.Voice.Name,culture=automatic.Voice.Culture.Name,languageVoiceInstalled=matches.Length>0 };
                }
            }
            string explicitName=enabled[0].Name;
            using(var manual=Speech.Create(new Settings { AppLanguage="en",SpeechVoice=explicitName }))
                Check(manual.Voice.Name==explicitName,"manual installed voice remains selectable after language synchronization");
        }
        // Construction selects and configures voices; this test never calls Speak.
        report["languageSwitchVoices"]=selected;
    }
}
}

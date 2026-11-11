using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Minus700K {
internal static class LocalizationDiagnostics {
    static void Check(bool condition,string description) { if(!condition) throw new Exception("localization: "+description); }
    static void Reject(Action action,string description) { try { action(); } catch(ArgumentException) { return; } throw new Exception("localization: expected rejection: "+description); }
    static IEnumerable<Control> Descendants(Control control) { foreach(Control child in control.Controls) { yield return child; foreach(var nested in Descendants(child)) yield return nested; } }
    static SettingsCard Card(Control control) { while(control!=null && !(control is SettingsCard)) control=control.Parent; return (SettingsCard)control; }
    static void Choose(ComboBox combo,string value) { combo.SelectedItem=combo.Items.Cast<SettingsChoice>().Single(choice=>object.Equals(choice.Value,value)); }
    static void WriteConfig(string fixture,object value) { File.WriteAllText(Path.Combine(fixture,"config.json"),SettingsStorage.Serializer().Serialize(value),new UTF8Encoding(false)); }
    internal static void Run(string root,Dictionary<string,object> report) {
        string oldLanguage=L10n.Current.Language;
        string parent=Path.GetFullPath(Path.Combine(root,"runtime","localization-test-work"));
        string fixture=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
        Check(fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture containment");
        Directory.CreateDirectory(fixture); File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1});
        try {
            L10n.SetLanguage("ja");
            Check(L10n.NormalizeLanguage("EN")=="en" && L10n.NormalizeLanguage("zh-CN")=="ja" && L10n.NormalizeLanguage("zh-Hans")=="ja" && L10n.NormalizeLanguage("zh")=="ja","canonical language codes and retired preview migration");
            Check(L10n.Languages.Select(value=>value.Code).SequenceEqual(new[]{"ja","en"}),"only Japanese and English are offered");
            foreach(string invalid in new[]{"","fr","../../ja",null}) Reject(()=>L10n.NormalizeLanguage(invalid),"invalid locale "+invalid);
            var original=new Settings { WhisperModel="model.bin",AppLanguage="ja",Language="en",SpeechVoice="fixture-explicit-voice" };
            var json=SettingsStorage.Serializer();
            var raw=json.Deserialize<Dictionary<string,object>>(json.Serialize(original)); raw.Remove("AppLanguage");
            WriteConfig(fixture,raw);
            Check(Settings.Load(fixture).AppLanguage=="ja" && Settings.Load(fixture).Language=="en","old config defaults to Japanese without changing recognition language");
            foreach(string language in new[]{"en","ja"}) {
                var edit=SettingsStorage.Open(fixture); var changed=SettingsStorage.Clone(edit.Value); LocalizedDefaults.ApplyLanguage(changed,language);
                edit.Save(changed);
                Settings loaded=Settings.Load(fixture);
                Check(loaded.AppLanguage==language && loaded.Language==language && loaded.SpeechVoice=="","explicit language change saves matching recognition language and automatic voice: "+language);
            }
            var invalidSettings=SettingsStorage.Clone(original); invalidSettings.AppLanguage="unsupported";
            Reject(()=>invalidSettings.Validate(),"settings validation rejects unsupported locale");
            raw["AppLanguage"]="zh-CN"; WriteConfig(fixture,raw);
            Check(Settings.Load(fixture).AppLanguage=="ja" && Settings.Load(fixture).CustomInstructionsText==original.CustomInstructionsText,"retired language loads in Japanese while preserving instructions");
            var aliasEdit=SettingsStorage.Open(fixture);
            Check(aliasEdit.Value.AppLanguage=="ja" && !aliasEdit.HasChanges(aliasEdit.Value),"editor migrates retired locale without creating dirty state");
            raw["AppLanguage"]=null; WriteConfig(fixture,raw); Reject(()=>Settings.Load(fixture),"explicit null locale rejected");

            var ja=L10n.For("ja"); int checkedResources=0;
            foreach(var language in L10n.Languages) {
                var locale=L10n.For(language.Code);
                foreach(string key in L10n.Keys) {
                    string value=locale.Text(key); Check(value!=null,"resource resolves: "+language.Code+" / "+key);
                    Check(locale.HasTranslation(key),"every shipped language has a translation: "+language.Code+" / "+key);
                    if(language.Code=="en" && !key.StartsWith("behavior.",StringComparison.Ordinal)) {
                        Check(!Regex.IsMatch(value,@"[ぁ-んァ-ヶ一-龯]"),"English resource has no untranslated Japanese: "+key);
                        var sourceArgs=Regex.Matches(ja.Text(key),@"(?<!\{)\{([0-9]+)(?:[^{}]*)\}(?!\})").Cast<Match>().Select(match=>int.Parse(match.Groups[1].Value)).Distinct().OrderBy(index=>index).ToArray();
                        var targetArgs=Regex.Matches(value,@"(?<!\{)\{([0-9]+)(?:[^{}]*)\}(?!\})").Cast<Match>().Select(match=>int.Parse(match.Groups[1].Value)).Distinct().OrderBy(index=>index).ToArray();
                        Check(sourceArgs.SequenceEqual(targetArgs),"format arguments preserved: "+key);
                        if(targetArgs.Length>0) locale.Format(key,Enumerable.Repeat((object)"fixture",targetArgs.Max()+1).ToArray());
                    }
                    checkedResources++;
                }
            }
            bool unknownRejected=false; try { ja.Text("diagnostics.unknown.key"); } catch(KeyNotFoundException) { unknownRejected=true; }
            Check(unknownRejected,"unknown source keys fail instead of leaking key names into UI");
            Check(L10n.For("en").Format("errors.hotkey.invalid","Ctrl+Fixture").Contains("Ctrl+Fixture"),"translated resource preserves parameter");
            var partial=new Localizer("en",new Dictionary<string,string>(),new Dictionary<string,string>{{"fixture.fallback","日本語の代替文"}});
            Check(partial.Text("fixture.fallback")=="日本語の代替文","future untranslated keys still fall back");
            Check(LocalizedDefaults.Resolve("defaults.applicationInstructions",ApplicationInstructions.DefaultText.Replace("\r\n",""),"en")==L10n.For("en").Text("defaults.applicationInstructions"),"old joined-line application default is recognized");
            Check(LocalizedDefaults.Resolve("defaults.customInstructions",CustomInstructions.DefaultText.Replace("\r\n","\n"),"en")==L10n.For("en").Text("defaults.customInstructions"),"LF-only default follows language");
            const string custom="  Keep my exact text.\n自分専用の指示。\r\n";
            foreach(string language in new[]{"en","ja"}) {
                Check(LocalizedDefaults.Resolve("defaults.customInstructions",custom,language)==custom,"custom text remains byte-for-byte: "+language);
                Check(LocalizedDefaults.Resolve("defaults.customInstructions","",language)=="","empty instructions remain empty");
                Check(new CustomInstructions(true,CustomInstructions.DefaultText,language).Read().Text==L10n.For(language).Text("defaults.customInstructions").Trim(),"shipped custom default follows language: "+language);
                Check(new CustomInstructions(false,custom,language).Read().Text=="","disabled instructions stay disabled");
            }
            var captured=L10n.For("en"); L10n.SetLanguage("ja");
            Check(captured.Language=="en" && captured.Text("prompts.answerLanguage").Contains("English"),"captured locale survives UI changes");
            var app=new ApplicationInfo { windowTitle="Localization fixture",executableName="fixture.exe",processId=7,WindowHandle=1 };
            foreach(string language in new[]{"ja","en"}) {
                Settings serviceSettings=SettingsStorage.Clone(Settings.Load(root)); serviceSettings.AppLanguage=language;
                serviceSettings.GameName="";
                serviceSettings.CustomInstructionsEnabled=true;
                serviceSettings.CustomInstructionsText=CustomInstructions.DefaultText; serviceSettings.ApplicationInstructionsText=ApplicationInstructions.DefaultText;
                var service=new AssistantService(root,serviceSettings); L10n.SetLanguage(language=="en"?"ja":"en");
                string prompt=service.BuildPrompt("Fixture question",app,false); var locale=L10n.For(language);
                Check(prompt.Contains(locale.Text("prompts.answerLanguage")) && prompt.Contains(locale.Text("defaults.customInstructions").Trim()),"request prompt captures selected language: "+language);
                Check(prompt.Contains(locale.Text("prompts.noImage")) && prompt.Contains("Fixture question"),"localized prompt and question retained: "+language);
                if(language=="en") Check(!Regex.IsMatch(prompt,@"[ぁ-んァ-ヶ一-龯]"),"English default request contains no Japanese instructions");
                if(language!="ja") Check(!prompt.Contains(CustomInstructions.DefaultText.Trim()),"Japanese default language request is replaced: "+language);
                serviceSettings.CustomInstructionsText=custom;
                var customService=new AssistantService(root,serviceSettings);
                Check(customService.BuildPrompt("Fixture",app,false).Contains(custom.Trim()),"user instructions are not translated: "+language);
            }
            Check(L10n.For("en").Text("prompts.codexBase")!=ja.Text("prompts.codexBase") && !ja.Text("prompts.codexBase").Contains("日本語で"),"base instructions are translated without forcing Japanese");
            var assessment=SearchFallback.Parse("{\"answer\":\"Answer\",\"status\":\"answered\",\"reason\":\"en fixture\"}",L10n.For("en"));
            Check(assessment.answer=="Answer" && assessment.status=="answered" && SearchFallback.Schema.Contains("needs_web"),"protocol fields remain language-independent");

            Check(LanguageBehavior.ForInput(new Settings { AppLanguage="en",Language="ja" }).Code=="ja","explicit recognition language overrides UI language");
            Check(LanguageBehavior.ForInput(new Settings { AppLanguage="en",Language="auto" }).Code=="en","automatic recognition uses app language for commands");
            Check(SearchPolicy.Decide("live","ネット検索はしないで",null,LanguageBehavior.For("ja")).reason=="voice_opt_out","Japanese explicit search opt-out retained");
            Check(LanguageBehavior.For("ja").CleanTranscription("(音楽) これは何ですか")=="これは何ですか","Japanese transcription annotations removed");
            Check(AssistantService.Spoken(new string('a',710),"en").EndsWith(".") && AssistantService.Spoken(new string('あ',710),"ja").EndsWith("。"),"speech truncation follows language");
            Check(AssistantService.Spoken("答えです。\n確認日：2026年11月9日\n出典：[資料](https://example.com)","en")=="答えです。","saved Japanese speech metadata removed after changing language");
            CheckSettingsUi(fixture,original);
            LanguageSwitchDiagnostics.Run(root,report);
            report["localizationResourcesChecked"]=checkedResources; report["localizationLanguages"]=L10n.Languages.Select(language=>language.Code).ToArray();
            report["localizationFixture"]=fixture;
        } finally { L10n.SetLanguage(oldLanguage); }
    }
    static void CheckSettingsUi(string fixture,Settings original) {
        Application.EnableVisualStyles(); WriteConfig(fixture,original); L10n.SetLanguage("ja");
        using(var form=new SettingsForm(fixture,original,null,0,true)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Application.DoEvents();
            IntPtr handle=form.Handle;
            var language=(ComboBox)form.Inputs["AppLanguage"];
            var engine=Card(form.Inputs["Language"]); var application=Card(form.Inputs["ApplicationInstructionsText"]);
            var input=form.Inputs["CustomInstructionsText"];
            Check(language.Items.Count==2 && engine.Parent==null && application.Parent==null,"language choices and hidden advanced cards");
            Choose(language,"en");
            Check(form.Draft.AppLanguage=="en" && Settings.Load(fixture).AppLanguage=="ja" && L10n.Current.Language=="ja","unsaved choice leaves active language and disk intact");
            form.PrepareApply=value=>new SettingsRuntimeUpdate(delegate { throw new InvalidOperationException("fixture apply failure"); },delegate { });
            Check(!form.SaveAndApply() && Settings.Load(fixture).AppLanguage=="ja" && L10n.Current.Language=="ja","failed runtime apply rolls back language");
            form.PrepareApply=null; Check(form.SaveAndApply(),"English language saves through settings UI");
            Check(form.Handle==handle && form.Text==L10n.For("en").Text("ui.settings.title") && Descendants(form).OfType<Button>().Any(button=>button.Text=="Save and apply"),"same settings window updates translated title and button");
            Check(Settings.Load(fixture).AppLanguage=="en" && form.AppliedSettings.AppLanguage=="en" && L10n.Current.Language=="en","saved language applied and reloadable");
            Check(engine.Parent==null && application.Parent==null && object.ReferenceEquals(input,form.Inputs["CustomInstructionsText"]) && input.Text==SettingsTheme.EditorText(L10n.For("en").Text("defaults.customInstructions")),"language switch updates default text without replacing hidden cards or editor controls");
            Check(form.Draft.Language=="en" && form.Draft.SpeechVoice=="","UI language synchronizes speech recognition and selects automatic voice");
            Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-en.png")); }
            foreach(int tab in Enumerable.Range(0,7)) {
                form.SelectTab(tab); Application.DoEvents();
                using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-en-tab-"+tab+".png")); }
            }
            form.SelectTab(6); Descendants(form).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle").PerformClick(); Application.DoEvents();
            Check(engine.Parent!=null && application.Parent!=null,"advanced cards remain usable after language refresh");
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-en-advanced.png")); }
            form.Size=new Size(920,650); form.SelectTab(0); Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-en-compact.png")); }
            form.SelectTab(6); Choose(language,"ja"); Check(form.SaveAndApply(),"Japanese language saves through same settings window");
            Check(form.Handle==handle && engine.Visible && application.Visible && form.Text==L10n.For("ja").Text("ui.settings.title"),"revealed advanced cards remain visible after second switch");
            form.SelectTab(0); Application.DoEvents();
            using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-ja.png")); }
            form.Close();
        }
        var english=Settings.Load(fixture); english.AppLanguage="en"; WriteConfig(fixture,english); L10n.SetLanguage("ja");
        using(var reopened=new SettingsForm(fixture,Settings.Load(fixture),null,0,true)) {
            Check(reopened.Text==L10n.For("en").Text("ui.settings.title") && Descendants(reopened).OfType<Button>().Any(button=>button.Text==L10n.For("en").Text("ui.save")),"first open uses saved language including initialized buttons");
        }
    }
}
}

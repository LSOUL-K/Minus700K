using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Minus700K {
internal static class SettingsUiDiagnostics {
    [StructLayout(LayoutKind.Sequential)] struct ScrollInfo { internal uint Size,Mask; internal int Minimum,Maximum; internal uint Page; internal int Position,Track; }
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")] static extern int SetScrollInfo(IntPtr window,int bar,ref ScrollInfo info,bool redraw);
    [DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr window,int bar,bool show);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] static extern IntPtr EditMessage(IntPtr window,int message,IntPtr parameter,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageW")] static extern IntPtr InsertEditText(IntPtr window,int message,IntPtr parameter,string text);
    static bool HasVerticalBar(TextBox input) { return (GetWindowLong(input.Handle,-16)&0x00200000)!=0; }
    static bool NoHorizontalScroll(SettingsPage page) { return !page.HorizontalScroll.Visible && (GetWindowLong(page.Handle,-16)&0x00100000)==0 && page.AutoScrollPosition.X==0; }
    static void Check(bool condition,string description) { if(!condition) throw new Exception("settings UI: "+description); }
    internal static void Run(string root,Dictionary<string,object> report) {
        Application.EnableVisualStyles();
        string fixture=Path.Combine(root,"runtime","settings-ui-test-work",Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
        var original=new Settings { WhisperModel="model.bin",SpeakerDevice="fixture-speaker-id",CaptureMaximumWidth=2560,AnnounceCaptureFailure=true,GameName="旧設定の補足名（保持）",
            CustomInstructionsText="  結論から回答してください。  \n改行を保持します。\r\n末尾も保持します。\r",WebSearchTriggersText="# 改行を保持\n最新情報\r現在は\r\nリアルタイム\n" };
        File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1});
        var originalRaw=(Dictionary<string,object>)JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(original));
        originalRaw["SaveConversationHistory"]=true; originalRaw["UseSavedHistory"]=true;
        string config=Path.Combine(fixture,"config.json"); File.WriteAllText(config,JsonData.Serializer().Serialize(originalRaw),new UTF8Encoding(false));
        byte[] baseline=File.ReadAllBytes(config);
        using(var form=new SettingsForm(fixture,original,null,0,true)) {
            Check(form.TabCount==7,"seven categorized tabs");
            Check(Descendants(form).OfType<Label>().Any(label=>label.Text.Contains(RequestStorage.RequestsRoot)),"debug destination displays the per-user request log directory");
            var debug=(CheckBox)form.Inputs["KeepRequestFiles"]; debug.Checked=true;
            Check(form.Draft.KeepRequestFiles && !original.KeepRequestFiles && File.ReadAllBytes(config).SequenceEqual(baseline),"debug saving toggle edits only the draft"); debug.Checked=false;
            var speaker=(ComboBox)form.Inputs["SpeakerDevice"];
            var exclusive=(CheckBox)form.Inputs["ExclusiveMicrophone"];
            Check(!exclusive.Checked && !form.Draft.ExclusiveMicrophone,"exclusive microphone defaults to OFF");
            exclusive.Checked=true;
            Check(form.Draft.ExclusiveMicrophone && !original.ExclusiveMicrophone && File.ReadAllBytes(config).SequenceEqual(baseline),"exclusive microphone changes only the draft");
            exclusive.Checked=false;
            Check(speaker.Items.Count==2 && form.Draft.SpeakerDevice==original.SpeakerDevice && speaker.SelectedItem.ToString().Contains("未接続"),"disconnected speaker remains selectable without losing saved endpoint ID");
            speaker.SelectedIndex=0;
            Check(form.Draft.SpeakerDevice=="" && original.SpeakerDevice=="fixture-speaker-id","Windows routing selection changes only draft");
            speaker.SelectedIndex=1;
            Check(!form.Inputs.ContainsKey("GameName") && form.Draft.GameName==original.GameName,"legacy application hint is hidden and preserved in draft");
            var capture=(CheckBox)form.Inputs["CaptureEnabled"]; capture.Checked=false;
            Check(!form.Draft.CaptureEnabled && !form.Inputs["CaptureMaximumWidth"].Enabled && !form.Inputs["AnnounceCaptureFailure"].Enabled && form.Draft.CaptureMaximumWidth==2560 && form.Draft.AnnounceCaptureFailure,"capture OFF disables dependent controls without clearing values");
            capture.Checked=true;
            Check(form.Inputs["CaptureMaximumWidth"].Enabled && form.Inputs["AnnounceCaptureFailure"].Enabled && form.Draft.CaptureMaximumWidth==2560 && form.Draft.AnnounceCaptureFailure,"capture ON restores dependent controls and prior values");
            Check(!Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用").Enabled,"unchanged settings with retired options do not enable save or prompt on close");
            Check(((TextBox)form.Inputs["WebSearchTriggersText"]).Lines.Length>=3,"LF keywords display on separate lines");
            Check(form.Draft.CustomInstructionsText==original.CustomInstructionsText && form.Draft.WebSearchTriggersText==original.WebSearchTriggersText,"unchanged inline editor content preserves mixed line endings and surrounding whitespace");
            ((CheckBox)form.Inputs["CustomInstructionsEnabled"]).Checked=false; ((CheckBox)form.Inputs["WebSearchTriggersEnabled"]).Checked=false;
            Check(!form.Draft.CustomInstructionsEnabled && !form.Draft.WebSearchTriggersEnabled && form.Draft.CustomInstructionsText==original.CustomInstructionsText && form.Draft.WebSearchTriggersText==original.WebSearchTriggersText,"disabling instructions and keywords preserves their inline content");
            Check(form.Inputs["CustomInstructionsText"].Enabled && form.Inputs["WebSearchTriggersText"].Enabled,"disabled instructions and keywords remain editable");
            ((TextBox)form.Inputs["CustomInstructionsText"]).Text="OFFの間に編集した指示\r\n"; ((TextBox)form.Inputs["WebSearchTriggersText"]).Text="OFFの間に編集したキーワード\r\n";
            Check(form.Draft.CustomInstructionsText=="OFFの間に編集した指示\r\n" && form.Draft.WebSearchTriggersText=="OFFの間に編集したキーワード\r\n","disabled inline editors still populate the draft");
            ((CheckBox)form.Inputs["CustomInstructionsEnabled"]).Checked=true; ((CheckBox)form.Inputs["WebSearchTriggersEnabled"]).Checked=true;
            Check(form.Draft.CustomInstructionsEnabled && form.Draft.WebSearchTriggersEnabled && form.Draft.CustomInstructionsText=="OFFの間に編集した指示\r\n" && form.Draft.WebSearchTriggersText=="OFFの間に編集したキーワード\r\n","re-enabling instructions and keywords preserves edited draft text");
            ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=37;
            var otherVolume=(NumericUpDown)form.Inputs["OtherApplicationVolume"];
            Check(otherVolume.Minimum==0 && otherVolume.Maximum==100 && otherVolume.Value==100,"other application volume defaults to 100 with full zero to hundred range");
            otherVolume.Value=0;
            Check(form.Draft.OtherApplicationVolume==0 && original.OtherApplicationVolume==100,"muting other applications edits only the isolated draft");
            ((CheckBox)form.Inputs["PlayProcessingCues"]).Checked=false;
            ((TextBox)form.Inputs["PushToTalkKey"]).Text="Ctrl+F11";
            Check(form.Draft.SpeechVolume==37 && !form.Draft.PlayProcessingCues && form.Draft.PushToTalkKey=="Ctrl+F11","controls populate isolated draft");
            Check(original.SpeechVolume==85 && original.PlayProcessingCues && File.ReadAllBytes(config).SequenceEqual(baseline),"editing does not mutate active or disk settings");
            Check(!form.Inputs.ContainsKey("SaveConversationHistory") && !form.Inputs.ContainsKey("UseSavedHistory"),"conversation saving and saved recall controls are absent");
            var memoryCount=(NumericUpDown)form.Inputs["RememberLastTurns"];
            foreach(bool keepDebug in new[]{true,false}) {
                debug.Checked=keepDebug;
                Check(memoryCount.Enabled && form.Draft.RememberLastTurns==original.RememberLastTurns,"debug saving does not change memory retention");
            }
            memoryCount.Value=0;
            Check(form.Draft.RememberLastTurns==0 && memoryCount.Minimum==0 && memoryCount.Maximum==10,"memory limit retains its full zero to ten range");
            memoryCount.Value=2;
            Check(form.Draft.RememberLastTurns==2 && !form.Draft.KeepRequestFiles,"memory count can be changed while debug saving is OFF");
        }
        Check(File.ReadAllBytes(config).SequenceEqual(baseline),"cancel closes without writing");
        using(var form=new SettingsForm(fixture,original,s=>s.PushToTalkKey=="F11"?"fixture key is busy":null,0,true)) {
            ((TextBox)form.Inputs["PushToTalkKey"]).Text=form.Draft.ReplayKey;
            Check(!form.SaveAndApply() && !form.Saved && File.ReadAllBytes(config).SequenceEqual(baseline),"duplicate hotkeys rejected without write");
            ((TextBox)form.Inputs["PushToTalkKey"]).Text="F11";
            Check(!form.SaveAndApply() && File.ReadAllBytes(config).SequenceEqual(baseline),"OS hotkey conflict rejected without write");
            ((TextBox)form.Inputs["PushToTalkKey"]).Text="Ctrl+F11"; ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=37;
            ((NumericUpDown)form.Inputs["OtherApplicationVolume"]).Value=0;
            ((CheckBox)form.Inputs["ExclusiveMicrophone"]).Checked=true;
            Check(form.SaveAndApply() && form.Saved && form.AppliedSettings.SpeechVolume==37 && form.AppliedSettings.OtherApplicationVolume==0 && form.AppliedSettings.ExclusiveMicrophone,"valid settings saved including audio isolation options");
        }
        Check(Settings.Load(fixture).PushToTalkKey=="Ctrl+F11" && Settings.Load(fixture).SpeechVolume==37 && Settings.Load(fixture).OtherApplicationVolume==0 && Settings.Load(fixture).ExclusiveMicrophone,"saved config is readable by resident");
        Check(!((Dictionary<string,object>)JsonData.Serializer().DeserializeObject(File.ReadAllText(config))).Keys.Any(SettingsStorage.IsRetiredSetting),"explicit UI save removes retired conversation options");
        Check(Settings.Load(fixture).WebSearchTriggersText==original.WebSearchTriggersText && Settings.Load(fixture).CustomInstructionsText==original.CustomInstructionsText,"saving unrelated settings preserves inline text exactly");
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,3,true)) {
            IntPtr handle=form.Handle; int reservations=0,releases=0,commits=0,rollbacks=0; bool fail=true;
            form.BeginApply=delegate { reservations++; return null; }; form.EndApply=delegate { releases++; };
            form.PrepareApply=value=>new SettingsRuntimeUpdate(delegate { if(fail) throw new InvalidOperationException("fixture runtime apply failure"); commits++; },delegate { rollbacks++; });
            byte[] before=File.ReadAllBytes(config);
            ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=39; ((TextBox)form.Inputs["CustomInstructionsText"]).Text="一度目の保存\r\n";
            Check(!form.SaveAndApply() && !form.Saved && before.SequenceEqual(File.ReadAllBytes(config)) && Settings.Load(fixture).CustomInstructionsText==original.CustomInstructionsText,"runtime failure rolls back config including inline instructions");
            Check(form.Handle==handle && !form.IsDisposed && reservations==releases && rollbacks==1,"failed apply retains same window/draft and releases reservation");
            fail=false;
            Check(form.SaveAndApply() && !form.IsDisposed && form.Handle==handle && Settings.Load(fixture).SpeechVolume==39 && Settings.Load(fixture).CustomInstructionsText=="一度目の保存\r\n","retry applies inline text without recreating the form");
            Check(!Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用").Enabled,"saved snapshot clears dirty state");
            ((TextBox)form.Inputs["CustomInstructionsText"]).Text="二度目の保存\r\n";
            Check(form.SaveAndApply() && Settings.Load(fixture).CustomInstructionsText=="二度目の保存\r\n" && commits==2 && reservations==releases,"same editor supports another save using the refreshed inline text snapshot");
            ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=41;
            Check(Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用").Enabled && Settings.Load(fixture).SpeechVolume==39,"editing after save becomes dirty without changing applied values");
        }
        Check(Settings.Load(fixture).SpeechVolume==39,"discarding later edits preserves the last successful save");
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,2,true)) {
            ((CheckBox)form.Inputs["CustomInstructionsEnabled"]).Checked=false; ((CheckBox)form.Inputs["WebSearchTriggersEnabled"]).Checked=false;
            ((TextBox)form.Inputs["CustomInstructionsText"]).Text="OFFで保存した指示\r\n"; ((TextBox)form.Inputs["WebSearchTriggersText"]).Text="現在の相場\r\n速報\r\n";
            IntPtr handle=form.Handle;
            Check(form.SaveAndApply() && form.Handle==handle && !form.IsDisposed,"disabled inline text saves without replacing the settings window");
            Settings disabled=Settings.Load(fixture);
            Check(!disabled.CustomInstructionsEnabled && !disabled.WebSearchTriggersEnabled && disabled.CustomInstructionsText=="OFFで保存した指示\r\n" && disabled.WebSearchTriggersText=="現在の相場\r\n速報\r\n","OFF flags and edited inline text persist together in config");
            Check(form.Inputs["CustomInstructionsText"].Enabled && form.Inputs["WebSearchTriggersText"].Enabled && !Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用").Enabled,"saved disabled text stays editable and updates dirty baseline");
            ((CheckBox)form.Inputs["CustomInstructionsEnabled"]).Checked=true; ((CheckBox)form.Inputs["WebSearchTriggersEnabled"]).Checked=true;
            Check(form.SaveAndApply() && Settings.Load(fixture).CustomInstructionsText==disabled.CustomInstructionsText && Settings.Load(fixture).WebSearchTriggersText==disabled.WebSearchTriggersText,"re-enabling both features retains saved inline text exactly");
        }
        Check(!File.Exists(Path.Combine(fixture,"custom-instructions.txt")) && !File.Exists(Path.Combine(fixture,"web-search-triggers.txt")),"UI saves do not create separate instruction or keyword files");
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,0,true)) {
            ((CheckBox)form.Inputs["CaptureEnabled"]).Checked=false;
            Check(form.SaveAndApply() && !form.AppliedSettings.CaptureEnabled,"capture OFF saves through UI");
        }
        Settings captureOff=Settings.Load(fixture);
        Check(!captureOff.CaptureEnabled && captureOff.CaptureMaximumWidth==2560 && captureOff.AnnounceCaptureFailure && captureOff.GameName==original.GameName,"capture OFF survives reload with hidden legacy hint and dependent values");
        using(var form=new SettingsForm(fixture,captureOff,null,0,true)) {
            Check(!((CheckBox)form.Inputs["CaptureEnabled"]).Checked && !form.Inputs["CaptureMaximumWidth"].Enabled && !form.Inputs["AnnounceCaptureFailure"].Enabled,"reopened capture OFF UI remains disabled");
            ((CheckBox)form.Inputs["CaptureEnabled"]).Checked=true;
            Check(form.Inputs["CaptureMaximumWidth"].Enabled && form.Inputs["AnnounceCaptureFailure"].Enabled && form.Draft.CaptureMaximumWidth==2560 && form.Draft.AnnounceCaptureFailure,"reopening then re-enabling capture restores prior values");
            Check(form.SaveAndApply(),"capture ON saves through UI");
        }
        Check(Settings.Load(fixture).CaptureEnabled && Settings.Load(fixture).GameName==original.GameName,"capture ON reload retains hidden legacy hint");
        var legacyRaw=(Dictionary<string,object>)JsonData.Serializer().DeserializeObject(File.ReadAllText(config)); legacyRaw.Remove("CaptureEnabled");
        File.WriteAllText(config,JsonData.Serializer().Serialize(legacyRaw),new UTF8Encoding(false));
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,0,true)) {
            Check(form.Draft.CaptureEnabled && ((CheckBox)form.Inputs["CaptureEnabled"]).Checked && form.Inputs["CaptureMaximumWidth"].Enabled && form.Draft.GameName==original.GameName,"legacy config without capture flag opens enabled and retains hidden hint");
            ((NumericUpDown)form.Inputs["SpeechVolume"]).Value=38;
            Check(form.SaveAndApply() && Settings.Load(fixture).CaptureEnabled && Settings.Load(fixture).GameName==original.GameName,"saving migrated legacy config retains default capture behavior and hint");
        }
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,1,true)) {
            Check(form.Draft.SpeakerDevice==original.SpeakerDevice,"unrelated UI changes preserve selected speaker");
            ((ComboBox)form.Inputs["SpeakerDevice"]).SelectedIndex=0;
            Check(form.SaveAndApply() && Settings.Load(fixture).SpeakerDevice=="","Windows routing selection saves and reloads");
        }
        CheckPlaceholders(fixture);
        CheckInputScrollbars(fixture);
        CheckAdvancedReveal(fixture);
        report["placeholderChecks"]="default text paints only for empty native editors / native Text stays empty / empty content persists without hint text / typing hides hint / no dirty changes from painting";
        report["advancedRevealChecks"]="initially hidden / limits always visible / engine and application instructions retained / reveal replaces button with two cards without dirty changes / independent application instructions save and reopen / reset only this draft field / repeated in-place saves / reopen hidden / detached controls disposed";
        var files=new List<string>();
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,0,true)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show();
            string[] names={"general","audio","search","instructions","history","connection","advanced"};
            for(int i=0;i<form.TabCount;i++) {
                form.SelectTab(i); form.PerformLayout(); Application.DoEvents();
                string path=Path.Combine(fixture,"settings-"+names[i]+".png");
                using(var image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(path,ImageFormat.Png); } files.Add(path);
                foreach(var page in Descendants(form).OfType<SettingsPage>().Where(p=>p.Visible)) {
                    Check(!page.HorizontalScroll.Visible,"no horizontal scrolling in "+names[i]);
                    foreach(var card in Descendants(page).OfType<SettingsCard>()) Check(card.Width>page.ClientSize.Width*0.8 && card.Height>80,"cards fill available page width in "+names[i]);
                }
            }
            form.ClientSize=new Size(920,650); form.SelectTab(0); form.PerformLayout(); Application.DoEvents();
            report["compactLayout"]=Descendants(form).OfType<SettingsPage>().Where(p=>p.Visible).Select(p=>new { size=p.ClientSize.ToString(),display=p.DisplayRectangle.ToString(),horizontal=p.HorizontalScroll.Visible,children=p.Controls.Cast<Control>().Select(c=>new { name=c.GetType().Name,bounds=c.Bounds.ToString() }).ToArray() }).ToArray();
            foreach(var page in Descendants(form).OfType<SettingsPage>().Where(p=>p.Visible)) Check(!page.HorizontalScroll.Visible,"compact page has no horizontal scrolling");
            string small=Path.Combine(fixture,"settings-compact.png"); using(var image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(small,ImageFormat.Png); } files.Add(small);
            form.SelectTab(1); form.PerformLayout(); Application.DoEvents();
            foreach(var page in Descendants(form).OfType<SettingsPage>().Where(p=>p.Visible)) Check(!page.HorizontalScroll.Visible,"compact audio page has no horizontal scrolling");
            string smallAudio=Path.Combine(fixture,"settings-audio-compact.png"); using(var image=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(smallAudio,ImageFormat.Png); } files.Add(smallAudio);
            form.Close();
        }
        report["previews"]=files; report["checks"]="7 tabs / per-user debug destination / draft isolation / cancel / validation / hotkey conflicts / conditional controls / inline instructions and keywords / mixed line ending preservation / editable and persistent OFF content / in-place repeated saves and refreshed dirty baseline / runtime failure rollback / no separate text files / capture OFF-ON persistence / legacy capture default / hidden application hint preservation / config roundtrip / full and compact rendering / card dimensions";
    }
    static string AdvancedValues(Settings settings) {
        return JsonData.Serializer().Serialize(new { settings.WhisperThreads,settings.Language,settings.WhisperModel,settings.CodexPath,settings.CodexModel,settings.ApplicationInstructionsText });
    }
    static void CheckInputScrollbars(string fixture) {
        using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,3,true)) {
            form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Application.DoEvents();
            foreach(string key in new[]{"CustomInstructionsText","ApplicationInstructionsText","WebSearchTriggersText"}) {
                form.SelectTab(key=="CustomInstructionsText"?3:key=="ApplicationInstructionsText"?6:2);
                if(key=="ApplicationInstructionsText") Descendants(form).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle").PerformClick();
                Application.DoEvents();
                var input=(TextBox)form.Inputs[key]; string original=input.Text; int height=input.Height; IntPtr handle=input.Handle;
                Check(!HasVerticalBar(input),"short saved text has no empty native scrollbar: "+key);
                input.Text="短い本文"; input.ClearUndo(); input.Select(input.TextLength,0); Application.DoEvents();
                string appended="\r\n"+string.Join("\r\n",Enumerable.Range(1,100).Select(i=>"長い指示の行 "+i));
                InsertEditText(input.Handle,0x00C2,new IntPtr(1),appended); Application.DoEvents(); // EM_REPLACESEL, undoable
                Check(HasVerticalBar(input) && input.Handle==handle && input.CanUndo,"long text shows scrollbar without replacing the editor or undo history: "+key);
                Check(input.SelectionStart==input.TextLength && input.SelectionLength==0,"scrollbar update preserves the insertion caret: "+key);
                EditMessage(input.Handle,0x00B5,new IntPtr(7),IntPtr.Zero); // EM_SCROLL, SB_BOTTOM
                Check(EditMessage(input.Handle,0x00CE,IntPtr.Zero,IntPtr.Zero).ToInt32()>0,"overflow text remains scrollable: "+key);
                input.Undo(); Application.DoEvents();
                Check(input.Text=="短い本文" && !HasVerticalBar(input) && input.Handle==handle,"undo restores short text and hides the unnecessary scrollbar: "+key);
                input.Text=new string('あ',2000); input.Height=60; Application.DoEvents();
                Check(HasVerticalBar(input),"long text without line breaks also scrolls after native wrapping: "+key);
                input.Text=string.Join("\r\n",Enumerable.Range(1,20).Select(i=>"高さの変更 "+i)); input.Height=60; Application.DoEvents();
                Check(HasVerticalBar(input),"shrinking an input shows scrolling when necessary: "+key);
                input.Height=700; Application.DoEvents();
                Check(!HasVerticalBar(input) && input.Handle==handle,"enlarging an input removes the unused scrollbar without losing the native editor: "+key);
                input.Height=height; input.Text=""; Application.DoEvents();
                Check(!HasVerticalBar(input),"empty input and display-only placeholder have no scrollbar: "+key);
                input.Text=original; Application.DoEvents();
            }
            form.Close();
        }
    }
    static void CheckPlaceholders(string fixture) {
        string config=Path.Combine(fixture,"config.json"); byte[] baseline=File.ReadAllBytes(config);
        try {
            using(var form=new SettingsForm(fixture,Settings.Load(fixture),null,2,true)) {
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); Application.DoEvents();
                var keywords=(PlaceholderTextBox)form.Inputs["WebSearchTriggersText"];
                var custom=(PlaceholderTextBox)form.Inputs["CustomInstructionsText"];
                Check(keywords.PlaceholderText==SettingsTheme.EditorText(SearchTriggers.DefaultText) && custom.PlaceholderText==SettingsTheme.EditorText(CustomInstructions.DefaultText),"both placeholders use the shared default text");
                keywords.Text=""; custom.Text=""; Application.DoEvents();
                Check(keywords.PlaceholderVisible && custom.PlaceholderVisible && form.Draft.WebSearchTriggersText=="" && form.Draft.CustomInstructionsText=="","empty editor hints do not enter the draft");
                Check(GetWindowTextLength(keywords.Handle)==0 && GetWindowTextLength(custom.Handle)==0,"native input text excludes painted multiline placeholders");
                using(var shown=new Bitmap(keywords.Width,keywords.Height)) using(var hidden=new Bitmap(keywords.Width,keywords.Height)) {
                    keywords.DrawToBitmap(shown,new Rectangle(Point.Empty,shown.Size));
                    string hint=keywords.PlaceholderText; keywords.PlaceholderText=""; keywords.DrawToBitmap(hidden,new Rectangle(Point.Empty,hidden.Size)); keywords.PlaceholderText=hint;
                    int changed=0; for(int y=0;y<shown.Height;y++) for(int x=0;x<shown.Width;x++) if(shown.GetPixel(x,y)!=hidden.GetPixel(x,y)) changed++;
                    Check(changed>20,"empty multiline keyword editor visibly paints its placeholder");
                    shown.Save(Path.Combine(fixture,"settings-keyword-placeholder.png"),ImageFormat.Png);
                }
                Check(form.SaveAndApply() && Settings.Load(fixture).CustomInstructionsText=="" && Settings.Load(fixture).WebSearchTriggersText=="","saving empty fields does not save placeholder text");
                var save=Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用");
                form.SelectTab(3); custom.Focus(); custom.Invalidate(); custom.Refresh(); Application.DoEvents();
                using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-custom-placeholder.png"),ImageFormat.Png); }
                Check(!save.Enabled && custom.Text=="" && custom.PlaceholderVisible,"focus and repaint do not dirty or populate the editor");
                keywords.Text="指定したキーワード"; custom.Text="指定したカスタム指示";
                Check(!keywords.PlaceholderVisible && !custom.PlaceholderVisible && form.Draft.WebSearchTriggersText==keywords.Text && form.Draft.CustomInstructionsText==custom.Text,"user text replaces the hint without adding default instructions");
                form.Close();
            }
            using(var reopened=new SettingsForm(fixture,Settings.Load(fixture),null,3,true)) {
                Check(reopened.Draft.CustomInstructionsText=="" && reopened.Draft.WebSearchTriggersText=="" && ((PlaceholderTextBox)reopened.Inputs["CustomInstructionsText"]).PlaceholderVisible && ((PlaceholderTextBox)reopened.Inputs["WebSearchTriggersText"]).PlaceholderVisible,"empty saved values reopen with display-only hints");
            }
        } finally { File.WriteAllBytes(config,baseline); }
    }
    static void CheckAdvancedReveal(string fixture) {
        string config=Path.Combine(fixture,"config.json"); byte[] baseline=File.ReadAllBytes(config); Control engine=null,application=null;
        try {
            Settings before=Settings.Load(fixture);
            using(var form=new SettingsForm(fixture,before,null,0,true)) {
                form.ShowInTaskbar=false; form.StartPosition=FormStartPosition.Manual; form.Location=new Point(-20000,-20000); form.Show(); form.SelectTab(6); Application.DoEvents();
                var expand=Descendants(form).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle");
                var save=Descendants(form).OfType<Button>().Single(button=>button.Text=="保存して適用");
                var page=Descendants(form).OfType<SettingsPage>().Single(p=>p.Visible);
                string[] keys={"WhisperThreads","Language","WhisperModel","CodexPath","CodexModel"};
                engine=form.Inputs[keys[0]]; while(!(engine is SettingsCard)) engine=engine.Parent;
                application=form.Inputs["ApplicationInstructionsText"]; while(!(application is SettingsCard)) application=application.Parent;
                Control buttonCard=expand; while(!(buttonCard is SettingsCard)) buttonCard=buttonCard.Parent;
                int buttonIndex=page.Controls.GetChildIndex(buttonCard),cardCount=page.Controls.Count;
                Check(keys.All(key=>!form.Inputs[key].Visible) && !engine.Visible && !page.Controls.Contains(engine) && !application.Visible && !page.Controls.Contains(application),"advanced engine and application instructions are initially hidden without occupying page space");
                Check(form.Inputs["RequestTimeoutSeconds"].Visible && form.Inputs["WhisperTimeoutSeconds"].Visible && form.Inputs["MaximumRecordingSeconds"].Visible && form.Inputs["MinimumRecordingMilliseconds"].Visible,"recording and processing limits remain visible while engine settings are hidden");
                Check(AdvancedValues(form.Draft)==AdvancedValues(before) && !save.Enabled,"hidden advanced fields preserve engine values and application instructions without dirty changes");
                expand.PerformClick(); Application.DoEvents();
                string expandedPreview=Path.Combine(fixture,"settings-advanced-revealed.png");
                using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(expandedPreview,ImageFormat.Png); }
                Check(NoHorizontalScroll(page),"revealed advanced page has no horizontal scrollbar: client="+page.ClientSize+", display="+page.DisplayRectangle+", cards="+string.Join(";",page.Controls.Cast<Control>().Select(c=>c.Bounds.ToString()))+", preview="+expandedPreview);
                foreach(var size in new[]{new Size(920,650),new Size(1200,900),new Size(1080,790)}) {
                    form.ClientSize=size; Application.DoEvents(); page.ScrollControlIntoView(form.Inputs["ApplicationInstructionsText"]); Application.DoEvents();
                    Check(NoHorizontalScroll(page),"revealed advanced page fits after resizing and scrolling: form="+size+", client="+page.ClientSize+", display="+page.DisplayRectangle+", cards="+string.Join(";",page.Controls.Cast<Control>().Select(c=>c.Bounds.ToString())));
                    form.SelectTab(0); form.SelectTab(6); Application.DoEvents();
                    Check(NoHorizontalScroll(page),"revealed advanced page fits after changing tabs");
                }
                page.ScrollControlIntoView(application); Application.DoEvents();
                using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(fixture,"settings-application-instructions.png"),ImageFormat.Png); }
                // Reproduce the actual native range left over from the initial 200px
                // panel, even though the managed controls already fit the final width.
                page.SuspendLayout(); bool staleBar;
                try {
                    var stale=new ScrollInfo { Size=(uint)Marshal.SizeOf(typeof(ScrollInfo)),Mask=7,Minimum=0,Maximum=259,Page=183,Position=0 };
                    SetScrollInfo(page.Handle,0,ref stale,true); ShowScrollBar(page.Handle,0,true);
                    staleBar=(GetWindowLong(page.Handle,-16)&0x00100000)!=0;
                } finally { page.ResumeLayout(true); }
                page.PerformLayout(); Application.DoEvents();
                Check(staleBar && NoHorizontalScroll(page),"layout clears the reported stale native horizontal bar while retaining vertical scrolling");
                Check(page.VerticalScroll.Visible && page.Controls.Cast<Control>().All(card=>card.Left>=0 && card.Right<=page.ClientSize.Width),"expanded settings fit across the visible page and retain vertical access");
                Check(engine.Visible && application.Visible && keys.All(key=>form.Inputs[key].Visible) && form.Inputs["ApplicationInstructionsText"].Visible && !save.Enabled,"revealing advanced fields shows all controls without enabling save");
                Check(expand.IsDisposed && buttonCard.IsDisposed && !Descendants(form).Any(control=>control.Name=="advanced-settings-toggle") && page.Controls.Count==cardCount+1 && page.Controls.GetChildIndex(engine)==buttonIndex && page.Controls.GetChildIndex(application)==buttonIndex+1,"revealing replaces and disposes the button card with the engine and application instruction cards in order");
                string model=Path.Combine(fixture,"advanced-model.bin"),codex=Path.Combine(fixture,"advanced-codex.exe"); File.WriteAllBytes(model,new byte[]{1}); File.WriteAllBytes(codex,new byte[]{0});
                ((NumericUpDown)form.Inputs["WhisperThreads"]).Value=before.WhisperThreads==3?4:3;
                var language=(ComboBox)form.Inputs["Language"]; language.SelectedItem=language.Items.Cast<SettingsChoice>().First(choice=>(string)choice.Value=="en");
                ((TextBox)form.Inputs["WhisperModel"]).Text=model; ((TextBox)form.Inputs["CodexPath"]).Text=codex; ((TextBox)form.Inputs["CodexModel"]).Text="advanced-fixture-model";
                ((TextBox)form.Inputs["ApplicationInstructionsText"]).Text="今回の質問だけに答えてください。\r\n保存確認用のアプリケーション指示。";
                ((CheckBox)form.Inputs["CustomInstructionsEnabled"]).Checked=false;
                string edited=AdvancedValues(form.Draft); IntPtr handle=form.Handle;
                Check(save.Enabled && edited!=AdvancedValues(before),"expanded advanced fields remain editable through their existing readers");
                Check(form.SaveAndApply() && form.Handle==handle && engine.Visible && application.Visible && AdvancedValues(Settings.Load(fixture))==edited && !Settings.Load(fixture).CustomInstructionsEnabled && !save.Enabled,"saving advanced edits and independent application instructions keeps the same window and refreshes its dirty baseline");
                ((NumericUpDown)form.Inputs["WhisperThreads"]).Value=before.WhisperThreads==5?6:5; edited=AdvancedValues(form.Draft);
                Check(form.SaveAndApply() && form.Handle==handle && engine.Visible && AdvancedValues(Settings.Load(fixture))==edited && !save.Enabled && !Descendants(form).Any(control=>control.Name=="advanced-settings-toggle"),"repeated save preserves all advanced values without restoring the reveal button");
                form.Close();
            }
            Check(engine.IsDisposed && application.IsDisposed,"closing a revealed form disposes both advanced cards");
            using(var reopened=new SettingsForm(fixture,Settings.Load(fixture),null,6,true)) {
                reopened.ShowInTaskbar=false; reopened.StartPosition=FormStartPosition.Manual; reopened.Location=new Point(-20000,-20000); reopened.Show(); reopened.SelectTab(6); Application.DoEvents();
                engine=reopened.Inputs["WhisperThreads"]; while(!(engine is SettingsCard)) engine=engine.Parent;
                application=reopened.Inputs["ApplicationInstructionsText"]; while(!(application is SettingsCard)) application=application.Parent;
                Check(!engine.Visible && engine.Parent==null && !application.Visible && application.Parent==null && !reopened.Inputs["WhisperThreads"].Visible && Descendants(reopened).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle").Text=="上級者向け設定を表示" && AdvancedValues(reopened.Draft)==AdvancedValues(Settings.Load(fixture)),"reopening settings restores the reveal button with advanced fields hidden and saved values intact");
                reopened.Close();
            }
            Check(engine.IsDisposed && application.IsDisposed,"closing without revealing disposes both detached advanced cards");
            using(var resetForm=new SettingsForm(fixture,Settings.Load(fixture),null,6,true)) {
                resetForm.ShowInTaskbar=false; resetForm.StartPosition=FormStartPosition.Manual; resetForm.Location=new Point(-20000,-20000); resetForm.Show(); resetForm.SelectTab(6); Application.DoEvents();
                Descendants(resetForm).OfType<Button>().Single(button=>button.Name=="advanced-settings-toggle").PerformClick(); Application.DoEvents();
                byte[] beforeReset=File.ReadAllBytes(config); Settings saved=Settings.Load(fixture);
                Descendants(resetForm).OfType<Button>().Single(button=>button.Name=="application-instructions-reset").PerformClick();
                Check(resetForm.Draft.ApplicationInstructionsText==SettingsTheme.EditorText(ApplicationInstructions.DefaultText) && File.ReadAllBytes(config).SequenceEqual(beforeReset),"application reset changes only the draft until save");
                Settings expected=SettingsStorage.Clone(saved); expected.ApplicationInstructionsText=resetForm.Draft.ApplicationInstructionsText;
                Check(JsonData.Serializer().Serialize(resetForm.Draft)==JsonData.Serializer().Serialize(expected),"application reset leaves all other settings and custom instructions unchanged");
                Check(resetForm.SaveAndApply() && SettingsTheme.EditorText(Settings.Load(fixture).ApplicationInstructionsText)==SettingsTheme.EditorText(ApplicationInstructions.DefaultText) && !Settings.Load(fixture).CustomInstructionsEnabled,"reset application instructions save independently of the custom instruction toggle");
                resetForm.Close();
            }
        } finally { File.WriteAllBytes(config,baseline); }
    }
    static IEnumerable<Control> Descendants(Control parent) { foreach(Control control in parent.Controls) { yield return control; foreach(var child in Descendants(control)) yield return child; } }
}
}

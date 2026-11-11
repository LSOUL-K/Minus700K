using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Minus700K {
internal sealed class SettingsForm : Form {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd,int command);
    readonly string root; readonly SettingsEditSession edit; readonly Func<Settings,string> checkKeys; readonly bool preview;
    readonly List<Action<Settings>> readers=new List<Action<Settings>>();
    readonly List<Control> detachedLanguageControls=new List<Control>();
    readonly List<SettingsPage> pages=new List<SettingsPage>(); readonly List<Button> navigation=new List<Button>();
    readonly Dictionary<string,Control> inputs=new Dictionary<string,Control>();
    readonly Panel content=new Panel { Dock=DockStyle.Fill,Padding=new Padding(28,0,22,4) };
    readonly Label heading=SettingsTheme.Label("ui.tabs.general",false,22),description=SettingsTheme.Label("",true,10),feedback=SettingsTheme.Label("",true,9);
    readonly Button save=SettingsTheme.Button("ui.save",true,150),close=SettingsTheme.Button("ui.close",false,94);
    readonly FlowLayoutPanel startupFailure=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(28,12,22,12),BackColor=SettingsTheme.Card,Visible=false };
    readonly Label startupTitle=SettingsTheme.Label("ui.startup.title",false,11),startupError=SettingsTheme.Label("",false,10),startupHelp=SettingsTheme.Label("ui.startup.help",true,9);
    readonly Button startupRetry=SettingsTheme.Button("ui.startup.retry",true,250);
    readonly string[] titles={"ui.tabs.general","ui.tabs.audio","ui.tabs.search","ui.tabs.instructions","ui.tabs.history","ui.tabs.connection","ui.tabs.advanced"};
    TextBox instructions,triggers; string selectedAppLanguage;
    PlanConnectionPanel connection; SpeechPlayback sampleVoice; CuePlayer sampleCue;
    bool loading=true,discard,dirty,shortcutCapture,windowActive; int selected;
    bool updateWaiting,enabledBeforeUpdate;
    internal event EventHandler ShortcutCaptureChanged,ConnectionChanging,ConnectionUpdated,ConnectionBusyChanged;
    internal Func<string> BeginApply { get; set; }
    internal Action EndApply { get; set; }
    internal Func<Settings,SettingsRuntimeUpdate> PrepareApply { get; set; }
    internal Func<CancellationToken,Task> BeforeConnectionChange { get; set; }
    internal bool ShortcutCaptureActive { get { return shortcutCapture; } }
    internal bool ConnectionBusy { get { return connection!=null && connection.IsBusy; } }
    internal bool ShutdownReady { get { return !ConnectionBusy && (sampleVoice==null || sampleVoice.IsIdle); } }
    internal bool Saved { get; private set; }
    internal bool StartupRetryRequested { get; private set; }
    internal Settings AppliedSettings { get; private set; }
    internal IDictionary<string,Control> Inputs { get { return inputs; } }
    internal int TabCount { get { return pages.Count; } }
    internal Settings Draft { get { Settings value=SettingsStorage.Clone(edit.Value); foreach(var read in readers) read(value); return value; } }
    internal SettingsForm(string root,Settings current,Func<Settings,string> checkKeys=null,int initialTab=0,bool preview=false,PlanConnectionPanel connectionPanel=null) {
        this.root=root; this.checkKeys=checkKeys; this.preview=preview; connection=connectionPanel; edit=SettingsStorage.Open(root);
        L10n.SetLanguage(current==null?edit.Value.AppLanguage:current.AppLanguage);
        SettingsTheme.BindText(this,"ui.settings.title"); BackColor=SettingsTheme.Background; ForeColor=SettingsTheme.Text; Font=SettingsTheme.Font();
        AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi; ClientSize=new Size(1080,790); MinimumSize=new Size(920,650); StartPosition=FormStartPosition.CenterScreen;
        using(var icons=new TrayIcons()) Icon=(Icon)icons.ForState("ready").Clone();
        var sidebar=new Panel { Dock=DockStyle.Left,Width=196,BackColor=SettingsTheme.Sidebar,Padding=new Padding(16,24,16,16) };
        var shell=new Panel { Dock=DockStyle.Fill,BackColor=SettingsTheme.Background }; Controls.Add(shell); Controls.Add(sidebar);
        var brand=new Panel { Dock=DockStyle.Top,Height=100 };
        var name=SettingsTheme.Label("Minus700K",false,19); name.Font=SettingsTheme.Font(19,FontStyle.Bold); name.Location=new Point(4,0); brand.Controls.Add(name);
        var nav=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false };
        sidebar.Controls.Add(nav);
        sidebar.Controls.Add(brand);
        
        var header=new Panel { Dock=DockStyle.Top,Height=112,Padding=new Padding(28,23,24,12) };
        heading.Dock=DockStyle.Top; heading.Font=SettingsTheme.Font(22,FontStyle.Bold); description.Dock=DockStyle.Bottom; header.Controls.Add(heading); header.Controls.Add(description);
        var footer=new Panel { Dock=DockStyle.Bottom,Height=82,Padding=new Padding(28,14,28,14),BackColor=SettingsTheme.Sidebar };
        var actions=new FlowLayoutPanel { Dock=DockStyle.Right,Width=270,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Padding=new Padding(0,5,0,0) };
        actions.Controls.Add(close); actions.Controls.Add(save); feedback.Dock=DockStyle.Fill; feedback.AutoSize=false; feedback.TextAlign=ContentAlignment.MiddleLeft; feedback.Padding=new Padding(0,0,15,0);
        startupError.Name="StartupError"; startupError.ForeColor=SettingsTheme.Error;
        startupRetry.Name="StartupRetry"; startupRetry.Margin=new Padding(0,6,0,0);
        startupFailure.Controls.AddRange(new Control[]{startupTitle,startupError,startupHelp,startupRetry});
        startupFailure.SizeChanged+=delegate { LayoutStartupFailure(); };
        startupRetry.Click+=delegate {
            if(ConnectionBusy || (dirty && !SaveAndApply())) return;
            StartupRetryRequested=true; discard=true; Close();
        };
        footer.Controls.Add(feedback); footer.Controls.Add(actions); shell.Controls.Add(content); shell.Controls.Add(footer); shell.Controls.Add(startupFailure); shell.Controls.Add(header);
        for(int i=0;i<titles.Length;i++) {
            int index=i; var button=SettingsTheme.Button(titles[i],false,164); button.Height=44; button.Margin=new Padding(0,0,0,7); button.FlatAppearance.BorderSize=0; button.TextAlign=ContentAlignment.MiddleLeft; button.Padding=new Padding(14,0,0,0); button.AccessibleRole=AccessibleRole.PageTab;
            button.Click+=delegate { SelectTab(index); }; nav.Controls.Add(button); navigation.Add(button);
            var page=new SettingsPage { AccessibleName=L10n.T(titles[i]),Visible=false };
            SettingsTheme.BindLanguage(page,delegate { page.AccessibleName=L10n.T(titles[index]); }); pages.Add(page); content.Controls.Add(page);
        }
        BuildGeneral(); BuildAudio(); BuildSearch(); BuildInstructions(); BuildHistory(); BuildConnection(); BuildAdvanced();
        close.Click+=delegate { discard=true; Close(); }; save.Click+=delegate { SaveAndApply(); };
        RefreshLanguage(); loading=false; SelectTab(initialTab); Changed();
        var language=(ComboBox)inputs["AppLanguage"];
        selectedAppLanguage=(string)((SettingsChoice)language.SelectedItem).Value;
        language.SelectedIndexChanged+=delegate { ChangeDraftLanguage(); };
        FormClosing+=ClosingSettings;
    }
    internal void ShowStartupFailure(string message) {
        StartupRetryRequested=false; startupError.Text=message??"";
        startupRetry.Enabled=!ConnectionBusy; startupFailure.Visible=true; LayoutStartupFailure();
    }
    internal void BeginUpdateWait() {
        if(updateWaiting) return;
        updateWaiting=true; enabledBeforeUpdate=Enabled; Enabled=false;
        feedback.Text=L10n.T("ui.update.waiting");
    }
    internal void EndUpdateWait() {
        if(!updateWaiting) return;
        updateWaiting=false; Enabled=enabledBeforeUpdate; Changed();
    }
    internal void CloseForUpdate() { discard=true; Close(); }
    void LayoutStartupFailure() {
        int width=Math.Max(200,startupFailure.ClientSize.Width-startupFailure.Padding.Horizontal);
        foreach(var label in new[]{startupTitle,startupError,startupHelp}) {
            var maximum=new Size(width,0); if(label.MaximumSize!=maximum) label.MaximumSize=maximum;
        }
    }
    protected override bool ShowWithoutActivation { get { return preview; } }
    protected override void OnActivated(EventArgs e) { windowActive=true; base.OnActivated(e); UpdateShortcutCapture(); }
    protected override void OnDeactivate(EventArgs e) { windowActive=false; SetShortcutCapture(false); base.OnDeactivate(e); }
    void UpdateShortcutCapture() { SetShortcutCapture(windowActive && inputs.Values.OfType<ShortcutBox>().Any(input=>input.Focused)); }
    void SetShortcutCapture(bool value) {
        bool changed=shortcutCapture!=value; shortcutCapture=value;
        if(changed && ShortcutCaptureChanged!=null) ShortcutCaptureChanged(this,EventArgs.Empty);
        // Update every editor even when moving directly between shortcut fields.
        foreach(var input in inputs.Values.OfType<ShortcutBox>()) input.SetCaptureActive(value && input.Focused);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); try { int dark=1; DwmSetWindowAttribute(Handle,20,ref dark,4); } catch { } }
    protected override void OnShown(EventArgs e) {
        base.OnShown(e); if(preview) return;
        var area=Screen.FromControl(this).WorkingArea;
        MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width-24),Math.Min(MinimumSize.Height,area.Height-24));
        Size=new Size(Math.Min(Width,area.Width-32),Math.Min(Height,area.Height-32));
        // Hidden startup (WScript style 0 / STARTUPINFO SW_HIDE) overrides the
        // first native ShowWindow even though WinForms reports Visible=true.
        // Run after that first show completes, only for an explicit settings UI.
        BeginInvoke((MethodInvoker)delegate { Reveal(); });
    }
    internal void Reveal() {
        if(preview || IsDisposed || !IsHandleCreated) return;
        ShowWindow(Handle,WindowState==FormWindowState.Minimized?9:5);
        Activate();
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        if(inputs.Values.OfType<ShortcutBox>().Any(input=>input.Focused)) return base.ProcessCmdKey(ref msg,keyData);
        if(keyData==(Keys.Control|Keys.Tab)) { SelectTab((selected+1)%pages.Count); return true; }
        if(keyData==(Keys.Control|Keys.Shift|Keys.Tab)) { SelectTab((selected+pages.Count-1)%pages.Count); return true; }
        if(keyData==(Keys.Control|Keys.S)) { SaveAndApply(); return true; }
        return base.ProcessCmdKey(ref msg,keyData);
    }
    protected override bool ProcessDialogChar(char charCode) {
        // Give access keys their normal handling first. Unassigned Alt letters
        // otherwise reach DefWindowProc as WM_SYSCHAR and beep on every repeat.
        if(base.ProcessDialogChar(charCode)) return true;
        // Keep Alt+Space's system menu and Ctrl+Alt (AltGr) text input intact.
        return charCode!=' ' && (ModifierKeys&(Keys.Control|Keys.Alt))==Keys.Alt;
    }
    internal void SelectTab(int index) {
        index=Math.Max(0,Math.Min(pages.Count-1,index)); selected=index;
        for(int i=0;i<pages.Count;i++) { pages[i].Visible=i==index; navigation[i].BackColor=i==index?SettingsTheme.Selected:SettingsTheme.Sidebar; navigation[i].ForeColor=i==index?SettingsTheme.Accent:SettingsTheme.Muted; }
        pages[index].BringToFront(); 
        heading.Text=L10n.T(titles[index]); 
        ActiveControl=navigation[index];
    }
    SettingsCard Card(int tab,string title,string detail=null) { var card=new SettingsCard(title,detail); pages[tab].Controls.Add(card); return card; }
    void Bind(string key,Control input,Action<Settings> read) { inputs[key]=input; readers.Add(read); }
    ToggleSwitch Toggle(string key,bool value,Action<Settings,bool> setter) { var control=new ToggleSwitch(value); control.CheckedChanged+=delegate { Changed(); }; Bind(key,control,s=>setter(s,control.Checked)); return control; }
    NumericUpDown Number(string key,int value,int min,int max,Action<Settings,int> setter) {
        var control=new NumericUpDown { Minimum=min,Maximum=max,Value=Math.Max(min,Math.Min(max,value)),BackColor=SettingsTheme.Input,ForeColor=SettingsTheme.Text,BorderStyle=BorderStyle.FixedSingle,Font=SettingsTheme.Font(),Width=150,Height=30,TextAlign=HorizontalAlignment.Right };
        control.ValueChanged+=delegate { Changed(); }; Bind(key,control,s=>setter(s,(int)control.Value)); return control;
    }
    TextBox TextInput(string key,string value,Action<Settings,string> setter,bool multiline=false) {
        var input=SettingsTheme.TextBox(value,multiline); input.TextChanged+=delegate { Changed(); }; Bind(key,input,s=>setter(s,input.Text.Trim())); return input;
    }
    ComboBox Choice(string key,object value,IEnumerable<SettingsChoice> choices,Action<Settings,object> setter) {
        var input=SettingsTheme.Combo(); foreach(var c in choices) input.Items.Add(c);
        int index=-1; for(int i=0;i<input.Items.Count;i++) if(object.Equals(((SettingsChoice)input.Items[i]).Value,value)) index=i;
        if(index<0) { input.Items.Add(new SettingsChoice(value,()=>L10n.F("ui.currentValue",value))); index=input.Items.Count-1; }
        input.SelectedIndex=index; input.SelectedIndexChanged+=delegate { Changed(); }; Bind(key,input,s=>setter(s,((SettingsChoice)input.SelectedItem).Value)); return input;
    }
    void ChangeDraftLanguage() {
        if(loading) return;
        var selectedLanguage=((ComboBox)inputs["AppLanguage"]).SelectedItem as SettingsChoice;
        if(selectedLanguage==null) return;
        string language=(string)selectedLanguage.Value;
        if(language==selectedAppLanguage) return;
        Settings value=Draft;
        string custom=value.CustomInstructionsText,keywords=value.WebSearchTriggersText,application=value.ApplicationInstructionsText;
        LocalizedDefaults.ApplyLanguage(value,language);
        loading=true;
        try {
            if(value.CustomInstructionsText!=custom) SetDraftText("CustomInstructionsText",value.CustomInstructionsText);
            if(value.WebSearchTriggersText!=keywords) SetDraftText("WebSearchTriggersText",value.WebSearchTriggersText);
            if(value.ApplicationInstructionsText!=application) SetDraftText("ApplicationInstructionsText",value.ApplicationInstructionsText);
            SetDraftChoice("Language",value.Language);
            SetDraftChoice("SpeechVoice",value.SpeechVoice);
            selectedAppLanguage=language;
        } finally { loading=false; }
        Changed();
    }
    void SetDraftText(string key,string value) {
        var input=(TextBox)inputs[key]; string text=SettingsTheme.EditorText(value);
        if(input.Text!=text) input.Text=text;
    }
    void SetDraftChoice(string key,object value) {
        var input=(ComboBox)inputs[key];
        for(int i=0;i<input.Items.Count;i++) if(object.Equals(((SettingsChoice)input.Items[i]).Value,value)) { input.SelectedIndex=i; return; }
        input.Items.Add(new SettingsChoice(value,()=>L10n.F("ui.currentValue",value))); input.SelectedIndex=input.Items.Count-1;
    }
    void Shortcut(SettingsCard card,string key,string title,string detail,string value,Action<Settings,string> setter) {
        var input=new ShortcutBox(value); input.TextChanged+=delegate { Changed(); }; input.GotFocus+=delegate { UpdateShortcutCapture(); }; input.LostFocus+=delegate { UpdateShortcutCapture(); }; Bind(key,input,s=>setter(s,input.Text)); card.Field(title,detail,input);
    }
    void BuildGeneral() {
        var value=edit.Value;
        var language=Card(0,"ui.language.title");
        language.Field("ui.language.label","ui.language.help",Choice("AppLanguage",value.AppLanguage,L10n.Languages.Select(locale=>new SettingsChoice(locale.Code,()=>locale.NativeName)),(s,v)=>s.AppLanguage=(string)v));
        var keys=Card(0,"ui.general.shortcuts");
        Shortcut(keys,"PushToTalkKey","ui.general.record",null,value.PushToTalkKey,(s,v)=>s.PushToTalkKey=v);
        Shortcut(keys,"CancelKey","ui.general.cancel","ui.general.cancelHelp",value.CancelKey,(s,v)=>s.CancelKey=v);
        Shortcut(keys,"ReplayKey","ui.general.replay","ui.general.replayHelp",value.ReplayKey,(s,v)=>s.ReplayKey=v);
        keys.Add(SettingsTheme.Label("ui.general.sharedShortcuts",true,9));
        var screen=Card(0,"ui.general.capture");
        var capture=Toggle("CaptureEnabled",value.CaptureEnabled,(s,v)=>s.CaptureEnabled=v); screen.Field("ui.general.captureEnabled","ui.general.captureHelp",capture);
        var width=Number("CaptureMaximumWidth",value.CaptureMaximumWidth,320,7680,(s,v)=>s.CaptureMaximumWidth=v); screen.Field("ui.general.imageWidth","ui.general.imageWidthHelp",width);
        var failure=Toggle("AnnounceCaptureFailure",value.AnnounceCaptureFailure,(s,v)=>s.AnnounceCaptureFailure=v); screen.Field("ui.general.captureFailure","ui.general.captureFailureHelp",failure);
        Action update=delegate { width.Enabled=capture.Checked; failure.Enabled=capture.Checked; }; capture.CheckedChanged+=delegate { update(); }; update();
        var hint=SettingsTheme.Label("ui.general.captureFallbackHelp",true,9); screen.Add(hint);
    }
    void BuildAudio() {
        var value=edit.Value; var voice=Card(1,"ui.audio.io");
        var microphones=new List<SettingsChoice> { new SettingsChoice(-1,"ui.audio.defaultMicrophone") };
        if(!preview) {
            try { foreach(string device in WaveRecorder.Devices()) { int colon=device.IndexOf(':'),id; if(colon>0 && int.TryParse(device.Substring(0,colon),out id)) microphones.Add(new SettingsChoice(id,()=>device)); } }
            catch(Exception e) { Log.Write("microphone list: "+e.GetType().Name); }
        }
        voice.Field("ui.audio.microphone",null,Choice("MicrophoneDevice",value.MicrophoneDevice,microphones,(s,v)=>s.MicrophoneDevice=(int)v));
        var speakers=new List<SettingsChoice> { new SettingsChoice("","ui.audio.defaultSpeaker") };
        if(!preview) {
            try { foreach(var device in SpeakerDevices.List()) speakers.Add(new SettingsChoice(device.Id,()=>device.Name)); }
            catch(Exception e) { Log.Write("speaker list: "+e.GetType().Name); }
        }
        if(value.SpeakerDevice.Length>0 && !speakers.Any(s=>object.Equals(s.Value,value.SpeakerDevice))) speakers.Add(new SettingsChoice(value.SpeakerDevice,"ui.audio.disconnectedSpeaker"));
        voice.Field("ui.audio.speaker",null,Choice("SpeakerDevice",value.SpeakerDevice,speakers,(s,v)=>s.SpeakerDevice=(string)v));
        var voices=new List<SettingsChoice> { new SettingsChoice("","ui.audio.automaticVoice") };
        if(!preview) {
            try { using(var synth=new SpeechSynthesizer()) foreach(var item in synth.GetInstalledVoices().Where(v=>v.Enabled)) voices.Add(new SettingsChoice(item.VoiceInfo.Name,()=>item.VoiceInfo.Name)); }
            catch(Exception e) { Log.Write("voice list: "+e.GetType().Name); }
        }
        voice.Field("ui.audio.voice",null,Choice("SpeechVoice",value.SpeechVoice,voices,(s,v)=>s.SpeechVoice=(string)v));
        voice.Field("ui.audio.rate","ui.audio.rateHelp",Number("SpeechRate",value.SpeechRate,-10,10,(s,v)=>s.SpeechRate=v));
        voice.Field("ui.audio.volume","ui.audio.volumeHelp",Number("SpeechVolume",value.SpeechVolume,0,100,(s,v)=>s.SpeechVolume=v));
        var test=SettingsTheme.Button("ui.audio.previewVoice"); test.Click+=delegate { PreviewVoice(); };
        var mixer=SettingsTheme.Button("ui.audio.mixer",false,215); mixer.Click+=delegate { if(!preview) Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute=true }); };
        voice.Buttons(test,mixer); voice.Add(SettingsTheme.Label("ui.audio.routingHelp",true,9));
        var sounds=Card(1,"ui.audio.cues");
        var cues=Toggle("PlayCues",value.PlayCues,(s,v)=>s.PlayCues=v); sounds.Field("ui.audio.recordCues",null,cues);
        var processing=Toggle("PlayProcessingCues",value.PlayProcessingCues,(s,v)=>s.PlayProcessingCues=v); sounds.Field("ui.audio.processingCues","ui.audio.processingCuesHelp",processing);
        var interval=Number("ProcessingCueIntervalSeconds",value.ProcessingCueIntervalSeconds,1,60,(s,v)=>s.ProcessingCueIntervalSeconds=v); sounds.Field("ui.audio.interval",null,interval);
        var volume=Number("CueVolume",value.CueVolume,0,100,(s,v)=>s.CueVolume=v); sounds.Field("ui.audio.cueVolume","ui.audio.volumeHelp",volume);
        Action update=delegate { interval.Enabled=processing.Checked; volume.Enabled=cues.Checked||processing.Checked; }; cues.CheckedChanged+=delegate { update(); }; processing.CheckedChanged+=delegate { update(); }; update();
        var testCue=SettingsTheme.Button("ui.audio.previewCue"); testCue.Click+=delegate { PreviewCue(); }; sounds.Buttons(testCue);
    }
    void BuildSearch() {
        var value=edit.Value; var search=Card(2,"ui.search.title");
        var mode=Choice("WebSearch",value.WebSearch,new[]{new SettingsChoice("on-demand","ui.search.onDemand"),new SettingsChoice("disabled","ui.search.disabled"),new SettingsChoice("live","ui.search.live"),new SettingsChoice("cached","ui.search.cached")},(s,v)=>s.WebSearch=(string)v);
        search.Field("ui.search.mode","ui.search.modeHelp",mode);
        var retry=Toggle("AutoWebSearchFallback",value.AutoWebSearchFallback,(s,v)=>s.AutoWebSearchFallback=v); search.Field("ui.search.fallback","ui.search.fallbackHelp",retry);
        Action update=delegate { retry.Enabled=(string)((SettingsChoice)mode.SelectedItem).Value=="on-demand"; }; mode.SelectedIndexChanged+=delegate { update(); }; update();
        var keywords=Card(2,"ui.search.keywords","ui.search.keywordsHelp");
        var enabled=Toggle("WebSearchTriggersEnabled",value.WebSearchTriggersEnabled,(s,v)=>s.WebSearchTriggersEnabled=v); keywords.Field("ui.search.keywordsEnabled",null,enabled);
        triggers=SettingsTheme.TextBox(value.WebSearchTriggersText,true,L10n.Current.Text("defaults.searchTriggers")); triggers.Height=170; triggers.TextChanged+=delegate { Changed(); }; keywords.Add(triggers);
        Bind("WebSearchTriggersText",triggers,s=>s.WebSearchTriggersText=triggers.Text==SettingsTheme.EditorText(edit.Value.WebSearchTriggersText)?edit.Value.WebSearchTriggersText:triggers.Text);
        keywords.Add(SettingsTheme.Label("ui.search.voicePriority",true,9));
    }
    void BuildInstructions() {
        var value=edit.Value; var card=Card(3,"ui.tabs.instructions","ui.instructions.help");
        var enabled=Toggle("CustomInstructionsEnabled",value.CustomInstructionsEnabled,(s,v)=>s.CustomInstructionsEnabled=v); card.Field("ui.instructions.enabled",null,enabled);
        instructions=SettingsTheme.TextBox(value.CustomInstructionsText,true,L10n.Current.Text("defaults.customInstructions")); instructions.Height=330; instructions.TextChanged+=delegate { Changed(); }; card.Add(instructions);
        Bind("CustomInstructionsText",instructions,s=>s.CustomInstructionsText=instructions.Text==SettingsTheme.EditorText(edit.Value.CustomInstructionsText)?edit.Value.CustomInstructionsText:instructions.Text);
        card.Add(SettingsTheme.Label("ui.instructions.overrideHelp",true,9));
    }
    void BuildHistory() {
        var value=edit.Value; var history=Card(4,"ui.history.title");
        var count=Number("RememberLastTurns",value.RememberLastTurns,0,10,(s,v)=>s.RememberLastTurns=v); history.Field("ui.history.count","ui.history.countHelp",count);
        var debug=Card(4,"ui.history.debug"); debug.Field("ui.history.debugEnabled","ui.history.debugHelp",Toggle("KeepRequestFiles",value.KeepRequestFiles,(s,v)=>s.KeepRequestFiles=v));
        debug.Add(SettingsTheme.Label("ui.history.retentionHelp",true,9));
        var destination=SettingsTheme.Label("",true,9); SettingsTheme.BindLanguage(destination,delegate { destination.Text=L10n.F("ui.history.destination",RequestStorage.RequestsRoot); }); debug.Add(destination);
        var folder=SettingsTheme.Button("ui.history.openFolder",false,200); folder.Click+=delegate {
            if(preview) return;
            try { string path=RequestStorage.RequestsRoot; Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); }
            catch(Exception e) { Error(L10n.F("ui.history.openFolderError",e.Message)); }
        }; debug.Buttons(folder);
    }
    void BuildConnection() {
        if(connection==null) connection=new PlanConnectionPanel(edit.Value,preview); connection.Width=650;
        connection.ModelSelectionChanged+=delegate { Changed(); };
        connection.BeforeConnectionChange=token=>BeforeConnectionChange==null?Task.FromResult(0):BeforeConnectionChange(token);
        connection.ConnectionChanging+=delegate { if(ConnectionChanging!=null) ConnectionChanging(this,EventArgs.Empty); };
        connection.ConnectionUpdated+=delegate { if(ConnectionUpdated!=null) ConnectionUpdated(this,EventArgs.Empty); };
        connection.BusyStateChanged+=delegate { startupRetry.Enabled=!ConnectionBusy; if(ConnectionBusyChanged!=null) ConnectionBusyChanged(this,EventArgs.Empty); };
        pages[5].Controls.Add(connection);
    }
    void BuildAdvanced() {
        var value=edit.Value; var limits=Card(6,"ui.advanced.limits");
        limits.Field("ui.advanced.answerTimeout","ui.advanced.answerTimeoutHelp",Number("RequestTimeoutSeconds",value.RequestTimeoutSeconds,10,600,(s,v)=>s.RequestTimeoutSeconds=v));
        limits.Field("ui.advanced.whisperTimeout","ui.advanced.whisperTimeoutHelp",Number("WhisperTimeoutSeconds",value.WhisperTimeoutSeconds,10,600,(s,v)=>s.WhisperTimeoutSeconds=v));
        limits.Field("ui.advanced.maximumRecording","ui.advanced.maximumRecordingHelp",Number("MaximumRecordingSeconds",value.MaximumRecordingSeconds,1,180,(s,v)=>s.MaximumRecordingSeconds=v));
        limits.Field("ui.advanced.minimumRecording","ui.advanced.minimumRecordingHelp",Number("MinimumRecordingMilliseconds",value.MinimumRecordingMilliseconds,100,180000,(s,v)=>s.MinimumRecordingMilliseconds=v));
        var advanced=Card(6,"ui.advanced.engine");
        var expand=SettingsTheme.Button("ui.advanced.reveal",false,250); expand.Name="advanced-settings-toggle"; advanced.Buttons(expand);
        var engine=Card(6,"ui.advanced.engine");
        engine.Field("ui.advanced.threads","ui.advanced.threadsHelp",Number("WhisperThreads",value.WhisperThreads,1,8,(s,v)=>s.WhisperThreads=v));
        engine.Field("ui.advanced.inputLanguage",null,Choice("Language",value.Language,new[]{new SettingsChoice("ja","ui.language.japanese"),new SettingsChoice("en","English"),new SettingsChoice("auto","ui.language.auto")},(s,v)=>s.Language=(string)v));
        engine.Field("ui.advanced.whisperModel",null,TextInput("WhisperModel",value.WhisperModel,(s,v)=>s.WhisperModel=v));
        engine.Field("ui.advanced.codexPath","ui.advanced.codexPathHelp",TextInput("CodexPath",value.CodexPath,(s,v)=>s.CodexPath=v));
        engine.Field("ui.advanced.codexModel","ui.advanced.codexModelHelp",TextInput("CodexModel",value.CodexModel,(s,v)=>s.CodexModel=v));
        engine.Visible=false; pages[6].Controls.Remove(engine); detachedLanguageControls.Add(engine);
        var application=Card(6,"ui.advanced.applicationInstructions","ui.advanced.applicationInstructionsHelp");
        var applicationText=SettingsTheme.TextBox(value.ApplicationInstructionsText,true); applicationText.Height=300; SettingsTheme.BindLanguage(applicationText,delegate { applicationText.AccessibleName=L10n.T("ui.advanced.applicationInstructions"); }); applicationText.TextChanged+=delegate { Changed(); }; application.Add(applicationText);
        Bind("ApplicationInstructionsText",applicationText,s=>s.ApplicationInstructionsText=applicationText.Text==SettingsTheme.EditorText(edit.Value.ApplicationInstructionsText)?edit.Value.ApplicationInstructionsText:applicationText.Text);
        var reset=SettingsTheme.Button("ui.advanced.reset",false,180); reset.Name="application-instructions-reset";
        reset.Click+=delegate { applicationText.Text=SettingsTheme.EditorText(L10n.For(Draft.AppLanguage).Text("defaults.applicationInstructions")); }; application.Buttons(reset);
        application.Visible=false; pages[6].Controls.Remove(application); detachedLanguageControls.Add(application);
        expand.Click+=delegate {
            // Reveal the inputs in place of the button card for this window's lifetime.
            int index=pages[6].Controls.GetChildIndex(advanced);
            Point scroll=pages[6].AutoScrollPosition;
            pages[6].SuspendLayout();
            try {
                pages[6].Controls.Remove(advanced);
                pages[6].Controls.Add(engine); pages[6].Controls.SetChildIndex(engine,index); engine.Visible=true;
                pages[6].Controls.Add(application); pages[6].Controls.SetChildIndex(application,index+1); application.Visible=true;
                advanced.Dispose();
            } finally { pages[6].ResumeLayout(true); }
            pages[6].AutoScrollPosition=new Point(-scroll.X,-scroll.Y);
        };
        Disposed+=delegate { if(!engine.IsDisposed) engine.Dispose(); if(!application.IsDisposed) application.Dispose(); };
    }
    void Changed() {
        if(loading || instructions==null || triggers==null) return;
        dirty=edit.HasChanges(Draft) || (connection!=null && connection.HasModelChanges);
        save.Enabled=dirty; save.BackColor=dirty?SettingsTheme.Accent:SettingsTheme.Border; close.Text=L10n.T(dirty?"ui.cancel":"ui.close"); feedback.ForeColor=SettingsTheme.Muted; 
        feedback.Text=dirty?L10n.T("ui.unsaved"):"";
    }
    internal bool SaveAndApply() {
        if(connection!=null && connection.IsBusy) { Error(L10n.T("ui.connection.busy")); return false; }
        bool reserved=false; Saved=false;
        try {
            if(BeginApply!=null) { string applyingError=BeginApply(); if(!string.IsNullOrEmpty(applyingError)) { Error(applyingError); return false; } reserved=true; }
            Settings value=Draft; SettingsStorage.Validate(value,root);
            if(checkKeys!=null) { string error=checkKeys(value); if(!string.IsNullOrEmpty(error)) { Error(error); return false; } }
            using(var update=PrepareApply==null?null:PrepareApply(value)) {
                Action apply=update==null?(Action)null:update.Commit;
                edit.Save(value,connection==null?apply:delegate { connection.ApplyModelSelection(apply); });
            }
            if(connection!=null) connection.AcceptModelSelection();
            AppliedSettings=value; Saved=true; L10n.SetLanguage(value.AppLanguage); RefreshLanguage(); Changed();
            if(connection!=null) connection.UpdatePreferences(value);
            feedback.Text=L10n.T("ui.saved"); return true;
        } catch(Exception e) { Error(e.Message); return false; }
        finally { if(reserved && EndApply!=null) EndApply(); }
    }
    internal void RefreshLanguage() {
        // Refresh the same controls, including unrevealed advanced cards. Do not
        // recreate editors: their text, undo buffers and connection state survive.
        bool wasLoading=loading; loading=true;
        var scroll=pages.Select(page=>page.AutoScrollPosition).ToArray();
        SuspendLayout();
        try {
            SettingsTheme.RefreshLanguage(this);
            foreach(var control in detachedLanguageControls) SettingsTheme.RefreshLanguage(control);
            heading.Text=L10n.T(titles[selected]);
            if(connection!=null) connection.RefreshLanguage();
            var instructionHint=instructions as PlaceholderTextBox; if(instructionHint!=null) instructionHint.PlaceholderText=L10n.Current.Text("defaults.customInstructions");
            var triggerHint=triggers as PlaceholderTextBox; if(triggerHint!=null) triggerHint.PlaceholderText=L10n.Current.Text("defaults.searchTriggers");
        } finally { ResumeLayout(true); loading=wasLoading; }
        for(int i=0;i<pages.Count;i++) pages[i].AutoScrollPosition=new Point(-scroll[i].X,-scroll[i].Y);
    }
    void Error(string message) { feedback.ForeColor=SettingsTheme.Error; feedback.Text=message; }
    void ClosingSettings(object sender,FormClosingEventArgs e) {
        if(dirty && !discard && !preview) {
            var answer=MessageBox.Show(this,L10n.T("ui.saveQuestion"),"Minus700K",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Question);
            if(answer==DialogResult.Cancel) { e.Cancel=true; return; }
            if(answer==DialogResult.Yes) { e.Cancel=true; BeginInvoke((MethodInvoker)delegate { if(SaveAndApply()) { discard=true; Close(); } }); return; }
        }
        StopSamples();
    }
    void PreviewVoice() {
        if(preview) return;
        try { StopSamples(); var draft=Draft; sampleVoice=new SpeechPlayback(draft,SpeakerDevices.Resolve(draft.SpeakerDevice)); sampleVoice.SpeakAsync(L10n.For(draft.AppLanguage).Text("ui.audio.sample")); }
        catch(Exception e) { Error(L10n.F("ui.audio.voiceError",e.Message)); }
    }
    async void PreviewCue() {
        if(preview) return;
        try { StopSamples(); var draft=Draft; var output=SpeakerDevices.Resolve(draft.SpeakerDevice); var cue=new CuePlayer(draft.CueVolume,output==null?"":output.Id); sampleCue=cue; bool ready=await System.Threading.Tasks.Task.Run(()=>cue.WaitReady(5000)); if(!IsDisposed && sampleCue==cue && ready) cue.Play(660); }
        catch(Exception e) { if(!IsDisposed) Error(L10n.F("ui.audio.cueError",e.Message)); }
    }
    internal void StopSamples() { if(sampleVoice!=null) { sampleVoice.SpeakAsyncCancelAll(); sampleVoice.Dispose(); sampleVoice=null; } if(sampleCue!=null) { sampleCue.Dispose(); sampleCue=null; } }
    protected override void Dispose(bool disposing) { if(disposing) { StopSamples(); if(Icon!=null) Icon.Dispose(); } base.Dispose(disposing); }
}
}

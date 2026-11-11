using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Minus700K {
// Shared IPC contracts for the resident and its control commands.
internal static class ResidentIpc {
    internal const string InstanceMutex="Local\\Minus700K_PTT_7E940DB1";
    internal const string StopEvent="Local\\Minus700K_Stop_7E940DB1";
    internal const string ConnectEvent="Local\\Minus700K_Connect_7E940DB1";
    internal const string SettingsEvent="Local\\Minus700K_Settings_7E940DB1";
    internal const string InstallingMutex="Local\\Minus700K_Installing_7E940DB1";
}
internal static class Program {
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [STAThread] static int Main(string[] args) {
        SetErrorMode(0x8003); // Inherited by child processes: never show system failure dialogs over a game.
        string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
        Log.Root=root;
        if(args.Length>0 && args[0]=="--install-language") return Installation.ConfigureLanguageCommand(root,args);
        if(args.Length==0 || args[0]=="--post-install" || args[0]=="--settings" || args[0]=="--sign-in") {
            try { using(var installing=Mutex.OpenExisting(ResidentIpc.InstallingMutex)) return 0; }
            catch(WaitHandleCannotBeOpenedException) { }
        }
        bool postInstall=args.Length>0 && args[0]=="--post-install";
        if(postInstall && args.Length!=1) { Log.Write("Invalid post-install command arguments."); return 2; }
        try {
            Directory.CreateDirectory(Path.Combine(root,"runtime"));
            using(AudioSessionIdentity audioIdentity=new AudioSessionIdentity()) {
            if(args.Length>0 && !postInstall) return Diagnostics.Run(root,args);
            bool created; using(Mutex mutex=new Mutex(true,ResidentIpc.InstanceMutex,out created)) {
                if(!created) return postInstall?(Installation.ShowExistingConnectionSettings()?0:1):0;
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                return StartupRecovery.RunResident(root,postInstall);
            }
            }
        } catch(Exception e) { Log.Write("FATAL "+e); try { using(SpeechSynthesizer s=Speech.Create(null)) s.Speak(L10n.T("messages.Program.001")); } catch { } return 1; }
    }
}
internal static class Speech {
    internal static SpeechSynthesizer Create(Settings settings) {
        SpeechSynthesizer s=new SpeechSynthesizer();
        string name=settings==null?"":settings.SpeechVoice;
        var voices=s.GetInstalledVoices().Where(v=>v.Enabled).ToArray();
        string language=LanguageBehavior.VoiceLanguage(settings==null?"ja":settings.AppLanguage);
        if(!voices.Any(v=>v.VoiceInfo.Name==name)) name=voices.Where(v=>v.VoiceInfo.Culture.TwoLetterISOLanguageName==language).Select(v=>v.VoiceInfo.Name).FirstOrDefault();
        // An explicitly selected installed voice takes priority. Automatic choice
        // follows the app language, then the current Windows default voice.
        if(string.IsNullOrEmpty(name) && voices.Length>0) name=voices.Any(v=>v.VoiceInfo.Name==s.Voice.Name)?s.Voice.Name:voices[0].VoiceInfo.Name;
        if(string.IsNullOrEmpty(name)) { s.Dispose(); throw new Exception(L10n.For(settings==null?"ja":settings.AppLanguage).Text("behavior.speech.noVoices")); }
        s.SelectVoice(name); s.Rate=settings==null?1:settings.SpeechRate; s.Volume=settings==null?85:settings.SpeechVolume; s.SetOutputToDefaultAudioDevice(); return s;
    }
}
internal sealed class Recording {
    internal Task<OptionalScreenshot> Image; internal CaptureTarget Target;
    internal ApplicationInfo App=new ApplicationInfo();
    internal int Ended,CaptureFailureCued,Generation;
    internal Stopwatch Duration=Stopwatch.StartNew(); internal DateTimeOffset Started=DateTimeOffset.Now;
    internal CancellationTokenSource Cancellation=new CancellationTokenSource();
}
internal sealed class VoiceContext : ApplicationContext {
    readonly string root; Settings settings; AssistantService service;
    readonly DesktopCapture capture=new DesktopCapture(); WaveRecorder recorder;
    readonly Control dispatcher=new Control(); readonly NotifyIcon tray; SpeechPlayback speech;
    readonly TrayIcons icons=new TrayIcons();
    CuePlayer cues;
    readonly Thread keyboard; Hotkey ptt,cancel,replay; readonly object activeGate=new object(),operationGate=new object();
    readonly Func<int,bool> keyState=Native.Down;
    UpdateShutdown updateShutdown;
    readonly ManualResetEvent settingsIdle=new ManualResetEvent(false);
    readonly EventWaitHandle stopEvent=new EventWaitHandle(false,EventResetMode.ManualReset,ResidentIpc.StopEvent);
    readonly EventWaitHandle connectEvent=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.ConnectEvent);
    readonly EventWaitHandle settingsEvent=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.SettingsEvent);
    CancellationTokenSource active; Recording recording; Task processingTask;
    volatile bool quitting,paused,settingsOpen,keysRegistered,shortcutEditing,applyingSettings,connectionTransition,updatePending; int busy,cancelRequested,accountGeneration,inputEpoch; string lastAnswer="";
    SettingsForm settingsForm,connectionOwner;
    internal string ReplayAnswer { get { return lastAnswer; } }
    readonly object statusGate=new object(); int statusVersion;
    internal VoiceContext(string root,Settings settings,string replayAnswer="",bool initialPaused=false) {
        try {
        lastAnswer=replayAnswer??""; paused=initialPaused;
        this.root=root; this.settings=settings; L10n.SetLanguage(settings.AppLanguage); ptt=Hotkey.Parse(settings.PushToTalkKey); cancel=Hotkey.Parse(settings.CancelKey); replay=Hotkey.Parse(settings.ReplayKey);
        var output=SpeakerDevices.Resolve(settings.SpeakerDevice);
        service=new AssistantService(root,settings); speech=new SpeechPlayback(settings,output); recorder=new WaveRecorder(settings.MicrophoneDevice);
        if(settings.PlayCues || settings.PlayProcessingCues) cues=new CuePlayer(settings.CueVolume,output==null?"":output.Id);
        WireSpeech(speech);
        dispatcher.CreateControl(); IntPtr ignored=dispatcher.Handle;
        // Observe physical key state without claiming exclusive shortcuts.
        keysRegistered=true;
        ContextMenuStrip menu=new ContextMenuStrip();
        menu.Items.Add(L10n.F("messages.Program.005",settings.PushToTalkKey),null,delegate {});
        menu.Items.Add(L10n.F("messages.Program.007",settings.CancelKey),null,delegate { Interlocked.Exchange(ref cancelRequested,1); });
        menu.Items.Add(L10n.F("messages.Program.008",settings.ReplayKey),null,delegate { ReplayLastAnswer(); });
        ToolStripMenuItem pause=new ToolStripMenuItem(L10n.T("messages.Program.009")) { Checked=paused }; pause.Click+=delegate { paused=!paused; pause.Checked=paused; if(paused) Interlocked.Exchange(ref cancelRequested,1); SetState(paused?"paused":"ready"); }; menu.Items.Add(pause);
        menu.Items.Add(L10n.T("messages.Program.010"),null,delegate { service.ClearHistory(); });
        menu.Items.Add(L10n.T("messages.Program.011"),null,delegate { OpenSettings(0); });
        menu.Items.Add(L10n.T("messages.Program.012"),null,delegate { OpenSettings(5); });
        menu.Items.Add(L10n.T("messages.Program.013"),null,delegate { if(Volatile.Read(ref busy)==0) PlanConnectionPanel.OpenUsage(); else Cue(300); });
        ToolStripMenuItem editInstructions=new ToolStripMenuItem(L10n.T("messages.Program.014"));
        editInstructions.Click+=delegate { OpenSettings(3); };
        menu.Items.Add(editInstructions);
        ToolStripMenuItem editSearchTriggers=new ToolStripMenuItem(L10n.T("messages.Program.015"));
        editSearchTriggers.Click+=delegate { OpenSettings(2); };
        menu.Items.Add(editSearchTriggers);
        menu.Items.Add(L10n.T("messages.Program.016"),null,delegate { Process.Start(new ProcessStartInfo("notepad.exe",HiddenProcess.Quote(Path.Combine(root,"config.json"))) { UseShellExecute=true }); });
        menu.Items.Add(L10n.T("messages.Program.017"),null,delegate { ExitThread(); });
        tray=new NotifyIcon { Icon=icons.ForState("ready"),Text="Minus700K",ContextMenuStrip=menu,Visible=true };
        tray.DoubleClick+=delegate { OpenSettings(0); };
        SetState(paused?"paused":"ready"); Log.Write("started pid="+Process.GetCurrentProcess().Id+" ptt="+settings.PushToTalkKey+" replay="+settings.ReplayKey+" memory_turns="+settings.RememberLastTurns+" microphone="+settings.MicrophoneDevice);
        keyboard=new Thread(KeyLoop) { IsBackground=true,Name="Minus700K keys" }; keyboard.Start();
        updateShutdown=new UpdateShutdown(ReadyForUpdate,BeginUpdateWait,EndUpdateWait,delegate { ExitThread(); });
        } catch { quitting=true; if(keyboard!=null) keyboard.Join(2000); ReleaseKeys(); DisposeResources(); throw; }
    }
    void Post(Action action) { if(quitting) return; try { dispatcher.BeginInvoke((MethodInvoker)delegate { if(!quitting) action(); }); } catch(InvalidOperationException) {} }
    internal void OpenConnectionAfterStartup() { Post(delegate { OpenSettings(5); }); }
    void WireSpeech(SpeechPlayback source) {
        source.SpeakStarted+=delegate {
            if(!ReferenceEquals(speech,source)) return;
            if(cues!=null) cues.SetSpeaking(true);
            Post(delegate { if(ReferenceEquals(speech,source) && !paused && Volatile.Read(ref busy)==0 && source.State==SynthesizerState.Speaking) SetState("speaking"); });
        };
        source.SpeakCompleted+=delegate {
            if(!ReferenceEquals(speech,source) || source.State!=SynthesizerState.Ready) return;
            if(cues!=null) cues.SetSpeaking(false);
            if(!quitting && Volatile.Read(ref busy)==0) SetState(paused?"paused":"ready");
        };
    }
    void Say(string text) {
        speech.SpeakAsyncCancelAll(); string spoken=AssistantService.Spoken(text,settings.AppLanguage);
        if(spoken.Length==0) { if(cues!=null) cues.SetSpeaking(false); if(Volatile.Read(ref busy)==0) SetState(paused?"paused":"ready"); return; }
        if(cues!=null) cues.SetSpeaking(true);
        speech.SpeakAsync(spoken); if(Volatile.Read(ref busy)==0) SetState(speech.State==SynthesizerState.Speaking?"speaking":paused?"paused":"ready");
    }
    void ReplayLastAnswer() {
        if(Volatile.Read(ref busy)!=0 || connectionTransition || applyingSettings || updatePending) return;
        if(lastAnswer.Length==0) { Cue(300); return; }
        Say(lastAnswer);
        ThreadPool.QueueUserWorkItem(delegate { Log.Write("last answer replayed"); });
    }
    void OpenSettings(int tab) {
        if(updatePending) return;
        if(settingsForm!=null && !settingsForm.IsDisposed) { settingsForm.SelectTab(tab); settingsForm.Reveal(); return; }
        try {
            SettingsForm form=new SettingsForm(root,settings,null,tab);
            settingsForm=form; settingsOpen=true;
            form.BeginApply=BeginSettingsApply; form.EndApply=EndSettingsApply; form.PrepareApply=PrepareSettingsUpdate;
            form.ShortcutCaptureChanged+=delegate { SetShortcutEditing(form.ShortcutCaptureActive); };
            form.BeforeConnectionChange=PrepareConnectionChange;
            form.ConnectionChanging+=delegate {
                lock(operationGate) {
                    if(connectionTransition && connectionOwner!=form) throw new InvalidOperationException(L10n.T("messages.Program.018"));
                    connectionOwner=form; BeginConnectionTransition();
                }
            };
            form.ConnectionUpdated+=delegate { if(!quitting) { Interlocked.Increment(ref accountGeneration); service.ClearHistory(); lastAnswer=""; } };
            form.ConnectionBusyChanged+=delegate {
                if(!form.ConnectionBusy && connectionTransition && connectionOwner==form && !quitting) {
                    lock(operationGate) { connectionTransition=false; connectionOwner=null; Interlocked.Increment(ref inputEpoch); }
                }
            };
            form.FormClosed+=delegate {
                if(settingsForm==form) settingsForm=null;
                settingsOpen=false;
                if(quitting) return;
                SetShortcutEditing(false);
            };
            form.Show();
        } catch(Exception e) {
            var failed=settingsForm; settingsForm=null; settingsOpen=false;
            if(failed!=null) failed.Dispose(); SetShortcutEditing(false); Log.Write("settings open failed: "+e);
        }
    }
    void SetShortcutEditing(bool value) {
        if(shortcutEditing==value) return;
        lock(operationGate) { shortcutEditing=value; Interlocked.Increment(ref inputEpoch); }
        if(value) ReleaseKeys();
        else if(!quitting && !applyingSettings) keysRegistered=true;
    }
    string BeginSettingsApply() {
        lock(operationGate) {
            if(updatePending) return L10n.T("ui.update.waiting");
            if(connectionTransition) return L10n.T("messages.Program.019");
            if(Interlocked.CompareExchange(ref busy,2,0)!=0) return L10n.F("messages.Program.020",settings.CancelKey);
            settingsIdle.Reset();
            applyingSettings=true; Interlocked.Increment(ref inputEpoch);
        }
        if(!settingsIdle.WaitOne(3000)) { Interlocked.CompareExchange(ref busy,0,2); applyingSettings=false; return L10n.T("messages.Program.022"); }
        ReleaseKeys(); return null;
    }
    void EndSettingsApply() {
        if(!applyingSettings) return;
        if(!shortcutEditing) keysRegistered=true;
        Interlocked.Increment(ref inputEpoch); Interlocked.CompareExchange(ref busy,0,2); applyingSettings=false;
    }
    SettingsRuntimeUpdate PrepareSettingsUpdate(Settings value) {
        if(!applyingSettings || Volatile.Read(ref busy)!=2) throw new InvalidOperationException(L10n.T("messages.Program.023"));
        // The keyboard worker is idle and there is no recording/request. Build
        // replacements before touching the live resources or saving any files.
        var nextPtt=Hotkey.Parse(value.PushToTalkKey); var nextCancel=Hotkey.Parse(value.CancelKey); var nextReplay=Hotkey.Parse(value.ReplayKey);
        var nextService=new AssistantService(root,value);
        SpeechPlayback nextSpeech=null; CuePlayer nextCues=null; WaveRecorder nextRecorder=null;
        Action rollback=delegate { ReleaseKeys(); SafeDispose(nextSpeech); SafeDispose(nextCues); SafeDispose(nextRecorder); };
        try {
            if(settingsForm!=null) settingsForm.StopSamples();
            var output=SpeakerDevices.Resolve(value.SpeakerDevice);
            nextSpeech=new SpeechPlayback(value,output); WireSpeech(nextSpeech);
            if(value.PlayCues || value.PlayProcessingCues) {
                nextCues=new CuePlayer(value.CueVolume,output==null?"":output.Id);
                if(!nextCues.WaitReady(5000)) throw new InvalidOperationException(L10n.T("messages.Program.024"));
            }
            if(value.MicrophoneDevice!=settings.MicrophoneDevice) nextRecorder=new WaveRecorder(value.MicrophoneDevice);
            return new SettingsRuntimeUpdate(delegate {
                // No fallible device creation or file I/O beyond this point.
                var oldSpeech=speech; var oldCues=cues; var oldRecorder=recorder;
                Interlocked.Increment(ref accountGeneration);
                nextService.TransferRecentHistoryFrom(service);
                settings=value; service=nextService; speech=nextSpeech; cues=nextCues;
                if(nextRecorder!=null) recorder=nextRecorder;
                ptt=nextPtt; cancel=nextCancel; replay=nextReplay; keysRegistered=!shortcutEditing;
                nextSpeech=null; nextCues=null; nextRecorder=null;
                SafeDispose(oldSpeech); SafeDispose(oldCues); if(oldRecorder!=recorder) SafeDispose(oldRecorder);
                try {
                    L10n.SetLanguage(value.AppLanguage);
                    RefreshTrayLanguage();
                    SetState(paused?"paused":"ready");
                } catch(Exception e) { Log.Write("settings status refresh: "+e.GetType().Name); }
            },rollback);
        } catch { rollback(); throw; }
    }
    void RefreshTrayLanguage() {
        var items=tray.ContextMenuStrip.Items;
        items[0].Text=L10n.F("messages.Program.005",settings.PushToTalkKey);
        items[1].Text=L10n.F("messages.Program.007",settings.CancelKey);
        items[2].Text=L10n.F("messages.Program.008",settings.ReplayKey);
        string[] keys={"009","010","011","012","013","014","015","016","017"};
        for(int i=0;i<keys.Length;i++) items[i+3].Text=L10n.T("messages.Program."+keys[i]);
    }
    async Task PrepareConnectionChange(CancellationToken token) {
        // Only the keyboard worker changes recording state. Wait for its cancel
        // and the request worker to settle before credentials can change.
        Interlocked.Exchange(ref cancelRequested,1); StopSpeechAndSamples();
        while(Volatile.Read(ref busy)!=0) { token.ThrowIfCancellationRequested(); await Task.Delay(20,token); }
        token.ThrowIfCancellationRequested();
    }
    void BeginConnectionTransition() {
        lock(operationGate) { connectionTransition=true; Interlocked.Increment(ref accountGeneration); Interlocked.Increment(ref inputEpoch); }
        Interlocked.Exchange(ref cancelRequested,1);
    }
    bool ReserveRecording() {
        lock(operationGate) {
            if(!keysRegistered || shortcutEditing || applyingSettings || connectionTransition || paused || updatePending) return false;
            return Interlocked.CompareExchange(ref busy,1,0)==0;
        }
    }
    void StopSpeechAndSamples() {
        speech.SpeakAsyncCancelAll();
        if(settingsForm!=null && !settingsForm.IsDisposed) settingsForm.StopSamples();
    }
    void ReleaseKeys() { keysRegistered=false; }
    void BeginUpdateWait() {
        lock(operationGate) { updatePending=true; Interlocked.Increment(ref inputEpoch); }
        if(settingsForm!=null && !settingsForm.IsDisposed) settingsForm.BeginUpdateWait();
        Log.Write("update requested; waiting for active work");
    }
    void EndUpdateWait() {
        lock(operationGate) { updatePending=false; Interlocked.Increment(ref inputEpoch); }
        if(settingsForm!=null && !settingsForm.IsDisposed) settingsForm.EndUpdateWait();
        Log.Write("update wait released");
    }
    internal bool ReadyForUpdate() {
        return Volatile.Read(ref busy)==0 && recording==null && !applyingSettings && !connectionTransition
            && (processingTask==null || processingTask.IsCompleted)
            && speech.IsIdle && (settingsForm==null || settingsForm.IsDisposed || settingsForm.ShutdownReady);
    }
    void Cue(int hz) { if(!quitting && settings.PlayCues && cues!=null) cues.Play(hz); }
    void ProcessingCues(bool enabled) { if(cues!=null) cues.SetProcessing(enabled && settings.PlayProcessingCues && !quitting,settings.ProcessingCueIntervalSeconds*1000); }
    void CaptureFailureCue(Recording r) { if(Interlocked.Exchange(ref r.CaptureFailureCued,1)==0) { Cue(330); Cue(220); } }
    void SetState(string state) {
        // The install directory can be on SMB. Never let status-file I/O delay key release.
        int version=Interlocked.Increment(ref statusVersion);
        Action write=delegate { lock(statusGate) { if(version!=Volatile.Read(ref statusVersion)) return; try { File.WriteAllText(Path.Combine(root,"runtime","status.json"),new JavaScriptSerializer().Serialize(new { state=state,pid=Process.GetCurrentProcess().Id,updated=DateTimeOffset.Now.ToString("o"),pushToTalk=settings.PushToTalkKey,cancel=settings.CancelKey,replay=settings.ReplayKey,settingsOpen=settingsOpen }),new UTF8Encoding(false)); } catch(Exception e) { Log.Write("status: "+e.Message); } } };
        if(state=="stopped") write(); else Task.Run(write);
        string text=state=="ready"?L10n.T("messages.Program.026"):state=="recording"?L10n.T("messages.Program.027"):state=="recognizing"?L10n.T("messages.Program.028"):state=="asking"?L10n.T("messages.Program.029"):state=="speaking"?L10n.T("messages.Program.030"):state=="paused"?L10n.T("messages.Program.009"):state=="settings"?L10n.T("messages.Program.031"):state;
        Post(delegate {
            if(version!=Volatile.Read(ref statusVersion)) return;
            tray.Text=("Minus700K: "+text).Substring(0,Math.Min(63,("Minus700K: "+text).Length));
            tray.Icon=icons.ForState(state);
        });
    }
    void KeyLoop() {
        bool previousPtt=ptt.IsDown(keyState),previousCancel=cancel.IsDown(keyState),previousReplay=replay.IsDown(keyState);
        int observedEpoch=Volatile.Read(ref inputEpoch);
        try {
            while(!quitting) {
                if(applyingSettings) { settingsIdle.Set(); Thread.Sleep(8); continue; }
                settingsIdle.Reset();
                if(stopEvent.WaitOne(0)) { Post(delegate { ExitThread(); }); break; }
                if(connectEvent.WaitOne(0)) Post(delegate { OpenSettings(5); });
                if(settingsEvent.WaitOne(0)) Post(delegate { OpenSettings(0); });
                bool down=ptt.IsDown(keyState),cancelDown=cancel.IsDown(keyState),replayDown=replay.IsDown(keyState);
                int epoch=Volatile.Read(ref inputEpoch);
                if(epoch!=observedEpoch) { if(recording==null) previousPtt=down; previousCancel=cancelDown; previousReplay=replayDown; observedEpoch=epoch; }
                bool inputAvailable=keysRegistered && !shortcutEditing && !applyingSettings;
                bool cancelNow=(inputAvailable && cancelDown&&!previousCancel) | (Interlocked.Exchange(ref cancelRequested,0)!=0);
                if(cancelNow) Cancel();
                if(inputAvailable && !connectionTransition && !updatePending && down&&!previousPtt&&!paused) {
                    if(ReserveRecording()) BeginRecording(); else if(!shortcutEditing && !connectionTransition && !applyingSettings) Cue(300);
                }
                if(!down&&previousPtt&&recording!=null) FinishRecording();
                if(inputAvailable && !connectionTransition && !updatePending && replayDown&&!previousReplay&&!down&&!cancelDown&&!cancelNow && Volatile.Read(ref busy)==0) { int generation=Volatile.Read(ref accountGeneration); Post(delegate { if(generation==Volatile.Read(ref accountGeneration) && !shortcutEditing) ReplayLastAnswer(); }); }
                if(recording!=null && recording.Duration.Elapsed.TotalSeconds>settings.MaximumRecordingSeconds) { Cancel(); Post(delegate { Say(L10n.T("messages.Program.032")); }); }
                previousPtt=down; previousCancel=cancelDown; previousReplay=replayDown; Thread.Sleep(8);
            }
        } catch(Exception e) { Log.Write("keyloop "+e); Post(delegate { Say(L10n.T("messages.Program.033")); }); }
        finally { if(recording!=null) Cancel(); }
    }
    void BeginRecording() {
        ProcessingCues(false);
        try {
            Recording r=new Recording { Generation=Volatile.Read(ref accountGeneration) }; recording=r; Exception targetError=null;
            try {
                if(settings.CaptureEnabled) r.Target=CaptureTarget.Current(out r.App);
                else r.App=ApplicationInfo.Current();
            } catch(Exception e) { targetError=e; }
            Post(delegate { StopSpeechAndSamples(); if(cues!=null) cues.SetSpeaking(false); });
            // Start the microphone first; expensive GPU/file work never blocks key release monitoring.
            recorder.Start();
            SetState("recording");
            r.Image=Task.Run(delegate {
                OptionalScreenshot image=OptionalScreenshot.Take(delegate { if(targetError!=null) throw targetError; return capture.Capture(r.Target,settings.CaptureMaximumWidth); },r.Cancellation.Token,targetError==null?"capture":"target",settings.CaptureEnabled);
                bool failed=image.Failed;
                Post(delegate { if(!r.Cancellation.IsCancellationRequested && Volatile.Read(ref r.Ended)==0) { if(failed) CaptureFailureCue(r); else Cue(880); } });
                Log.Write("capture result="+image.status+" elapsed_ms="+image.elapsedMilliseconds);
                return image;
            });
        } catch(Exception e) { Log.Write("record start "+e); try { recorder.Stop(); } catch {} if(recording!=null) Discard(recording); recording=null; Interlocked.Exchange(ref busy,0); SetState("ready"); Post(delegate { Say(L10n.T("messages.Program.034")); }); }
    }
    void FinishRecording() {
        Recording r=recording; recording=null; Interlocked.Exchange(ref r.Ended,1);
        byte[] data;
        try { data=recorder.Stop(); r.Duration.Stop(); } catch(Exception e) { Log.Write("record stop "+e); Discard(r); Interlocked.Exchange(ref busy,0); SetState("ready"); Post(delegate { Say(L10n.T("messages.Program.035")); }); return; }
        if(r.Duration.ElapsedMilliseconds<settings.MinimumRecordingMilliseconds || !WaveRecorder.Audible(data)) { Discard(r); Interlocked.Exchange(ref busy,0); SetState("ready"); Cue(300); return; }
        lock(activeGate) active=r.Cancellation;
        Cue(660); SetState("recognizing");
        ProcessingCues(true);
        processingTask=Task.Run(delegate { ProcessRecording(r,data); });
    }
    void ProcessRecording(Recording r,byte[] data) {
        RequestJournal journal=null; bool imageConsumed=false;
        CancellationToken token=r.Cancellation.Token;
        try {
            journal=new RequestJournal(RequestStorage.RequestsRoot,settings.KeepRequestFiles,r.Started);
            string dir=journal.DirectoryPath,wav=Path.Combine(dir,"question.wav");
            journal.Data["gameNameHint"]=settings.GameName; journal.Data["windowTitle"]=r.App.windowTitle;
            journal.Data["captureEnabled"]=settings.CaptureEnabled;
            journal.Data["application"]=r.App;
            if(r.Target!=null) {
                journal.Data["captureBoundsSource"]=r.Target.BoundsSource;
                journal.Data["captureBounds"]=new { x=r.Target.Bounds.X,y=r.Target.Bounds.Y,width=r.Target.Bounds.Width,height=r.Target.Bounds.Height };
            }
            journal.Data["recordingMilliseconds"]=r.Duration.ElapsedMilliseconds;
            journal.Data["audioMilliseconds"]=data.Length/32;
            journal.Data["requestedModel"]=string.IsNullOrWhiteSpace(settings.CodexModel)?"account-default":settings.CodexModel;
            journal.Data["reasoningEffort"]="low"; journal.Data["whisperModel"]=settings.WhisperModel;
            journal.Data["language"]=settings.Language; journal.Data["webSearchConfigured"]=settings.WebSearch;
            journal.Save();
            // Debug saving retains microphone input and the submitted screenshot.
            WaveRecorder.Save(wav,data);
            string imagePath;
            using(OptionalScreenshot image=r.Image.GetAwaiter().GetResult()) {
                imageConsumed=true; token.ThrowIfCancellationRequested(); imagePath=journal.SaveScreenshot(image);
                journal.Data["screenshot"]=image; journal.Data["imageAttached"]=image.attached;
                journal.Data["imageWidth"]=image.width; journal.Data["imageHeight"]=image.height;
                journal.Data["captureMilliseconds"]=image.elapsedMilliseconds;
                if(image.Failed) Post(delegate { if(!token.IsCancellationRequested) { CaptureFailureCue(r); if(settings.AnnounceCaptureFailure) Say(L10n.T("messages.Program.036")); } });
            }
            token.ThrowIfCancellationRequested(); r.App.ResolveDescriptions(); journal.Save();
            token.ThrowIfCancellationRequested(); journal.Phase("transcribing");
            Stopwatch stage=Stopwatch.StartNew(); string question;
            try { question=service.Transcribe(wav,token); }
            finally { journal.Data["transcriptionMilliseconds"]=stage.ElapsedMilliseconds; journal.Save(); }
            SearchDecision search=service.DecideSearch(question);
            journal.Data["webSearch"]=search.effectiveMode; journal.Data["searchDecision"]=search; journal.Data["initialSearchDecision"]=search;
            token.ThrowIfCancellationRequested(); journal.Phase("asking"); SetState("asking");
            stage.Restart(); string answer;
            try {
                AnswerResult result=service.AskWithResult(question,imagePath,r.App,dir,token,search);
                answer=result.Answer; journal.Data["webSearch"]=result.Search.effectiveMode; journal.Data["searchDecision"]=result.Search;
                journal.Data["searchFallback"]=result.Fallback;
                journal.Data["recentMemoryCount"]=result.RecentMemoryCount;
                journal.Data["codexThreadId"]=result.ThreadId;
            }
            finally { journal.Data["codexMilliseconds"]=stage.ElapsedMilliseconds; journal.Save(); }
            ProcessingCues(false);
            token.ThrowIfCancellationRequested(); journal.Phase("answer_received");
            RequestJournal.Text(settings.KeepRequestFiles,dir,"spoken-answer.txt",AssistantService.Spoken(answer,settings.AppLanguage));
            journal.Finish("completed",null);
            Post(delegate { if(!token.IsCancellationRequested && r.Generation==Volatile.Read(ref accountGeneration) && !connectionTransition) { lastAnswer=answer; Say(answer); } });
        } catch(OperationCanceledException e) { ProcessingCues(false); if(journal!=null) journal.Finish("cancelled",e); Log.Write("request cancelled"); }
        catch(Exception e) { ProcessingCues(false); if(journal!=null) journal.Finish("failed",e); Log.Write("request failed "+e.GetType().Name); Post(delegate { if(!token.IsCancellationRequested && r.Generation==Volatile.Read(ref accountGeneration) && !connectionTransition) Say(e is PlanException?e.Message:e is TimeoutException?L10n.T("messages.Program.037"):L10n.T("messages.Program.038")); }); }
        finally {
            ProcessingCues(false);
            if(journal!=null) journal.Dispose();
            if(!imageConsumed && r.Image!=null) r.Image.ContinueWith(t=>{ if(t.Status==TaskStatus.RanToCompletion) t.Result.Dispose(); else if(t.IsFaulted) { var observed=t.Exception; } });
            // Keep the request reserved until the preceding answer/error callback
            // has reached the UI, so saving settings cannot discard a queued answer.
            // Do not dispose its token before those callbacks have completed either.
            Post(delegate {
                lock(activeGate) if(active==r.Cancellation) active=null;
                Interlocked.CompareExchange(ref busy,0,1);
                if(Volatile.Read(ref busy)==0) SetState(paused?"paused":speech.State==SynthesizerState.Speaking?"speaking":"ready");
                r.Cancellation.Dispose();
            });
        }
    }
    void Cancel() {
        ProcessingCues(false);
        Post(delegate { StopSpeechAndSamples(); });
        lock(activeGate) if(active!=null) active.Cancel();
        if(recording!=null) { Recording r=recording; recording=null; try { recorder.Stop(); } catch(Exception e) { Log.Write("cancel stop "+e.Message); } Discard(r); Interlocked.Exchange(ref busy,0); SetState(paused?"paused":"ready"); }
        Cue(400);
    }
    static void Discard(Recording r) { Interlocked.Exchange(ref r.Ended,1); r.Cancellation.Cancel(); if(r.Image!=null) r.Image.ContinueWith(t=>{ if(t.Status==TaskStatus.RanToCompletion) t.Result.Dispose(); else if(t.IsFaulted) Log.Write("discarded capture "+t.Exception.GetBaseException().Message); r.Cancellation.Dispose(); }); else r.Cancellation.Dispose(); }
    protected override void ExitThreadCore() {
        SafeDispose(updateShutdown);
        quitting=true; ProcessingCues(false); lock(activeGate) if(active!=null) active.Cancel();
        if(settingsForm!=null && !settingsForm.IsDisposed) { var form=settingsForm; settingsForm=null; form.Dispose(); }
        ReleaseKeys();
        if(keyboard!=null) keyboard.Join(3000);
        if(processingTask!=null) { try { processingTask.Wait(3000); } catch(AggregateException e) { Log.Write("shutdown worker "+e.GetBaseException().Message); } }
        speech.SpeakAsyncCancelAll(); tray.Visible=false; SetState("stopped"); Log.Write("stopped");
        base.ExitThreadCore();
    }
    static void SafeDispose(IDisposable resource) { try { if(resource!=null) resource.Dispose(); } catch { } }
    void DisposeResources() { SafeDispose(updateShutdown); SafeDispose(cues); SafeDispose(recorder); SafeDispose(capture); SafeDispose(speech); SafeDispose(tray); SafeDispose(icons); SafeDispose(dispatcher); SafeDispose(stopEvent); SafeDispose(connectEvent); SafeDispose(settingsEvent); SafeDispose(settingsIdle); }
    protected override void Dispose(bool disposing) { if(disposing) { if(!quitting) ExitThreadCore(); DisposeResources(); } base.Dispose(disposing); }
}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ModelessSettingsHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const string FixtureInstructions="  fixture instructions  \nsecond line\r\nlast line\r";
    const string FixtureTriggers="# fixture keywords\nfixture keyword\rcurrent information\r\n";
    const string FixtureApplicationInstructions="fixture application rules\r\nkeep screen metadata private\n";
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static readonly List<string> checks=new List<string>();
    static readonly List<object> focusStates=new List<object>();
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr window);
    static object context;
    static object isolatedStore;
    static Type contextType;
    static Form form,other;
    static IDictionary<string,Control> inputs;
    static CancellationTokenSource fakeRequest;
    static TaskCompletionSource<bool> fakeCompletion;
    static Task prepare;
    static byte[] originalConfig;
    static string root,config;
    static IList history;
    static object historyApp;
    static bool passed;
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static object Call(object value,string name,params object[] args) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static string Prompt(string question) {
        object service=Field(context,"service");
        return (string)service.GetType().GetMethods(Instance).Single(m=>m.Name=="BuildPrompt" && m.GetParameters().Length==3).Invoke(service,new object[]{question,historyApp,false});
    }
    static void Check(bool condition,string text) { if(!condition) throw new Exception("Modeless settings: "+text); checks.Add(text); }
    static bool Flag(string name) { return (bool)Field(context,name); }
    static int Busy { get { return (int)Field(context,"busy"); } }
    static void FocusState(string phase) {
        var target=inputs["PushToTalkKey"];
        focusStates.Add(new { phase=phase,activeForm=Form.ActiveForm==form,formFocused=form.Focused,formContainsFocus=form.ContainsFocus,activeControl=form.ActiveControl==null?"null":form.ActiveControl.GetType().Name,targetFocused=target.Focused,targetCanFocus=target.CanFocus,capture=(bool)Property(form,"ShortcutCaptureActive"),shortcutEditing=Flag("shortcutEditing"),keysRegistered=Flag("keysRegistered"),nativeActive=GetActiveWindow().ToInt64(),nativeFocus=GetFocus().ToInt64(),formHandle=form.Handle.ToInt64(),targetHandle=target.Handle.ToInt64() });
    }
    static void Open() {
        Call(context,"OpenSettings",0); form=(Form)Field(context,"settingsForm"); inputs=(IDictionary<string,Control>)Property(form,"Inputs");
        Check(ReferenceEquals(Field(Field(form,"connection"),"store"),isolatedStore),"connection panel uses the isolated empty credential store");
        form.Activated+=delegate { FocusState("activated"); }; form.Deactivate+=delegate { FocusState("deactivated"); };
        inputs["PushToTalkKey"].GotFocus+=delegate { FocusState("got-focus"); }; inputs["PushToTalkKey"].LostFocus+=delegate { FocusState("lost-focus"); };
        FocusState("opened");
    }
    static void CloseDraft() { Set(form,"discard",true); form.Close(); }
    static void ActivateIsolated(Form targetForm) {
        // Form.Activate attempts global foreground activation, which an inactive
        // desktop cannot acquire. SetActiveWindow delivers the real activation
        // messages within this UI thread without changing the input desktop.
        targetForm.Activate(); SetActiveWindow(targetForm.Handle);
    }
    static void FakeBusy() {
        fakeRequest=new CancellationTokenSource(); fakeCompletion=new TaskCompletionSource<bool>();
        Set(context,"active",fakeRequest); Set(context,"processingTask",fakeCompletion.Task); Set(context,"busy",1);
    }
    static void FinishFake() {
        Set(context,"active",null); Set(context,"busy",0); fakeCompletion.TrySetResult(true); fakeRequest.Dispose(); fakeRequest=null;
    }
    static void ExpectDraftUnchanged() { Check(originalConfig.SequenceEqual(File.ReadAllBytes(config)),"fixture config remains unchanged"); }
    static void VerifySilentStartup() {
        // Keep effects enabled to catch an accidental startup tone. Muting only
        // this fixture lets the real output initialize without audible playback.
        object cues=Field(context,"cues");
        Check(cues!=null && (bool)Call(cues,"WaitReady",5000),"effects output initializes without a startup tone");
        Thread.Sleep(300);
        int requested=(int)Field(cues,"sequence"),completed=(int)Property(cues,"CompletedId"),processing=(int)Property(cues,"CompletedProcessingCues");
        report["startupCueRequests"]=requested; report["startupCueCompletions"]=completed; report["startupProcessingCues"]=processing; report["audiblePlayback"]=false;
        Check(requested==0 && completed==0 && processing==0,"startup requests and completes no effect or processing tones");
        Check((string)Property(cues,"Error")=="","silent startup keeps the output healthy");
        Call(context,"Cue",880); Call(context,"Cue",660);
        var wait=Stopwatch.StartNew();
        while((int)Property(cues,"CompletedId")<2 && wait.ElapsedMilliseconds<3000) Thread.Sleep(10);
        Check((int)Field(cues,"sequence")==2 && (int)Property(cues,"CompletedId")==2 && (string)Property(cues,"Error")=="","explicit start and end effects still complete after silent startup");
    }
    static void Step(int step) {
        switch(step) {
        case 0:
            FakeBusy(); Open();
            break;
        case 1:
            Check(form!=null && form.Visible,"settings opens modeless while request is active");
            Check(Busy==1 && !fakeRequest.IsCancellationRequested && !fakeCompletion.Task.IsCompleted,"opening preserves active request and cancellation source");
            Check(!Flag("paused") && Flag("keysRegistered") && !Flag("shortcutEditing"),"initial general tab keeps shortcuts active and does not auto-capture");
            Check((string)Property(context,"ReplayAnswer")=="fixture replay answer" && history.Count==1,"opening preserves replay answer and recent history");
            Call(context,"ReplayLastAnswer");
            Check((string)Property(context,"ReplayAnswer")=="fixture replay answer","busy replay guard keeps the answer without speaking");
            CloseDraft();
            break;
        case 2:
            Check(Field(context,"settingsForm")==null && Busy==1 && !fakeRequest.IsCancellationRequested,"closing settings preserves active request");
            Check((string)Property(context,"ReplayAnswer")=="fixture replay answer" && history.Count==1,"closing preserves replay and history");
            FinishFake(); Open();
            break;
        case 3:
            Check((bool)Call(context,"ReserveRecording") && Busy==1,"F9 admission succeeds with settings open without starting recording");
            Set(context,"busy",0);
            Check(Field(context,"recording")==null,"no microphone recording or screenshot task was started");
            report["shortcutFocusResult"]=inputs["PushToTalkKey"].Focus(); FocusState("after-focus-call");
            break;
        case 4:
            FocusState("capture-assertion");
            Check(Flag("shortcutEditing") && !Flag("keysRegistered") && (bool)Property(form,"ShortcutCaptureActive"),"focusing shortcut input suspends shared shortcut actions");
            Check(!(bool)Call(context,"ReserveRecording") && Busy==0,"shortcut capture rejects new recording");
            other=new Form { Text="Isolated activation fixture",Width=220,Height=120,ShowInTaskbar=false }; other.Show(); ActivateIsolated(other); FocusState("after-native-deactivate");
            break;
        case 5:
            Check(!Flag("shortcutEditing") && Flag("keysRegistered"),"deactivating settings restores shortcuts while shortcut field retains selection");
            Check((bool)Call(context,"ReserveRecording"),"another foreground app can admit recording while settings remains open"); Set(context,"busy",0);
            ActivateIsolated(form); FocusState("after-native-reactivate");
            break;
        case 6:
            FocusState("reactivation-assertion");
            Check(Flag("shortcutEditing") && !Flag("keysRegistered"),"reactivating the focused shortcut field resumes capture");
            Call(form,"SelectTab",0); other.Close(); other.Dispose(); other=null;
            break;
        case 7:
            Check(!Flag("shortcutEditing") && Flag("keysRegistered"),"moving focus away from shortcut input restores shortcuts");
            ((TextBox)inputs["PushToTalkKey"]).Text="F22";
            ((NumericUpDown)inputs["SpeechVolume"]).Value=37;
            FakeBusy(); Check(!(bool)Call(form,"SaveAndApply"),"save rejects a running request");
            Check(Busy==1 && !fakeRequest.IsCancellationRequested && !Flag("applyingSettings") && Flag("keysRegistered"),"rejected busy save leaves runtime gates and request unchanged");
            ExpectDraftUnchanged(); FinishFake();
            break;
        case 8:
            Check(!(bool)Call(form,"SaveAndApply"),"duplicate shortcut is rejected after reserving save");
            Check(Busy==0 && !Flag("applyingSettings") && Flag("keysRegistered"),"invalid save releases reservation and restores shared shortcut actions");
            ExpectDraftUnchanged(); ((TextBox)inputs["PushToTalkKey"]).Text="F21";
            break;
        case 9:
            Check(Call(context,"BeginSettingsApply")==null && Busy==2 && Flag("applyingSettings") && !Flag("keysRegistered"),"save atomically reserves busy state two and suspends shared shortcuts");
            Check(!(bool)Call(context,"ReserveRecording"),"save reservation rejects concurrent recording");
            Check(!string.IsNullOrEmpty((string)Call(context,"BeginSettingsApply")),"second save reservation is rejected");
            Call(context,"EndSettingsApply"); Check(Busy==0 && !Flag("applyingSettings") && Flag("keysRegistered"),"save rollback releases busy and restores keys");
            break;
        case 10:
            FakeBusy(); int generation=(int)Field(context,"accountGeneration"); Call(context,"BeginConnectionTransition");
            Check(Flag("connectionTransition") && (int)Field(context,"accountGeneration")>generation,"connection transition invalidates queued account generation");
            Check(!(bool)Call(context,"ReserveRecording") && !string.IsNullOrEmpty((string)Call(context,"BeginSettingsApply")),"connection transition blocks both new recording and save");
            prepare=(Task)Call(context,"PrepareConnectionChange",CancellationToken.None);
            break;
        case 11:
            Check(fakeRequest.IsCancellationRequested,"connection transition sends cancellation through keyboard worker");
            Check(!prepare.IsCompleted && Busy==1,"connection preparation waits for request completion");
            FinishFake();
            break;
        case 12:
            Check(prepare.IsCompleted && !prepare.IsFaulted && !prepare.IsCanceled,"connection preparation completes after request settles");
            Set(context,"connectionTransition",false); Set(context,"inputEpoch",(int)Field(context,"inputEpoch")+1);
            Check((string)Property(context,"ReplayAnswer")=="fixture replay answer","no account commit was performed in the fixture");
            Call(form,"SelectTab",1); IntPtr savedHandle=form.Handle; var savedBounds=form.Bounds;
            object serviceBeforeFirstApply=Field(context,"service"); IList firstSourceHistory=history; object firstRetainedTurn=history[0];
            Check((bool)Call(form,"SaveAndApply"),"valid idle save applies while settings remains open");
            history=(IList)Field(Field(context,"service"),"history");
            Check(!ReferenceEquals(serviceBeforeFirstApply,Field(context,"service")) && !ReferenceEquals(firstSourceHistory,history) && firstSourceHistory.Count==0 && history.Count==1 && ReferenceEquals(history[0],firstRetainedTurn),"resident settings apply transfers recent memory to its new service and clears the previous service");
            Check(Prompt("直前の質問を教えて").Contains("fixture answer"),"transferred memory remains usable without any saved-history feature");
            Check(form.Visible && !form.IsDisposed && form.Handle==savedHandle && form.Bounds==savedBounds && (int)Field(form,"selected")==1,"save retains native window, bounds, and selected tab");
            Check((int)Field(Field(context,"settings"),"SpeechVolume")==37 && !Flag("quitting"),"runtime preferences change without exiting context");
            var firstSaved=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(config));
            Check((string)Field(Field(context,"settings"),"CustomInstructionsText")==FixtureInstructions && (string)Field(Field(context,"settings"),"WebSearchTriggersText")==FixtureTriggers && (string)firstSaved["CustomInstructionsText"]==FixtureInstructions && (string)firstSaved["WebSearchTriggersText"]==FixtureTriggers,"unrelated in-place save preserves inline text and mixed line endings in runtime and config");
            Check((string)Field(Field(context,"settings"),"ApplicationInstructionsText")==FixtureApplicationInstructions && (string)firstSaved["ApplicationInstructionsText"]==FixtureApplicationInstructions,"hidden application instructions survive unrelated in-place save");
            Check(Busy==0 && !Flag("applyingSettings") && Flag("keysRegistered") && (bool)Call(context,"ReserveRecording"),"F9 admission resumes after in-place apply"); Set(context,"busy",0);
            Check((string)Property(context,"ReplayAnswer")=="fixture replay answer","save preserves last answer for replay");
            Check(!originalConfig.SequenceEqual(File.ReadAllBytes(config)),"save writes only the fixture config");
            break;
        case 13:
            object serviceBeforeReduction=Field(context,"service");
            Call(serviceBeforeReduction,"Remember",historyApp,"second fixture question","second fixture answer",false);
            Call(serviceBeforeReduction,"Remember",historyApp,"third fixture question","third fixture answer",false);
            IList sourceBeforeReduction=history;
            Check(sourceBeforeReduction.Count==3 && inputs["RememberLastTurns"].Enabled,"memory retains three exchanges and its count stays editable while saving is off");
            ((NumericUpDown)inputs["RememberLastTurns"]).Value=1;
            ((NumericUpDown)inputs["SpeechVolume"]).Value=38;
            ((CheckBox)inputs["CustomInstructionsEnabled"]).Checked=false; ((CheckBox)inputs["WebSearchTriggersEnabled"]).Checked=false;
            ((TextBox)inputs["CustomInstructionsText"]).Text="edited while disabled\r\n"; ((TextBox)inputs["WebSearchTriggersText"]).Text="new fixture keyword\r\n";
            ((TextBox)inputs["ApplicationInstructionsText"]).Text="updated application rule\r\n";
            Check(inputs["CustomInstructionsText"].Enabled && inputs["WebSearchTriggersText"].Enabled,"disabled inline instruction and keyword editors remain editable");
            Check((bool)Call(form,"SaveAndApply") && (int)Field(Field(context,"settings"),"SpeechVolume")==38,"same settings window applies a second edit");
            history=(IList)Field(Field(context,"service"),"history");
            Check(!ReferenceEquals(serviceBeforeReduction,Field(context,"service")) && sourceBeforeReduction.Count==0 && history.Count==1 && (string)Field(history[0],"Answer")=="third fixture answer","resident immediately trims transferred memory when the applied limit is reduced");
            Check(!Prompt("2つ前の質問を教えて").Contains("second fixture answer"),"older exchange is unavailable after the resident applies the smaller limit");
            object secondSettings=Field(context,"settings"); var secondSaved=(Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(config));
            Check((int)Field(secondSettings,"RememberLastTurns")==1 && Convert.ToInt32(secondSaved["RememberLastTurns"])==1,"reduced memory count is applied to runtime and config");
            Check((string)Field(secondSettings,"ApplicationInstructionsText")=="updated application rule\r\n" && (string)secondSaved["ApplicationInstructionsText"]=="updated application rule\r\n" && (string)Field(Field(Field(context,"service"),"settings"),"ApplicationInstructionsText")=="updated application rule\r\n","application instruction edits apply to the next assistant service without restart while custom instructions are OFF");
            Check(!(bool)Field(secondSettings,"CustomInstructionsEnabled") && !(bool)Field(secondSettings,"WebSearchTriggersEnabled") && (string)Field(secondSettings,"CustomInstructionsText")=="edited while disabled\r\n" && (string)Field(secondSettings,"WebSearchTriggersText")=="new fixture keyword\r\n","second in-place apply updates inline text and both OFF flags in runtime");
            Check(!(bool)secondSaved["CustomInstructionsEnabled"] && !(bool)secondSaved["WebSearchTriggersEnabled"] && (string)secondSaved["CustomInstructionsText"]=="edited while disabled\r\n" && (string)secondSaved["WebSearchTriggersText"]=="new fixture keyword\r\n" && !File.Exists(Path.Combine(root,"custom-instructions.txt")) && !File.Exists(Path.Combine(root,"web-search-triggers.txt")),"second save stores disabled text in config without separate text files");
            ((TextBox)inputs["PushToTalkKey"]).Text="F22";
            Check(!(bool)Call(form,"SaveAndApply") && Busy==0 && !Flag("applyingSettings") && Flag("keysRegistered"),"invalid edit after successful save still restores hotkeys and busy state");
            ((TextBox)inputs["PushToTalkKey"]).Text="F21";
            break;
        case 14:
            byte[] beforeDevice=File.ReadAllBytes(config); object oldSpeech=Field(context,"speech");
            object serviceBeforeFailedApply=Field(context,"service"); IList historyBeforeFailedApply=history;
            ((NumericUpDown)inputs["RememberLastTurns"]).Value=0;
            var microphone=(ComboBox)inputs["MicrophoneDevice"]; int previousMicrophone=microphone.SelectedIndex;
            Type choice=contextType.Assembly.GetType("Minus700K.SettingsChoice");
            // 0xffff is accepted as the legacy WAVE_MAPPER on some drivers.
            microphone.Items.Add(choice.GetConstructors(Instance)[0].Invoke(new object[]{32767,"fixture unavailable microphone"})); microphone.SelectedIndex=microphone.Items.Count-1;
            Check(!(bool)Call(form,"SaveAndApply"),"unavailable input device rejects runtime preparation");
            Check(beforeDevice.SequenceEqual(File.ReadAllBytes(config)) && ReferenceEquals(oldSpeech,Field(context,"speech")) && Busy==0 && Flag("keysRegistered") && !Flag("applyingSettings"),"failed device preparation preserves applied settings and active audio resources");
            Check(ReferenceEquals(serviceBeforeFailedApply,Field(context,"service")) && historyBeforeFailedApply.Count==1 && (int)Field(Field(context,"settings"),"RememberLastTurns")==1,"failed settings apply preserves the active memory and its previous limit");
            microphone.SelectedIndex=previousMicrophone;
            Check((bool)Call(context,"ReserveRecording"),"recording can be admitted after apply failure"); Set(context,"busy",0);
            Check((bool)Call(form,"SaveAndApply"),"zero-memory setting applies after correcting the unavailable device");
            history=(IList)Field(Field(context,"service"),"history");
            Check(!ReferenceEquals(serviceBeforeFailedApply,Field(context,"service")) && historyBeforeFailedApply.Count==0 && history.Count==0 && (int)Field(Field(context,"settings"),"RememberLastTurns")==0,"resident discards prior memory from both services when zero is applied");
            Call(Field(context,"service"),"Remember",historyApp,"disabled fixture question","disabled fixture answer",false);
            Check(history.Count==0 && !Prompt("直前の質問を教えて").Contains("disabled fixture answer"),"resident does not retain or recall new exchanges after applying zero");
            CloseDraft();
            Check(!Flag("quitting") && Field(context,"settingsForm")==null && (int)Field(Field(context,"settings"),"SpeechVolume")==38,"closing saved settings keeps resident running with last applied values");
            passed=true; ((ApplicationContext)context).ExitThread();
            break;
        }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); string output=Path.GetFullPath(args[2]); config=Path.Combine(root,"config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)); report["checks"]=checks; report["fixtureRoot"]=root; report["focusStates"]=focusStates;
        Mutex resident=null; bool residentReserved=false;
        try {
            // Hold the resident identity for this fixture: refuse an active app,
            // and prevent another launch from sharing the keyboard/control events.
            resident=new Mutex(false,"Local\\Minus700K_PTT_7E940DB1");
            try { residentReserved=resident.WaitOne(0); } catch(AbandonedMutexException) { residentReserved=true; }
            if(!residentReserved) throw new InvalidOperationException("Stop the resident before running this integration fixture.");
            if(File.Exists(config)) throw new InvalidOperationException("Use a new empty fixture directory.");
            Directory.CreateDirectory(Path.Combine(root,"runtime")); Directory.CreateDirectory(Path.Combine(root,"vendor","whisper"));
            File.WriteAllBytes(Path.Combine(root,"vendor","whisper","whisper-cli.exe"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"model.bin"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"codex.exe"),new byte[]{0});
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0])); assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            var ipc=assembly.GetType("Minus700K.ResidentIpc",true);
            Check((string)ipc.GetField("InstanceMutex",Static).GetRawConstantValue()=="Local\\Minus700K_PTT_7E940DB1" && (string)ipc.GetField("StopEvent",Static).GetRawConstantValue()=="Local\\Minus700K_Stop_7E940DB1","resident and fixture share the current Minus700K IPC contracts");
            // This harness targets the application's .NET Framework runtime,
            // where reflection can replace a readonly reference in this process.
            // Replace it before constructing any service or connection panel.
            var storeType=assembly.GetType("Minus700K.PlanStore",true);
            isolatedStore=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,isolatedStore);
            Check(ReferenceEquals(storeType.GetField("Default",Static).GetValue(null),isolatedStore),"default credential store is isolated before UI construction");
            report["realCredentialsAccessed"]=false;
            var settings=Activator.CreateInstance(assembly.GetType("Minus700K.Settings",true));
            Set(settings,"PushToTalkKey","F21"); Set(settings,"CancelKey","F22"); Set(settings,"ReplayKey","F23"); Set(settings,"WhisperModel","model.bin"); Set(settings,"CodexPath",Path.Combine(root,"codex.exe"));
            Set(settings,"PlayCues",true); Set(settings,"PlayProcessingCues",false); Set(settings,"CueVolume",0); Set(settings,"SpeechVolume",0);
            Set(settings,"CustomInstructionsText",FixtureInstructions); Set(settings,"WebSearchTriggersText",FixtureTriggers);
            Set(settings,"ApplicationInstructionsText",FixtureApplicationInstructions);
            File.WriteAllText(config,new JavaScriptSerializer().Serialize(settings),new UTF8Encoding(false)); originalConfig=File.ReadAllBytes(config);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            contextType=assembly.GetType("Minus700K.VoiceContext",true); context=contextType.GetConstructors(Instance)[0].Invoke(new object[]{root,settings,"fixture replay answer",false});
            VerifySilentStartup();
            history=(IList)Field(Field(context,"service"),"history");
            historyApp=Activator.CreateInstance(assembly.GetType("Minus700K.ApplicationInfo",true),true); Set(historyApp,"WindowHandle",101L); Set(historyApp,"processId",11u); Set(historyApp,"windowTitle","Memory fixture");
            Call(Field(context,"service"),"Remember",historyApp,"fixture question","fixture answer",false);
            Check(history.Count==1,"resident retains recent memory with saving and saved recall disabled");
            int step=0;
            using(var timer=new System.Windows.Forms.Timer{Interval=180}) {
                timer.Tick+=delegate {
                    try { Step(step++); }
                    catch(Exception e) {
                        timer.Stop(); report["error"]=e.ToString();
                        if(form!=null && !form.IsDisposed) report["settingsFeedback"]=((Control)Field(form,"feedback")).Text;
                        if(fakeCompletion!=null) fakeCompletion.TrySetResult(true); ((ApplicationContext)context).ExitThread();
                    }
                };
                timer.Start(); Application.Run((ApplicationContext)context); timer.Stop();
            }
            report["recordingEverStarted"]=Field(context,"recording")!=null;
        } catch(Exception e) { report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString(); }
        finally {
            try {
                if(other!=null) other.Dispose();
                if(context!=null) ((IDisposable)context).Dispose();
                if(fakeRequest!=null) fakeRequest.Dispose();
            } catch(Exception cleanupError) { passed=false; report["cleanupError"]=cleanupError.ToString(); }
            finally { if(residentReserved) resident.ReleaseMutex(); if(resident!=null) resident.Dispose(); }
            report["passed"]=passed; File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        }
        return passed?0:1;
    }
}

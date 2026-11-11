using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Runs on a private desktop with fixture credentials and synthetic in-flight
// work. No keys are injected, no microphone recording begins, and no AI is used.
internal static class UpdateShutdownHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const string ResidentMutex="Local\\Minus700K_PTT_7E940DB1";
    static readonly List<string> checks=new List<string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static Assembly assembly;
    static Type formType,updateType;
    static string root,config,mode;
    static byte[] configBefore;
    static object resident,coordinator;
    static Form form;
    static ApplicationContext coordinatorLoop;
    static EventWaitHandle signal;
    static CancellationTokenSource request;
    static TaskCompletionSource<bool> requestCompletion;
    static bool heartbeat,completed,coordinatorIdle;
    static int step,begins,resumes,shutdowns;
    static Stopwatch total,phase;
    static System.Windows.Forms.Timer driver;

    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static object Call(object value,string name,params object[] args) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static bool Flag(string name) { return (bool)Field(resident,name); }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks.Add(message); }
    static void Advance() { step++; phase.Restart(); }
    static bool After(int milliseconds) { return phase.ElapsedMilliseconds>=milliseconds; }
    static Form FindSettings() { return Application.OpenForms.Cast<Form>().FirstOrDefault(f=>f.GetType()==formType && !f.IsDisposed); }
    static void Pulse() {
        if(signal==null) {
            string name=(string)updateType.GetMethod("SignalName",Static).Invoke(null,new object[]{Process.GetCurrentProcess().Id});
            signal=EventWaitHandle.OpenExisting(name); report["signalName"]=name;
            Check(name.EndsWith("_"+Process.GetCurrentProcess().Id),"Update signal is scoped to this process ID");
        }
        signal.Set();
    }
    static void DirtySettings() {
        var inputs=(IDictionary<string,Control>)Property(form,"Inputs");
        ((NumericUpDown)inputs["SpeechVolume"]).Value=41;
        Check((int)Field(Property(form,"Draft"),"SpeechVolume")==41,"The fixture has an unsaved settings draft");
        Check(configBefore.SequenceEqual(File.ReadAllBytes(config)),"Editing a draft leaves saved configuration unchanged");
    }
    static void SetPendingSpeech(bool value) {
        object speech=Field(resident,"speech");
        FieldInfo pending=speech.GetType().GetField("pending",Instance);
        if(pending==null) throw new MissingFieldException("SpeechPlayback.pending");
        pending.SetValue(speech,value?Activator.CreateInstance(pending.FieldType,new object[]{"Unsubmitted synthetic speech prompt"}):null);
    }
    static void ObserveCoordinator() {
        switch(step) {
        case 0:
            heartbeat=true; Advance(); break;
        case 1:
            if(!(bool)Property(coordinator,"Pending")) return;
            Check(begins==1 && resumes==0 && shutdowns==0,"First heartbeat starts update waiting once"); Advance(); break;
        case 2:
            if(!After(350)) return;
            Check(begins==1 && shutdowns==0,"Repeated heartbeats do not interrupt unfinished work or repeat begin");
            heartbeat=false; Advance(); break;
        case 3:
            if((bool)Property(coordinator,"Pending")) return;
            Check(resumes==1 && shutdowns==0,"Expired heartbeat resumes work without terminating the app");
            heartbeat=true; Advance(); break;
        case 4:
            if(!(bool)Property(coordinator,"Pending")) return;
            Check(begins==2 && resumes==1,"A new installer heartbeat can request waiting again");
            coordinatorIdle=true; Advance(); break;
        case 5:
            break;
        }
    }
    static void ObserveResident() {
        switch(step) {
        case 0:
            request=new CancellationTokenSource(); requestCompletion=new TaskCompletionSource<bool>();
            Set(resident,"active",request); Set(resident,"processingTask",requestCompletion.Task); Set(resident,"busy",1);
            Call(resident,"OpenSettings",0); form=FindSettings();
            Check(form!=null,"Resident settings can be inspected during the synthetic request");
            DirtySettings(); heartbeat=true; Advance(); break;
        case 1:
            if(!Flag("updatePending")) return;
            Check(!request.IsCancellationRequested && !requestCompletion.Task.IsCompleted,"Update does not cancel the active request");
            Check(!(bool)Call(resident,"ReserveRecording"),"Update waiting rejects new recording");
            Check(!string.IsNullOrEmpty((string)Call(resident,"BeginSettingsApply")),"Update waiting rejects applying new settings");
            Call(resident,"ReplayLastAnswer");
            Check((bool)Property(Field(resident,"speech"),"IsIdle"),"Update waiting does not start replay");
            Check(!Flag("quitting") && !form.IsDisposed,"Unfinished work keeps the resident and its draft alive");
            heartbeat=false; Advance(); break;
        case 2:
            if(Flag("updatePending")) return;
            Check(!request.IsCancellationRequested && !requestCompletion.Task.IsCompleted,"Installer cancellation preserves the active request");
            Check(form.Enabled && Flag("keysRegistered"),"Lease expiry restores settings and shortcut input");
            Set(resident,"busy",0);
            Check((bool)Call(resident,"ReserveRecording"),"Lease expiry restores recording admission");
            Set(resident,"busy",1); heartbeat=true; Advance(); break;
        case 3:
            if(!Flag("updatePending")) return;
            Set(resident,"busy",0); Advance(); break;
        case 4:
            if(!After(350)) return;
            Check(!Flag("quitting") && !(bool)Call(resident,"ReadyForUpdate"),"Update waits until the processing task itself completes");
            SetPendingSpeech(true); requestCompletion.TrySetResult(true); Set(resident,"active",null); Advance(); break;
        case 5:
            if(!After(350)) return;
            Check(!Flag("quitting") && !(bool)Call(resident,"ReadyForUpdate"),"Queued speech is completed before the app exits for an update");
            Check(!request.IsCancellationRequested,"Finished request was never cancelled by update waiting");
            Check(configBefore.SequenceEqual(File.ReadAllBytes(config)),"Unsaved settings have not been written while waiting");
            SetPendingSpeech(false); completed=true; Advance(); break;
        case 6:
            break;
        }
    }
    static void ObserveSettings() {
        switch(step) {
        case 0:
            form=FindSettings(); if(form==null || !form.Visible) return;
            Check(form.Visible,"Settings are visible before update is requested");
            if(mode=="recovery") Check(form.Controls.Find("StartupRetry",true).Single().Visible,"Recovery settings are used for the failed-startup case");
            DirtySettings();
            Call(Field(form,"connection"),"Busy",true);
            heartbeat=true; Advance(); break;
        case 1:
            if(!After(450)) return;
            Check(!form.IsDisposed && !(bool)Property(form,"ShutdownReady"),"Standalone settings wait for active connection work");
            heartbeat=false; Advance(); break;
        case 2:
            if(!After(2500)) return;
            Check(!form.IsDisposed && form.Enabled,"Interrupted installer heartbeat restores the settings window");
            Check(configBefore.SequenceEqual(File.ReadAllBytes(config)),"Interrupted update preserves saved settings and draft separation");
            heartbeat=true; Advance(); break;
        case 3:
            if(!After(350)) return;
            Check(!form.IsDisposed,"Renewed update still waits for connection work");
            Call(Field(form,"connection"),"Busy",false);
            completed=true; Advance(); break;
        case 4:
            break;
        }
    }
    static void Fail(Exception error) {
        report["error"]=(error is TargetInvocationException && error.InnerException!=null?error.InnerException:error).ToString();
        heartbeat=false; driver.Stop();
        if(resident!=null) {
            if(requestCompletion!=null) requestCompletion.TrySetResult(true);
            Set(resident,"active",null); ((ApplicationContext)resident).ExitThread();
        } else if(coordinatorLoop!=null) coordinatorLoop.ExitThread();
        else { var current=FindSettings(); if(current!=null) { Set(current,"discard",true); current.Close(); } }
    }
    static void Drive() {
        driver=new System.Windows.Forms.Timer { Interval=80 };
        total=Stopwatch.StartNew(); phase=Stopwatch.StartNew();
        driver.Tick+=delegate {
            try {
                if(total.ElapsedMilliseconds>14000) throw new TimeoutException(mode+" timed out at step "+step);
                if(heartbeat) Pulse();
                if(mode=="coordinator") ObserveCoordinator();
                else if(mode=="resident") ObserveResident();
                else ObserveSettings();
            } catch(Exception error) { Fail(error); }
        };
        driver.Start();
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); config=Path.Combine(root,"config.json"); string output=Path.GetFullPath(args[2]);
        report["checks"]=checks; report["fixtureRoot"]=root; report["realCredentialsAccessed"]=false;
        Mutex reservation=null; bool ownsReservation=false;
        try {
            mode=File.ReadAllText(Path.Combine(root,"update-test.txt")).Trim(); report["mode"]=mode;
            try { using(Mutex existing=Mutex.OpenExisting(ResidentMutex)) { throw new InvalidOperationException("An application already exists; this test will not stop it."); } }
            catch(WaitHandleCannotBeOpenedException) { }
            assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            formType=assembly.GetType("Minus700K.SettingsForm",true); updateType=assembly.GetType("Minus700K.UpdateShutdown",true);
            assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            var storeType=assembly.GetType("Minus700K.PlanStore",true);
            var isolatedStore=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,isolatedStore);
            Check(ReferenceEquals(storeType.GetField("Default",Static).GetValue(null),isolatedStore),"Credential store is isolated before creating application components");
            configBefore=File.ReadAllBytes(config);
            if(mode=="coordinator" || mode=="resident") {
                reservation=new Mutex(false,ResidentMutex); ownsReservation=reservation.WaitOne(0);
                if(!ownsReservation) throw new InvalidOperationException("Another resident started before this fixture.");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if(mode=="coordinator") {
                    coordinatorLoop=new ApplicationContext();
                    coordinator=updateType.GetConstructors(Instance).Single(c=>c.GetParameters().Length==4).Invoke(new object[]{
                        (Func<bool>)(()=>coordinatorIdle),(Action)(()=>begins++),(Action)(()=>resumes++),(Action)delegate {
                            shutdowns++; Check(coordinatorIdle && begins==2 && shutdowns==1,"Update exits exactly once after work becomes idle");
                            completed=true; coordinatorLoop.ExitThread();
                        }});
                    Drive(); Application.Run(coordinatorLoop);
                } else {
                    var settings=assembly.GetType("Minus700K.Settings",true).GetMethod("Load",Static).Invoke(null,new object[]{root});
                    var contextType=assembly.GetType("Minus700K.VoiceContext",true);
                    resident=contextType.GetConstructors(Instance)[0].Invoke(new object[]{root,settings,"fixture answer",false});
                    Drive(); Application.Run((ApplicationContext)resident);
                    Check(Flag("quitting") && form.IsDisposed,"Update exits the resident and discards the unsaved settings window");
                    Check(Field(resident,"recording")==null,"No microphone recording was started by the fixture");
                }
            } else {
                Check(string.Equals(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName,root,StringComparison.OrdinalIgnoreCase),"Program entry point uses this isolated installation root");
                EventHandler idle=null; idle=delegate { Application.Idle-=idle; Drive(); };
                Application.Idle+=idle;
                try {
                    var main=assembly.GetType("Minus700K.Program",true).GetMethod("Main",Static);
                    int code=(int)main.Invoke(null,new object[]{new[]{mode=="recovery"?"--post-install":"--settings"}});
                    Check(code==0 || (mode=="recovery" && code==1),"Settings update exits without an unhandled startup failure");
                } finally { Application.Idle-=idle; }
                Check(form!=null && form.IsDisposed,"Update closes standalone or recovery settings without an unsaved-draft prompt");
            }
            Check(completed,"All update waiting observations completed");
            Check(configBefore.SequenceEqual(File.ReadAllBytes(config)),"Automatic update discards unsaved edits without changing saved configuration");
            Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"Update verification does not create credentials");
        } catch(Exception error) { report["error"]=(error is TargetInvocationException && error.InnerException!=null?error.InnerException:error).ToString(); }
        finally {
            if(driver!=null) driver.Dispose();
            if(signal!=null) signal.Dispose();
            if(coordinator!=null) ((IDisposable)coordinator).Dispose();
            if(coordinatorLoop!=null) coordinatorLoop.Dispose();
            if(resident!=null) ((IDisposable)resident).Dispose();
            if(request!=null) request.Dispose();
            if(reservation!=null) { if(ownsReservation) reservation.ReleaseMutex(); reservation.Dispose(); }
        }
        report["passed"]=completed && !report.ContainsKey("error");
        Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        return (bool)report["passed"]?0:1;
    }
}

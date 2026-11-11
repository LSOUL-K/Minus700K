using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class SharedShortcutsHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(uint style,string className,string name,uint windowStyle,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll",SetLastError=true)] static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window,int id);
    static readonly List<string> checks=new List<string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static readonly List<int> keys=new List<int>();
    static readonly int[] state=new int[256];
    static object context,isolatedStore;
    static Form form;
    static IntPtr owner,probe;
    static string root,config;
    static int step,replayStarts,baseline;
    static bool passed;
    static CancellationTokenSource pendingRequest;
    static Stopwatch total,phase;
    static byte[] originalConfig;
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Call(object value,string name,params object[] args) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static void Check(bool value,string message) { if(!value) throw new Exception(message); checks.Add(message); }
    static string Shortcut(int key) { return "Ctrl+Alt+"+((Keys)key).ToString(); }
    static bool InputEnabled { get { return (bool)Field(context,"keysRegistered"); } }
    static int Starts { get { return Volatile.Read(ref replayStarts); } }
    static void Press(int key) { Volatile.Write(ref state[17],1); Volatile.Write(ref state[18],1); Volatile.Write(ref state[key],1); }
    static void Release() { for(int i=0;i<state.Length;i++) Volatile.Write(ref state[i],0); }
    static bool ProbeCanRegister(int key) {
        bool success=RegisterHotKey(probe,90,0x4003,(uint)key);
        if(success) UnregisterHotKey(probe,90);
        return success;
    }
    static void CheckOtherOwner() {
        foreach(int key in keys) Check(!ProbeCanRegister(key),"Other window retains ownership of "+Shortcut(key));
    }
    static void ObserveSpeech() {
        object speech=Field(context,"speech");
        speech.GetType().GetEvent("SpeakStarted",Instance).GetAddMethod(true).Invoke(speech,new object[]{(EventHandler)delegate { Interlocked.Increment(ref replayStarts); }});
    }
    static void Next() { step++; phase.Restart(); }
    static void Tick(System.Windows.Forms.Timer timer) {
        if(total.ElapsedMilliseconds>20000) throw new TimeoutException("Shared shortcut observation timed out at step "+step);
        switch(step) {
        case 0:
            Check(InputEnabled && !(bool)Field(context,"quitting"),"Resident starts with all configured shortcuts already owned by another window");
            CheckOtherOwner(); baseline=Starts; Press(keys[2]); Next(); break;
        case 1:
            if(Starts==baseline) return;
            Check(Starts==baseline+1,"Polling executes replay while another window owns the same shortcut");
            Next(); break;
        case 2:
            if(phase.ElapsedMilliseconds<400) return;
            Check(Starts==baseline+1,"Holding the shared shortcut does not repeat the replay action");
            Release(); Next(); break;
        case 3:
            if(phase.ElapsedMilliseconds<100) return;
            baseline=Starts; Press(keys[2]); Next(); break;
        case 4:
            if(Starts==baseline) return;
            Check(Starts==baseline+1,"A release and second press executes the shared shortcut again");
            Release(); pendingRequest=new CancellationTokenSource(); Set(context,"active",pendingRequest); Set(context,"busy",1); Next(); break;
        case 5:
            if(phase.ElapsedMilliseconds<100) return;
            baseline=Starts; Press(keys[1]); Volatile.Write(ref state[keys[2]],1); Next(); break;
        case 6:
            if(!pendingRequest.IsCancellationRequested) return;
            Check(pendingRequest.IsCancellationRequested,"The shared cancel shortcut reaches the active request");
            Check(Starts==baseline,"Cancel takes priority over a simultaneous replay press");
            Release(); Set(context,"active",null); Set(context,"busy",0); pendingRequest.Dispose(); pendingRequest=null;
            Call(context,"SetShortcutEditing",true); Press(keys[2]); Next(); break;
        case 7:
            if(phase.ElapsedMilliseconds<200) return;
            Check(!InputEnabled && Starts==baseline,"Shortcut editing suppresses shared shortcut actions");
            Call(context,"SetShortcutEditing",false); Next(); break;
        case 8:
            if(phase.ElapsedMilliseconds<200) return;
            Check(InputEnabled && Starts==baseline,"Resuming input while a key is held does not spuriously trigger replay");
            Release(); Next(); break;
        case 9:
            if(phase.ElapsedMilliseconds<100) return;
            Press(keys[2]); Next(); break;
        case 10:
            if(Starts==baseline) return;
            Check(Starts==baseline+1,"Shared shortcuts resume after a fresh press following editing");
            Release(); Call(context,"OpenSettings",0); form=(Form)Field(context,"settingsForm");
            Check(form!=null && form.Visible,"Resident settings open while other shortcut owners remain registered");
            Next(); break;
        case 11:
            var inputs=(IDictionary<string,Control>)Property(form,"Inputs");
            byte[] before=File.ReadAllBytes(config);
            ((TextBox)inputs["ReplayKey"]).Text=Shortcut(keys[0]);
            Check(!(bool)Call(form,"SaveAndApply") && before.SequenceEqual(File.ReadAllBytes(config)),"Duplicate actions inside Minus700K remain rejected without saving");
            ((TextBox)inputs["ReplayKey"]).Text=Shortcut(keys[3]);
            Check((bool)Call(form,"SaveAndApply"),"A shortcut already owned by another window saves and applies successfully");
            ObserveSpeech(); CheckOtherOwner();
            Check((string)Field(Field(context,"settings"),"ReplayKey")==Shortcut(keys[3]),"The shared replacement shortcut becomes the active preference");
            Set(form,"discard",true); form.Close(); form=null;
            baseline=Starts; Press(keys[2]); Next(); break;
        case 12:
            if(phase.ElapsedMilliseconds<200) return;
            Check(Starts==baseline,"The previous shortcut stops triggering after settings apply");
            Release(); Next(); break;
        case 13:
            if(phase.ElapsedMilliseconds<100) return;
            Press(keys[3]); Next(); break;
        case 14:
            if(Starts==baseline) return;
            Check(Starts==baseline+1,"The replacement shared shortcut executes replay through the real key loop");
            Release();
            UnregisterHotKey(owner,63);
            Check(ProbeCanRegister(keys[3]),"Minus700K never claims the shortcut when the other owner releases it");
            Check(RegisterHotKey(owner,63,0x4003,(uint)keys[3]),"The other owner can register the shortcut again while Minus700K runs");
            Check(Field(context,"recording")==null,"Synthetic key tests never start recording or screen capture");
            Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"Shortcut tests never create authentication data");
            passed=true; timer.Stop(); ((ApplicationContext)context).ExitThread(); break;
        }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); config=Path.Combine(root,"config.json"); string output=Path.GetFullPath(args[2]);
        report["checks"]=checks; report["fixtureRoot"]=root; report["realCredentialsAccessed"]=false; report["syntheticKeyStateOnly"]=true;
        Mutex resident=null; bool reserved=false;
        try {
            resident=new Mutex(false,"Local\\Minus700K_PTT_7E940DB1");
            try { reserved=resident.WaitOne(0); } catch(AbandonedMutexException) { reserved=true; }
            if(!reserved) throw new InvalidOperationException("An existing resident is active; this test never stops it.");
            if(File.Exists(config)) throw new InvalidOperationException("Use an empty fixture directory.");
            Directory.CreateDirectory(Path.Combine(root,"runtime")); Directory.CreateDirectory(Path.Combine(root,"vendor","whisper"));
            File.WriteAllBytes(Path.Combine(root,"vendor","whisper","whisper-cli.exe"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"model.bin"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"codex.exe"),new byte[]{0});
            owner=CreateWindowEx(0,"STATIC","Other shortcut owner",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            probe=CreateWindowEx(0,"STATIC","Shortcut ownership probe",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            Check(owner!=IntPtr.Zero && probe!=IntPtr.Zero,"Fixture creates independent native shortcut owner and probe windows");
            foreach(int key in new[]{0x84,0x85,0x86,0x87,0x7c,0x7d,0x7e,0x7f,0x80,0x81,0x82,0x83}) {
                if(RegisterHotKey(owner,60+keys.Count,0x4003,(uint)key)) keys.Add(key);
                if(keys.Count==4) break;
            }
            Check(keys.Count==4,"Fixture reserves four free combinations without disturbing existing application shortcuts");
            report["shortcuts"]=keys.Select(Shortcut).ToArray();
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0])); assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            var storeType=assembly.GetType("Minus700K.PlanStore",true); isolatedStore=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,isolatedStore);
            var settings=Activator.CreateInstance(assembly.GetType("Minus700K.Settings",true));
            Set(settings,"PushToTalkKey",Shortcut(keys[0])); Set(settings,"CancelKey",Shortcut(keys[1])); Set(settings,"ReplayKey",Shortcut(keys[2]));
            Set(settings,"WhisperModel","model.bin"); Set(settings,"CodexPath",Path.Combine(root,"codex.exe"));
            Set(settings,"KeepRequestFiles",false);
            Set(settings,"PlayCues",false); Set(settings,"PlayProcessingCues",false); Set(settings,"SpeechVolume",0); Set(settings,"CueVolume",0);
            File.WriteAllText(config,new JavaScriptSerializer().Serialize(settings),new UTF8Encoding(false)); originalConfig=File.ReadAllBytes(config);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            var contextType=assembly.GetType("Minus700K.VoiceContext",true);
            context=contextType.GetConstructors(Instance)[0].Invoke(new object[]{root,settings,"shared shortcut test answer",false});
            Set(context,"keyState",(Func<int,bool>)(key=>Volatile.Read(ref state[key])!=0));
            ObserveSpeech(); total=Stopwatch.StartNew(); phase=Stopwatch.StartNew();
            using(var timer=new System.Windows.Forms.Timer { Interval=60 }) {
                timer.Tick+=delegate { try { Tick(timer); } catch(Exception e) { timer.Stop(); report["error"]=e.ToString(); if(form!=null) report["settingsFeedback"]=((Control)Field(form,"feedback")).Text; Release(); if(context!=null) ((ApplicationContext)context).ExitThread(); } };
                timer.Start(); Application.Run((ApplicationContext)context); timer.Stop();
            }
            Check(!originalConfig.SequenceEqual(File.ReadAllBytes(config)),"Only the fixture configuration was updated");
        } catch(Exception e) { passed=false; report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString(); }
        finally {
            Release(); if(pendingRequest!=null) { Set(context,"active",null); pendingRequest.Dispose(); }
            if(context!=null) ((IDisposable)context).Dispose();
            if(owner!=IntPtr.Zero) { for(int i=0;i<keys.Count;i++) UnregisterHotKey(owner,60+i); DestroyWindow(owner); }
            if(probe!=IntPtr.Zero) DestroyWindow(probe);
            if(resident!=null) { if(reserved) resident.ReleaseMutex(); resident.Dispose(); }
            report["passed"]=passed && !report.ContainsKey("error"); Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        }
        return (bool)report["passed"]?0:1;
    }
}

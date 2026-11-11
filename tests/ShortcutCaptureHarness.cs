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

// Runs only on HiddenDesktopLauncher's private desktop. Keyboard notifications
// enter the capture observer directly; no input is sent to the user's desktop.
internal static class ShortcutCaptureHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint style,string className,string name,uint windowStyle,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr window,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window,int id);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    static readonly List<string> checks=new List<string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static readonly List<int> keys=new List<int>();
    static readonly JavaScriptSerializer json=new JavaScriptSerializer();
    static Form form,other;
    static IDictionary<string,Control> inputs;
    static IntPtr owner,probe;
    static string root,config;
    static int ordinaryKeyDowns;
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Call(object value,string name,params object[] args) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks.Add(message); }
    static void Pump() { for(int i=0;i<4;i++) { Application.DoEvents(); Thread.Sleep(5); } }
    static string Shortcut(int key) { return "Ctrl+Alt+"+((Keys)key); }
    static void Activate(Form target) { target.Activate(); SetActiveWindow(target.Handle); }
    static object Capture(Control input) { return Field(input,"capture"); }
    static bool Installed(object capture) { return capture!=null && (IntPtr)Field(capture,"hook")!=IntPtr.Zero; }
    static object Focus(string name) {
        Activate(form); inputs[name].Focus(); Pump();
        var capture=Capture(inputs[name]);
        Check(inputs[name].Focused && Installed(capture),name+" installs a scoped keyboard observer while focused");
        return capture;
    }
    static void Observe(object capture,int key,bool pressed=true) { Call(capture,"Observe",pressed?0x100:0x101,key); }
    static void Chord(object capture,int key,bool release=true) {
        Observe(capture,(int)Keys.LControlKey); Observe(capture,(int)Keys.LMenu);
        Call(capture,"Observe",0x104,key);
        if(release) { Call(capture,"Observe",0x105,key); Observe(capture,(int)Keys.LMenu,false); Observe(capture,(int)Keys.LControlKey,false); }
    }
    static void OwnerUnchanged() {
        foreach(int key in keys) {
            bool reserved=RegisterHotKey(probe,90,0x4003,(uint)key);
            if(reserved) UnregisterHotKey(probe,90);
            Check(!reserved,"The other window keeps ownership of "+Shortcut(key));
        }
    }
    static string SavedKey(string name) { return (string)json.Deserialize<Dictionary<string,object>>(File.ReadAllText(config))[name]; }
    static void Run() {
        Check(inputs.Values.All(control=>control.GetType().Name!="ShortcutBox" || Capture(control)==null),"Opening settings does not install an observer until a shortcut field is focused");
        string[] names={"PushToTalkKey","CancelKey","ReplayKey"};
        for(int i=0;i<names.Length;i++) {
            var capture=Focus(names[i]); string before=SavedKey(names[i]);
            Chord(capture,keys[i]); Pump();
            Check(inputs[names[i]].Text==Shortcut(keys[i]),names[i]+" captures a reserved shortcut without a normal KeyDown notification");
            Check(SavedKey(names[i])==before,"Capturing "+names[i]+" changes only the unsaved draft");
            Check((bool)Call(form,"SaveAndApply"),names[i]+" saves successfully while another window owns the combination");
            Check(SavedKey(names[i])==Shortcut(keys[i]),names[i]+" survives a configuration reload");
        }
        Check(ordinaryKeyDowns==0,"All three shortcut captures and saves complete without ordinary KeyDown delivery");
        OwnerUnchanged();

        var input=inputs["ReplayKey"]; var active=Focus("ReplayKey");
        string previous=input.Text; int changes=0;
        input.TextChanged+=delegate { changes++; };
        Observe(active,(int)Keys.LControlKey); Observe(active,(int)Keys.RControlKey); Observe(active,(int)Keys.LControlKey,false);
        Observe(active,(int)Keys.RMenu); Observe(active,(int)Keys.RShiftKey); Observe(active,keys[3]); Pump();
        Check(input.Text=="Ctrl+Alt+Shift+"+((Keys)keys[3]),"Right modifiers and releasing only one side preserve the remaining modifier");
        int baseline=changes;
        Observe(active,(int)Keys.RControlKey,false); Observe(active,(int)Keys.RMenu,false); Observe(active,(int)Keys.RShiftKey,false);
        Observe(active,keys[3]); Pump();
        Check(changes==baseline && input.Text=="Ctrl+Alt+Shift+"+((Keys)keys[3]),"Autorepeat after modifier release does not overwrite the captured chord");
        Observe(active,keys[3],false); Observe(active,keys[3]); Observe(active,keys[3],false); Pump();
        Check(input.Text==((Keys)keys[3]).ToString(),"A fresh primary press after release can replace the chord");

        previous=input.Text;
        foreach(int key in new[]{(int)Keys.LControlKey,(int)Keys.RShiftKey,(int)Keys.Menu,(int)Keys.LWin,(int)Keys.RWin,0,1,2,(int)Keys.Escape,(int)Keys.Tab}) { Observe(active,key); Observe(active,key,false); }
        Observe(active,(int)Keys.LWin); Observe(active,(int)Keys.Q); Observe(active,(int)Keys.Q,false); Observe(active,(int)Keys.LWin,false); Pump();
        Check(input.Text==previous,"Modifiers alone, mouse virtual keys, navigation keys and unsupported Windows combinations are ignored");

        var down=(bool[])Field(active,"down"); down[keys[2]]=true;
        Chord(active,keys[2]); Pump();
        Check(input.Text==previous,"A key already held at capture activation is ignored until released");
        Chord(active,keys[2]); Pump();
        Check(input.Text==Shortcut(keys[2]),"Releasing a seeded held key permits the next full press");

        byte[] saved=File.ReadAllBytes(config);
        Chord(active,keys[0]); Pump();
        Check(!(bool)Call(form,"SaveAndApply") && saved.SequenceEqual(File.ReadAllBytes(config)),"Sharing a primary key between Minus700K actions is still rejected without overwriting saved settings");
        Chord(active,keys[2]); Pump(); Check((bool)Call(form,"SaveAndApply"),"The original distinct shared shortcut can be restored and saved");

        previous=input.Text;
        Set(input,"foregroundWindow",(Func<IntPtr>)(()=>other.Handle));
        Chord(active,keys[3]); Pump();
        Check(input.Text==previous && Installed(active),"Foreground mismatch blocks capture before a delayed deactivation notification is processed");
        Set(input,"foregroundWindow",(Func<IntPtr>)GetActiveWindow);

        Chord(active,keys[3]);
        other.Show(); Activate(other); Pump();
        Check(input.Text==Shortcut(keys[3]),"A captured press is retained when the competing action immediately deactivates settings");
        Check(!Installed(active) && Capture(input)==null,"Deactivating settings releases its keyboard observer");
        previous=input.Text; Chord(active,keys[0]); Pump();
        Check(input.Text==previous,"Notifications to a released observer cannot edit an inactive field");

        active=Focus("ReplayKey"); inputs["AppLanguage"].Focus(); Pump();
        Check(!Installed(active) && Capture(input)==null,"Moving focus to a regular setting releases keyboard capture");
        active=Focus("ReplayKey"); input.Enabled=false; Pump();
        Check(!Installed(active),"Disabling the shortcut field releases its observer");
        input.Enabled=true; active=Focus("ReplayKey"); input.Hide(); Pump();
        Check(!Installed(active),"Hiding the shortcut field releases its observer");
        input.Show(); active=Focus("ReplayKey");
        Call(input,"SetCaptureActive",false);
        Check(!Installed(active),"Stopping capture explicitly unhooks the native observer");
        var fallback=new KeyEventArgs(Keys.Control|Keys.F11); Call(input,"OnKeyDown",fallback);
        Check(input.Text=="Ctrl+F11" && fallback.Handled && fallback.SuppressKeyPress,"Ordinary key delivery remains available when the observer is unavailable");
        inputs["AppLanguage"].Focus(); active=Focus("ReplayKey");
        Chord(active,keys[2]); Set(form,"discard",true); form.Dispose(); Pump();
        Check(!Installed(active),"Disposing settings releases capture even when a captured update is queued");
        Chord(active,keys[1]); Pump();
        OwnerUnchanged();
        Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"The empty fixture account remains disconnected");
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); config=Path.Combine(root,"config.json"); string output=Path.GetFullPath(args[2]);
        report["checks"]=checks; report["fixtureRoot"]=root; report["syntheticObserverNotificationsOnly"]=true; report["realCredentialsAccessed"]=false;
        bool passed=false;
        try {
            if(File.Exists(config)) throw new InvalidOperationException("Use a new empty fixture directory.");
            Directory.CreateDirectory(Path.Combine(root,"runtime")); Directory.CreateDirectory(Path.Combine(root,"vendor","whisper"));
            File.WriteAllBytes(Path.Combine(root,"model.bin"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"codex.exe"),new byte[]{0});
            File.WriteAllBytes(Path.Combine(root,"vendor","whisper","whisper-cli.exe"),new byte[]{0});
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Check(assembly.GetType("Minus700K.ShortcutCapture")!=null,"The application includes a capture path independent of ordinary KeyDown delivery");
            assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            var storeType=assembly.GetType("Minus700K.PlanStore",true); var store=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,store);
            var settings=Activator.CreateInstance(assembly.GetType("Minus700K.Settings",true));
            Set(settings,"WhisperModel","model.bin"); Set(settings,"CodexPath",Path.Combine(root,"codex.exe"));
            Set(settings,"KeepRequestFiles",false);
            Set(settings,"PlayCues",false); Set(settings,"PlayProcessingCues",false); Set(settings,"SpeechVolume",0); Set(settings,"CueVolume",0);
            File.WriteAllText(config,json.Serialize(settings),new UTF8Encoding(false));
            owner=CreateWindowEx(0,"STATIC","Other shortcut owner",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            probe=CreateWindowEx(0,"STATIC","Shortcut ownership probe",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
            Check(owner!=IntPtr.Zero && probe!=IntPtr.Zero,"Independent native owner and probe windows are created on the private desktop");
            foreach(int key in new[]{0x84,0x85,0x86,0x87,0x7c,0x7d,0x7e,0x7f,0x80,0x81,0x82,0x83}) {
                if(RegisterHotKey(owner,60+keys.Count,0x4003,(uint)key)) keys.Add(key);
                if(keys.Count==4) break;
            }
            Check(keys.Count==4,"Four free key combinations are reserved by the competing owner");
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            form=(Form)assembly.GetType("Minus700K.SettingsForm",true).GetConstructors(Instance)[0].Invoke(new object[]{root,settings,null,0,false,null});
            inputs=(IDictionary<string,Control>)Property(form,"Inputs");
            foreach(string name in new[]{"PushToTalkKey","CancelKey","ReplayKey"}) {
                // A private desktop is never switched onto the user's display.
                // Its real thread activation replaces input-desktop foreground.
                Set(inputs[name],"foregroundWindow",(Func<IntPtr>)GetActiveWindow);
                inputs[name].KeyDown+=delegate { ordinaryKeyDowns++; };
            }
            other=new Form { Text="Inactive settings fixture" }; form.Show(); Pump(); Run(); passed=true;
        } catch(Exception e) {
            report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString();
            if(form!=null && !form.IsDisposed) report["settingsFeedback"]=((Control)Field(form,"feedback")).Text;
        } finally {
            if(form!=null) form.Dispose(); if(other!=null) other.Dispose();
            if(owner!=IntPtr.Zero) { for(int i=0;i<keys.Count;i++) UnregisterHotKey(owner,60+i); DestroyWindow(owner); }
            if(probe!=IntPtr.Zero) DestroyWindow(probe);
            report["passed"]=passed; report["ordinaryKeyDowns"]=ordinaryKeyDowns;
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output,json.Serialize(report),new UTF8Encoding(false));
        }
        return passed?0:1;
    }
}

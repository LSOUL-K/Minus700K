using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Only the private desktop/test thread receives these synthetic window
// messages. No SendInput, global keyboard changes, audio or real account data.
internal static class SettingsAltKeysHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static readonly JavaScriptSerializer json=new JavaScriptSerializer();
    static readonly List<string> checks=new List<string>();
    static readonly List<object> probes=new List<object>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static Form form;
    static IDictionary<string,Control> inputs;
    static string root,config;
    static bool expectUnhandled;
    static int unhandled;
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static object Call(object value,string name,params object[] args) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,args); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks.Add(name); }
    static void Pump() { for(int i=0;i<3;i++) { Application.DoEvents(); Thread.Sleep(5); } }
    sealed class KeyboardState : IDisposable {
        readonly byte[] original=new byte[256];
        internal KeyboardState(Keys modifiers) {
            if(!GetKeyboardState(original)) throw new InvalidOperationException("GetKeyboardState failed");
            byte[] state=new byte[256];
            if((modifiers&Keys.Alt)!=0) { state[(int)Keys.Menu]=128; state[(int)Keys.LMenu]=128; }
            if((modifiers&Keys.Control)!=0) { state[(int)Keys.ControlKey]=128; state[(int)Keys.LControlKey]=128; }
            if((modifiers&Keys.Shift)!=0) { state[(int)Keys.ShiftKey]=128; state[(int)Keys.LShiftKey]=128; }
            if(!SetKeyboardState(state)) throw new InvalidOperationException("SetKeyboardState failed");
        }
        public void Dispose() { SetKeyboardState(original); }
    }
    static bool Preprocess(Control control,int message,int value,Keys modifiers,bool repeat=false,bool dispatch=false) {
        int flags=1;
        if(message==0x104 || message==0x106) flags|=1<<29;
        if(repeat) flags|=1<<30;
        var msg=Message.Create(control.Handle,message,new IntPtr(value),new IntPtr(flags));
        using(var state=new KeyboardState(modifiers)) {
            bool handled=control.PreProcessMessage(ref msg);
            // Only ordinary text and known native commands are ever dispatched.
            // Unhandled WM_SYSCHAR is recorded, never played as a Windows beep.
            if(dispatch && !handled) SendMessage(control.Handle,message,msg.WParam,msg.LParam);
            return handled;
        }
    }
    static void Select(int tab) { Call(form,"SelectTab",tab); Pump(); }
    static Control Focus(string name) {
        var control=inputs[name]; form.Activate(); SetActiveWindow(form.Handle); control.Focus(); Pump();
        Check(control.ContainsFocus,"Test control receives focus: "+name); return control;
    }
    static void ProbeAlt(Control control,string description) {
        int misses=0;
        foreach(char key in new[]{'q','w','a'}) {
            for(int press=0;press<10;press++) if(!Preprocess(control,0x106,key,Keys.Alt,press>0)) misses++;
        }
        probes.Add(new {control=description,unhandled=misses,total=30}); unhandled+=misses;
        if(!expectUnhandled) Check(misses==0,"Unhandled Alt letters and held repeats are consumed: "+description);
    }
    static void Run() {
        form.Show(); form.Activate(); SetActiveWindow(form.Handle); Pump();
        var nav=(List<Button>)Field(form,"navigation");
        nav[0].Focus(); ProbeAlt(nav[0],"navigation button");
        ProbeAlt(Focus("AppLanguage"),"language combo");
        ProbeAlt(Focus("CaptureEnabled"),"toggle");
        ProbeAlt(Focus("CaptureMaximumWidth"),"numeric editor");
        Select(3); var text=(TextBox)Focus("CustomInstructionsText");
        string before=text.Text; ProbeAlt(text,"multiline text editor");
        Check(text.Text==before,"Unmatched Alt letters do not alter text");
        if(expectUnhandled) Check(unhandled>0,"Old application reproduces unhandled WM_SYSCHAR before native warning sound");

        text.Text="";
        Check(!Preprocess(text,0x102,'x',Keys.None,false,true) && text.Text=="x","Ordinary WM_CHAR text entry is preserved");
        Check(!Preprocess(text,0x102,'@',Keys.Control|Keys.Alt,false,true) && text.Text=="x@","AltGr-style WM_CHAR text entry is preserved");
        Check(Preprocess(text,0x100,(int)Keys.S,Keys.Control),"Ctrl+S still reaches the settings save command");
        var saved=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(config));
        Check(Convert.ToString(saved["CustomInstructionsText"])=="x@","Ctrl+S persists the edited text");

        int tab=(int)Field(form,"selected");
        Check(Preprocess(text,0x100,(int)Keys.Tab,Keys.Control) && (int)Field(form,"selected")==tab+1,"Ctrl+Tab still changes settings tabs");
        Check(Preprocess(form,0x100,(int)Keys.Tab,Keys.Control|Keys.Shift) && (int)Field(form,"selected")==tab,"Ctrl+Shift+Tab still changes settings tabs backwards");
        nav[3].Focus(); Pump();
        Check(Preprocess(nav[3],0x100,(int)Keys.Tab,Keys.None),"Tab navigation remains handled by the dialog");
        Check(Preprocess(form,0x100,(int)Keys.Tab,Keys.Shift),"Shift+Tab navigation remains handled by the dialog");

        nav[3].Focus();
        Check(!Preprocess(nav[3],0x104,(int)Keys.F4,Keys.Alt),"Alt+F4 remains available to native window closing");
        Check(!Preprocess(nav[3],0x106,' ',Keys.Alt),"Alt+Space remains available to the native system menu");
        Check(!Preprocess(nav[3],0x100,(int)Keys.F10,Keys.None),"F10 remains available to native menu handling");
        bool clicked=false;
        using(var mnemonic=new Button {Text="&Test mnemonic",Location=new Point(0,0),Size=new Size(160,35)}) {
            mnemonic.Click+=delegate { clicked=true; }; form.Controls.Add(mnemonic); mnemonic.BringToFront();
            Check(Preprocess(form,0x106,'t',Keys.Alt) && clicked,"A valid Alt mnemonic is still invoked");
        }

        Select(0); var combo=(ComboBox)Focus("AppLanguage");
        Preprocess(combo,0x104,(int)Keys.Down,Keys.Alt,false,true); Pump();
        Check(combo.DroppedDown,"Alt+Down still opens a native combo dropdown"); combo.DroppedDown=false;
        var shortcut=Focus("ReplayKey");
        Call(shortcut,"SetCaptureActive",false);
        Check(Preprocess(shortcut,0x104,(int)Keys.J,Keys.Alt) && shortcut.Text=="Alt+J","Shortcut editor still records Alt combinations");
        Check(Field(form,"sampleCue")==null && Field(form,"sampleVoice")==null,"Synthetic Alt input never starts app sound or speech previews");
        Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"Fixture account stays disconnected");
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); config=Path.Combine(root,"config.json"); string output=Path.GetFullPath(args[2]);
        expectUnhandled=File.Exists(Path.Combine(root,"expect-unhandled"));
        report["checks"]=checks; report["probes"]=probes; report["fixtureRoot"]=root; report["expectUnhandled"]=expectUnhandled;
        report["syntheticThreadMessagesOnly"]=true; report["realCredentialsAccessed"]=false; bool passed=false;
        try {
            if(File.Exists(config)) throw new InvalidOperationException("Use an empty fixture directory");
            Directory.CreateDirectory(Path.Combine(root,"runtime")); File.WriteAllBytes(Path.Combine(root,"model.bin"),new byte[]{0}); File.WriteAllBytes(Path.Combine(root,"codex.exe"),new byte[]{0});
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0])); assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            var storeType=assembly.GetType("Minus700K.PlanStore",true); var store=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")}); storeType.GetField("Default",Static).SetValue(null,store);
            var settings=Activator.CreateInstance(assembly.GetType("Minus700K.Settings",true));
            Set(settings,"WhisperModel","model.bin"); Set(settings,"CodexPath",Path.Combine(root,"codex.exe")); Set(settings,"KeepRequestFiles",false);
            Set(settings,"PlayCues",false); Set(settings,"PlayProcessingCues",false); Set(settings,"SpeechVolume",0); Set(settings,"CueVolume",0);
            File.WriteAllText(config,json.Serialize(settings),new UTF8Encoding(false));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            form=(Form)assembly.GetType("Minus700K.SettingsForm",true).GetConstructors(Instance)[0].Invoke(new object[]{root,settings,null,0,true,null});
            inputs=(IDictionary<string,Control>)Property(form,"Inputs");
            foreach(var control in inputs.Values.Where(c=>c.GetType().Name=="ShortcutBox")) Set(control,"foregroundWindow",(Func<IntPtr>)GetActiveWindow);
            Run(); passed=true;
        } catch(Exception e) { report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString(); if(form!=null && !form.IsDisposed) report["settingsFeedback"]=((Control)Field(form,"feedback")).Text; }
        finally {
            if(form!=null) { Set(form,"discard",true); form.Dispose(); }
            report["passed"]=passed; report["unhandledSysChars"]=unhandled;
            Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllText(output,json.Serialize(report),new UTF8Encoding(false));
        }
        return passed?0:1;
    }
}

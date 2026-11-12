using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

// Exercises the installed UI and real Windows shell. Never starts the resident,
// opens audio, changes tray promotion preferences, or captures the desktop.
internal static class ShellIconsHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)] struct IconId { public uint Size; public IntPtr Window; public uint Id; public Guid Identity; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconId id,out Rect rectangle);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr parameter,IntPtr value);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    static Assembly assembly; static object voice,tray,icons,settings; static Form form; static Control dispatcher;
    static Guid identity; static string fixture,output,current="ready",commandPath,pending; static ContextMenuStrip menu;
    static readonly List<string> checks=new List<string>(); static readonly List<object> samples=new List<object>();
    static readonly Dictionary<string,string> bigHashes=new Dictionary<string,string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static readonly Stopwatch elapsed=Stopwatch.StartNew(),sinceCommand=new Stopwatch();
    static bool passed,interactive,finishing; static int autoIndex; static string lastCommand="";
    static readonly string[] commands={"ready","recording","recognizing","asking","speaking","paused","ready","recording","minimize","restore","asking","reopen","tray-readd","tray-recreate","tray-restart","ready","exit"};
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Property(object value,string name) { return value.GetType().GetProperty(name,Instance).GetValue(value,null); }
    static void SetProperty(object value,string name,object data) { value.GetType().GetProperty(name,Instance).SetValue(value,data,null); }
    static object Call(object value,string name,params object[] arguments) {
        try { return value.GetType().GetMethod(name,Instance).Invoke(value,arguments); }
        catch(TargetInvocationException error) { throw error.InnerException??error; }
    }
    static void Check(bool value,string message) { if(!value) throw new InvalidOperationException(message); checks.Add(message); }
    static void WriteReport() {
        report["passed"]=passed; report["elapsedMs"]=elapsed.ElapsedMilliseconds;
        report["checks"]=checks.ToArray(); report["samples"]=samples.ToArray(); report["state"]=current;
        File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
    }
    static string Fingerprint(Icon icon) {
        using(Bitmap bitmap=icon.ToBitmap()) using(var bytes=new MemoryStream()) using(var hash=SHA256.Create()) {
            bitmap.Save(bytes,ImageFormat.Png); return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-","");
        }
    }
    static string NativeFingerprint(IntPtr handle) {
        Check(handle!=IntPtr.Zero,"Windows exposes a nonempty window icon handle");
        using(Icon view=Icon.FromHandle(handle)) using(Icon copy=(Icon)view.Clone()) return Fingerprint(copy);
    }
    static int TrayRectangle(out Rect rectangle) {
        var id=new IconId { Size=(uint)Marshal.SizeOf(typeof(IconId)),Identity=identity };
        return Shell_NotifyIconGetRect(ref id,out rectangle);
    }
    static void CreateTray() {
        Type type=assembly.GetType("Minus700K.TrayNotificationIcon",true);
        tray=type.GetConstructor(Instance,null,new[]{typeof(Guid)},null).Invoke(new object[]{identity});
        SetProperty(tray,"Icon",Call(icons,"ForState",current));
        SetProperty(tray,"Text","Minus700K shell verification"); SetProperty(tray,"ContextMenuStrip",menu); SetProperty(tray,"Visible",true);
        Set(voice,"tray",tray);
        Check((Guid)Property(tray,"Identity")==identity,"Verification uses its own notification GUID");
    }
    static void CreateForm() {
        Type type=assembly.GetType("Minus700K.SettingsForm",true);
        form=(Form)type.GetConstructors(Instance)[0].Invoke(new object[]{fixture,settings,null,0,true,null});
        form.Text="Minus700K shell verification"; form.ShowInTaskbar=true;
        form.StartPosition=FormStartPosition.Manual;
        Rectangle area=Screen.PrimaryScreen.WorkingArea;
        form.Location=new Point(area.Left+24,area.Top+24);
        form.Size=new Size(Math.Min(980,area.Width-48),Math.Min(720,area.Height-48));
        Set(voice,"settingsForm",form); Set(voice,"settingsOpen",true);
        Call(form,"SetStatusIcon",(string)Field(voice,"displayedState"));
        form.Show();
        // A hidden STARTUPINFO may suppress the first ShowWindow. This explicit
        // second call affects only this fixture and does not activate it.
        ShowWindow(form.Handle,4);
    }
    static void Request(string command) {
        if(command=="exit") { Finish(); return; }
        if(command=="minimize") form.WindowState=FormWindowState.Minimized;
        else if(command=="restore") form.WindowState=FormWindowState.Normal;
        else if(command=="reopen") {
            form.Dispose(); Set(voice,"settingsForm",null); CreateForm();
        } else if(command=="tray-readd") {
            SetProperty(tray,"Visible",false); Rect absent;
            Check(TrayRectangle(out absent)<0,"Hiding removes only the verification notification icon");
            SetProperty(tray,"Visible",true);
        } else if(command=="tray-recreate") {
            ((IDisposable)tray).Dispose(); tray=null; CreateTray();
        } else if(command=="tray-restart") {
            // Simulate Explorer dropping this ONE test icon, never restart the
            // user's shell or delete another application's notification entry.
            object data=Call(tray,"Data",0u);
            object[] arguments={2u,data};
            bool removed=(bool)tray.GetType().GetMethod("Shell_NotifyIconW",Static).Invoke(null,arguments);
            Rect absent; Check(removed && TrayRectangle(out absent)<0,"Simulated shell restart removes only the test GUID");
            uint created=RegisterWindowMessage("TaskbarCreated");
            SendMessage((IntPtr)Property(tray,"Handle"),created,IntPtr.Zero,IntPtr.Zero);
        } else {
            if(command!="ready" && command!="recording" && command!="recognizing" && command!="asking" && command!="speaking" && command!="paused") throw new ArgumentException("Unknown shell state: "+command);
            current=command;
            // This is the production dispatch path, with no microphone, speech,
            // account service, polling keyboard thread, or global IPC constructor.
            Call(voice,"SetState",command);
        }
        pending=command; sinceCommand.Restart();
    }
    static bool Sample() {
        object taskbar=Field(form,"taskbarStatus");
        bool buttonReady=(bool)Property(taskbar,"IsButtonReady");
        int overlayResult=(int)Property(taskbar,"LastResult"),appliedCount=(int)Property(taskbar,"AppliedCount");
        report["lastTaskbarObservation"]=new { command=pending,state=current,window=form.Handle.ToInt64(),taskbarButtonCreated=buttonReady,
            overlayResult=overlayResult,overlayResultHex="0x"+overlayResult.ToString("X8"),overlayAppliedCount=appliedCount };
        if(buttonReady && overlayResult<0) throw new COMException("Windows rejected the taskbar status overlay (0x"+overlayResult.ToString("X8")+").",overlayResult);
        if(!buttonReady || appliedCount==0) {
            if(sinceCommand.ElapsedMilliseconds<6000) return false;
            throw new TimeoutException(buttonReady?"The taskbar button exists, but no status overlay was applied.":"The real shell did not create the settings taskbar button.");
        }
        Rect rectangle; int trayResult=TrayRectangle(out rectangle);
        if(trayResult<0 && sinceCommand.ElapsedMilliseconds<4000) return false;
        Check(form.ShowInTaskbar && IsWindowVisible(form.Handle),"Settings owns a visible real taskbar window: "+pending);
        Check((string)Field(voice,"displayedState")==current,"Production status dispatch keeps the current state: "+pending);
        Check((int)Property(taskbar,"LastResult")>=0,"Windows accepts the taskbar overlay update: "+pending);
        Check(trayResult==0,"Windows finds the same notification GUID: "+pending);
        IntPtr big=SendMessage(form.Handle,0x7F,new IntPtr(1),IntPtr.Zero),small=SendMessage(form.Handle,0x7F,IntPtr.Zero,IntPtr.Zero);
        string bigHash=NativeFingerprint(big),smallHash=NativeFingerprint(small);
        Check(bigHash==Fingerprint(form.Icon),"Native large icon matches the settings state: "+pending);
        string canonical=current=="recognizing"?"asking":current;
        if(bigHashes.ContainsKey(canonical)) Check(bigHashes[canonical]==bigHash,"Returning to a state restores its native icon: "+canonical);
        else bigHashes.Add(canonical,bigHash);
        samples.Add(new { command=pending,state=current,elapsedMs=elapsed.ElapsedMilliseconds,window=form.Handle.ToInt64(),minimized=IsIconic(form.Handle),
            bigIconHash=bigHash,smallIconHash=smallHash,taskbarButtonCreated=Property(taskbar,"IsButtonReady"),overlayResult=Property(taskbar,"LastResult"),overlayAppliedCount=Property(taskbar,"AppliedCount"),
            trayResult=trayResult,trayIdentity=identity.ToString("D"),trayRectangle=new[]{rectangle.Left,rectangle.Top,rectangle.Right,rectangle.Bottom} });
        pending=null; WriteReport(); return true;
    }
    static void Finish() {
        var unique=new HashSet<string>(bigHashes.Values);
        Check(bigHashes.Count==5 && unique.Count==5,"Ready, recording, processing, speaking and paused use five distinct native icons");
        ReadNotificationRegistry();
        passed=true; finishing=true; WriteReport(); Application.ExitThread();
    }
    static void ReadNotificationRegistry() {
        var entries=new List<object>();
        try {
            using(var parent=Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings")) {
                if(parent!=null) foreach(string name in parent.GetSubKeyNames()) using(var key=parent.OpenSubKey(name)) {
                    if(key==null || !string.Equals(key.GetValue("InitialTooltip") as string,"Minus700K shell verification",StringComparison.Ordinal)) continue;
                    object value=key.GetValue("IconGuid"); Guid registeredGuid=Guid.Empty;
                    byte[] bytes=value as byte[];
                    if(bytes!=null && bytes.Length==16) registeredGuid=new Guid(bytes);
                    else if(value is string) Guid.TryParse((string)value,out registeredGuid);
                    entries.Add(new { key=name,initialTooltip=key.GetValue("InitialTooltip"),executable=key.GetValue("ExecutablePath"),
                        iconGuid=registeredGuid.ToString("D"),isCurrentIdentity=registeredGuid==identity,isPromoted=key.GetValue("IsPromoted") });
                }
            }
            report["notificationRegistry"]=entries.ToArray();
        } catch(Exception error) { report["notificationRegistryReadError"]=error.GetType().Name+": "+error.Message; }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length<3 || args.Length>4) return 2;
        fixture=Path.GetFullPath(args[1]); output=Path.GetFullPath(args[2]); interactive=args.Length==4 && args[3]=="interactive";
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        try {
            if(Directory.Exists(fixture)) throw new InvalidOperationException("Use a new empty fixture directory.");
            Directory.CreateDirectory(Path.Combine(fixture,"runtime")); commandPath=Path.Combine(fixture,"shell-command.txt");
            identity=Guid.NewGuid(); report["notificationIdentity"]=identity.ToString("D"); report["interactive"]=interactive;
            report["microphoneOpened"]=false; report["desktopCaptured"]=false; report["promotionSettingsChanged"]=false; report["visualOverlayVerified"]=false;
            assembly=Assembly.LoadFrom(Path.GetFullPath(args[0])); assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,fixture);
            Type storeType=assembly.GetType("Minus700K.PlanStore",true);
            object store=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(fixture,"connection")}); storeType.GetField("Default",Static).SetValue(null,store);
            settings=Activator.CreateInstance(assembly.GetType("Minus700K.Settings",true));
            Set(settings,"PlayCues",false); Set(settings,"PlayProcessingCues",false); Set(settings,"ExclusiveMicrophone",true);
            File.WriteAllText(Path.Combine(fixture,"config.json"),new JavaScriptSerializer().Serialize(settings),new UTF8Encoding(false));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            dispatcher=new Control(); dispatcher.CreateControl(); IntPtr dispatcherHandle=dispatcher.Handle;
            icons=Activator.CreateInstance(assembly.GetType("Minus700K.TrayIcons",true),true);
            voice=FormatterServices.GetUninitializedObject(assembly.GetType("Minus700K.VoiceContext",true));
            Set(voice,"root",fixture); Set(voice,"settings",settings); Set(voice,"statusGate",new object()); Set(voice,"operationGate",new object());
            Set(voice,"dispatcher",dispatcher); Set(voice,"icons",icons); Set(voice,"displayedState","ready");
            menu=new ContextMenuStrip(); menu.Items.Add("Minus700K shell verification"); CreateTray(); CreateForm();
            report["pid"]=Process.GetCurrentProcess().Id; report["commandPath"]=commandPath;
            File.WriteAllText(output+".ready",fixture,new UTF8Encoding(false)); WriteReport();
            using(var timer=new System.Windows.Forms.Timer { Interval=100 }) {
                timer.Tick+=delegate {
                    try {
                        if(elapsed.ElapsedMilliseconds>(interactive?150000:45000)) throw new TimeoutException("Shell verification exceeded its time limit.");
                        if(pending!=null) { if(sinceCommand.ElapsedMilliseconds>=500) Sample(); return; }
                        if(interactive) {
                            if(!File.Exists(commandPath)) return;
                            string command=File.ReadAllText(commandPath).Trim();
                            if(command.Length==0 || command==lastCommand) return;
                            lastCommand=command; Request(command);
                        } else Request(commands[autoIndex++]);
                    } catch(Exception error) { timer.Stop(); report["error"]=error.ToString(); finishing=true; Application.ExitThread(); }
                };
                timer.Start(); Application.Run(); timer.Stop();
            }
            if(!finishing) throw new InvalidOperationException("Shell verification exited unexpectedly.");
        } catch(Exception error) { report["error"]=(error is TargetInvocationException && error.InnerException!=null?error.InnerException:error).ToString(); passed=false; }
        finally {
            try {
                if(voice!=null) Set(voice,"quitting",true);
                if(form!=null) form.Dispose();
                if(tray!=null) ((IDisposable)tray).Dispose();
                if(menu!=null) menu.Dispose();
                if(icons!=null) ((IDisposable)icons).Dispose();
                if(dispatcher!=null) dispatcher.Dispose();
                if(identity!=Guid.Empty) { Rect remaining; Check(TrayRectangle(out remaining)<0,"Disposal removes the verification notification icon"); }
            } catch(Exception error) { passed=false; report["cleanupError"]=error.ToString(); }
            WriteReport();
        }
        return passed?0:1;
    }
}

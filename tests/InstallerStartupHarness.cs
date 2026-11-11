using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class InstallerStartupHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const string ResidentMutex="Local\\Minus700K_PTT_7E940DB1";
    const string StopEvent="Local\\Minus700K_Stop_7E940DB1";
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    static readonly List<string> checks=new List<string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static MethodInfo main;
    static Type formType;
    static Form initialForm;
    static bool completed;
    static int step;
    static Stopwatch total,phase;
    static string language;
    static bool postInstall;

    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks.Add(message); }
    static bool OwnsResidentMutex() {
        try {
            using(Mutex mutex=Mutex.OpenExisting(ResidentMutex)) {
                if(!mutex.WaitOne(0)) return false;
                mutex.ReleaseMutex(); return true;
            }
        } catch(WaitHandleCannotBeOpenedException) { return false; }
    }
    static Form[] Forms() { return Application.OpenForms.Cast<Form>().Where(form=>form.GetType()==formType && !form.IsDisposed).ToArray(); }
    static int Selected(Form form) { return (int)formType.GetField("selected",Instance).GetValue(form); }
    static int InvokeMain(params string[] arguments) { return (int)main.Invoke(null,new object[]{arguments}); }
    static void VerifyConnection(Form form) {
        Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle),"Connection settings are natively visible");
        Check(Screen.AllScreens.Any(screen=>screen.WorkingArea.IntersectsWith(form.Bounds)),"Connection settings are within a display");
        Check(Selected(form)==5,"Connection tab is selected");
        string heading=((Label)formType.GetField("heading",Instance).GetValue(form)).Text;
        Check(heading==(language=="en"?"Connection":"接続"),"Connection heading follows " + language + " app language");
        Check(Forms().Length==1,"Exactly one settings window is open");
        Check(OwnsResidentMutex(),"Settings belong to this fixture's resident");
    }
    static void CloseSettings(Form form) { formType.GetField("discard",Instance).SetValue(form,true); form.Close(); }
    static void StopOwnResident() {
        if(!OwnsResidentMutex()) { Application.ExitThread(); return; }
        using(EventWaitHandle stop=EventWaitHandle.OpenExisting(StopEvent)) stop.Set();
    }
    static void Observe(System.Windows.Forms.Timer timer) {
        if(total.ElapsedMilliseconds>12000) throw new TimeoutException("Installer startup UI observation timed out at step " + step);
        var forms=Forms();
        if(step==0) {
            if(!postInstall) {
                if(phase.ElapsedMilliseconds<400) return;
                Check(forms.Length==0,"Normal startup remains tray-only");
                Check(OwnsResidentMutex(),"Normal startup creates this fixture's resident");
                Check(InvokeMain("--post-install")==0,"Post-install command reaches an existing resident");
                step=1; phase.Restart(); return;
            }
            if(forms.Length==0 || !IsWindowVisible(forms[0].Handle)) return;
            initialForm=forms[0]; VerifyConnection(initialForm); step=2; phase.Restart(); return;
        }
        if(step==1) {
            if(forms.Length==0 || Selected(forms[0])!=5 || !IsWindowVisible(forms[0].Handle)) return;
            initialForm=forms[0]; VerifyConnection(initialForm); step=2; phase.Restart(); return;
        }
        if(step==2) {
            formType.GetMethod("SelectTab",Instance).Invoke(initialForm,new object[]{0});
            Check(Selected(initialForm)==0,"Fixture switches away from the connection tab");
            Check(InvokeMain("--post-install")==0,"Repeated post-install request succeeds");
            step=3; phase.Restart(); return;
        }
        if(step==3) {
            if(forms.Length==0 || Selected(forms[0])!=5) return;
            Check(forms.Length==1 && object.ReferenceEquals(forms[0],initialForm),"Repeated request reuses the existing settings window");
            VerifyConnection(initialForm); CloseSettings(initialForm); step=4; phase.Restart(); return;
        }
        if(step==4 && phase.ElapsedMilliseconds>=350) {
            Check(Forms().Length==0,"Closing settings removes the window");
            Check(Application.MessageLoop && OwnsResidentMutex(),"Closing settings keeps the app resident");
            completed=true; timer.Stop(); StopOwnResident();
        }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        string root=Path.GetFullPath(args[1]),output=Path.GetFullPath(args[2]);
        report["checks"]=checks; report["fixtureRoot"]=root; report["realCredentialsAccessed"]=false;
        try {
            try { using(Mutex existing=Mutex.OpenExisting(ResidentMutex)) { throw new InvalidOperationException("A resident already exists; no application was stopped or changed."); } }
            catch(WaitHandleCannotBeOpenedException) { }
            Check(string.Equals(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName,root,StringComparison.OrdinalIgnoreCase),"Harness and application use the isolated installation root");
            var serializer=new JavaScriptSerializer();
            var scenario=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"startup-test.json")));
            language=(string)scenario["language"]; postInstall=(bool)scenario["postInstall"];
            report["language"]=language; report["postInstall"]=postInstall;
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            main=assembly.GetType("Minus700K.Program",true).GetMethod("Main",Static);
            formType=assembly.GetType("Minus700K.SettingsForm",true);
            var storeType=assembly.GetType("Minus700K.PlanStore",true);
            var isolatedStore=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,isolatedStore);
            Check(object.ReferenceEquals(storeType.GetField("Default",Static).GetValue(null),isolatedStore),"Credential store is isolated before application startup");
            byte[] config=File.ReadAllBytes(Path.Combine(root,"config.json"));
            total=Stopwatch.StartNew(); phase=Stopwatch.StartNew();
            using(var timer=new System.Windows.Forms.Timer { Interval=60 }) {
                timer.Tick+=delegate {
                    try { Observe(timer); }
                    catch(Exception e) {
                        timer.Stop(); report["error"]=e.ToString();
                        foreach(var form in Forms()) CloseSettings(form);
                        StopOwnResident();
                    }
                };
                // Start observation only after Program.Main initializes its own
                // WinForms settings and enters the real resident message loop.
                EventHandler idle=null;
                idle=delegate { Application.Idle-=idle; timer.Start(); };
                Application.Idle+=idle;
                try {
                    int code=InvokeMain(postInstall?new[]{"--post-install"}:new string[0]);
                    Check(code==0,"Resident exits cleanly after the fixture stop request");
                } finally { timer.Stop(); Application.Idle-=idle; }
            }
            Check(completed,"All startup and resident-lifetime observations completed");
            Check(config.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"config.json"))),"Startup and closing settings preserve configuration bytes");
            Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"Opening connection settings does not sign in or create credentials");
        } catch(Exception e) { report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString(); }
        report["passed"]=completed && !report.ContainsKey("error");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        return (bool)report["passed"]?0:1;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class StartupRecoveryHarness {
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
    static Form firstRecovery,successfulSettings;
    static bool completed,postInstall,recover;
    static int step;
    static Stopwatch total,phase;
    static string root,language;
    static byte[] originalConfig;

    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks.Add(message); }
    static object Property(object target,string name) { return target.GetType().GetProperty(name,Instance).GetValue(target,null); }
    static void Call(object target,string name,params object[] args) { target.GetType().GetMethod(name,Instance).Invoke(target,args); }
    static int InvokeMain(params string[] arguments) { return (int)main.Invoke(null,new object[]{arguments}); }
    static Form[] Forms() { return Application.OpenForms.Cast<Form>().Where(form=>form.GetType()==formType && !form.IsDisposed).ToArray(); }
    static int Selected(Form form) { return (int)formType.GetField("selected",Instance).GetValue(form); }
    static Control Named(Form form,string name) { return form.Controls.Find(name,true).SingleOrDefault(); }
    static bool Recovery(Form form) { Control retry=Named(form,"StartupRetry"); return retry!=null && retry.Visible; }
    static void CloseSettings(Form form) { formType.GetField("discard",Instance).SetValue(form,true); form.Close(); }
    static bool OwnsResidentMutex() {
        try {
            using(Mutex mutex=Mutex.OpenExisting(ResidentMutex)) {
                if(!mutex.WaitOne(0)) return false;
                mutex.ReleaseMutex(); return true;
            }
        } catch(WaitHandleCannotBeOpenedException) { return false; }
    }
    static bool ResidentReady() {
        string path=Path.Combine(root,"runtime","status.json");
        if(!File.Exists(path)) return false;
        try {
            var status=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            return Convert.ToInt32(status["pid"])==Process.GetCurrentProcess().Id && (string)status["state"]=="ready";
        } catch(IOException) { return false; }
    }
    static void StopOwnResident() {
        if(!OwnsResidentMutex()) { Application.ExitThread(); return; }
        try { using(EventWaitHandle stop=EventWaitHandle.OpenExisting(StopEvent)) stop.Set(); }
        catch(WaitHandleCannotBeOpenedException) { Application.ExitThread(); }
    }
    static void Visible(Form form) {
        Check(form.Visible && IsWindowVisible(form.Handle) && !IsIconic(form.Handle),"Settings are natively visible despite hidden process startup");
        Check(Screen.AllScreens.Any(screen=>screen.WorkingArea.IntersectsWith(form.Bounds)),"Settings are within a display");
        Check(Forms().Length==1,"Exactly one settings window is open");
        Check(OwnsResidentMutex(),"This fixture owns the startup or resident instance");
    }
    static void VerifyRecovery(Form form,int tab) {
        Visible(form);
        Check(Selected(form)==tab,"Recovery opens the expected settings tab");
        var retry=Named(form,"StartupRetry") as Button;
        var error=Named(form,"StartupError") as Label;
        Check(retry!=null && retry.Visible && retry.Enabled,"Startup recovery exposes an enabled retry action");
        Rectangle retryBounds=form.RectangleToClient(retry.RectangleToScreen(retry.ClientRectangle));
        Check(form.ClientRectangle.Contains(retryBounds),"Recovery action is fully within the settings window");
        Point retryCenter=retry.PointToScreen(new Point(retry.Width/2,retry.Height/2));
        Control top=form;
        while(true) { Control child=top.GetChildAtPoint(top.PointToClient(retryCenter),GetChildAtPointSkip.Invisible); if(child==null) break; top=child; }
        Check(ReferenceEquals(top,retry),"Recovery action is not covered by the header or page content");
        Check(error!=null && error.Visible && error.Text.Contains("code="),"Recovery displays the microphone initialization failure");
        Check(language=="en"?error.Text.Contains("microphone"):error.Text.Contains("マイク"),"Recovery error follows the app language");
        Check(!(bool)Property(form,"StartupRetryRequested"),"Recovery has not requested a restart before user action");
        Check(!ResidentReady(),"Failed microphone initialization never reports a ready resident");
        // Render only this synthetic settings form; never capture the desktop.
        using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,form.Size)); bitmap.Save(Path.Combine(root,"recovery.png"),ImageFormat.Png); }
    }
    static void Observe(System.Windows.Forms.Timer timer) {
        if(total.ElapsedMilliseconds>14000) throw new TimeoutException("Startup recovery observation timed out at step "+step);
        var forms=Forms();
        if(step==0) {
            if(forms.Length!=1 || !IsWindowVisible(forms[0].Handle)) return;
            firstRecovery=forms[0]; VerifyRecovery(firstRecovery,postInstall?5:0);
            Check(originalConfig.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"config.json"))),"Failed startup does not rewrite configuration");
            Call(firstRecovery,"SelectTab",0);
            Check(InvokeMain("--post-install")==0,"Post-install request reaches an existing recovery window");
            step=1; phase.Restart(); return;
        }
        if(step==1) {
            if(forms.Length!=1 || Selected(forms[0])!=5) return;
            Check(ReferenceEquals(firstRecovery,forms[0]),"Repeated launch reuses recovery settings and selects Connection");
            Check(InvokeMain("--settings")==0,"Settings command reaches an existing recovery window");
            step=2; phase.Restart(); return;
        }
        if(step==2) {
            if(forms.Length!=1 || Selected(forms[0])!=0) return;
            Check(ReferenceEquals(firstRecovery,forms[0]),"Settings command reuses recovery settings and selects General");
            if(recover) {
                var inputs=(IDictionary<string,Control>)Property(firstRecovery,"Inputs");
                ((ComboBox)inputs["MicrophoneDevice"]).SelectedIndex=0;
                Check(originalConfig.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"config.json"))),"Selecting the default microphone remains a draft until retry is selected");
            }
            step=3; phase.Restart();
            ((Button)Named(firstRecovery,"StartupRetry")).PerformClick();
            return;
        }
        if(step==3) {
            if(!recover) {
                if(forms.Length!=1 || ReferenceEquals(firstRecovery,forms[0]) || !Recovery(forms[0])) return;
                Check(firstRecovery.IsDisposed,"Retry closes the previous recovery window");
                VerifyRecovery(forms[0],postInstall?5:0);
                Check(originalConfig.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"config.json"))),"Retry without changes preserves configuration bytes");
                completed=true; timer.Stop(); CloseSettings(forms[0]); return;
            }
            if(!firstRecovery.IsDisposed || !ResidentReady()) return;
            Check(firstRecovery.IsDisposed,"Successful retry disposes the recovery window");
            Check(ResidentReady(),"Successful retry starts the resident after microphone repair");
            var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"config.json")));
            Check(Convert.ToInt32(config["MicrophoneDevice"])==-1,"Retry persists the corrected default microphone");
            if(postInstall) {
                if(forms.Length!=1 || Recovery(forms[0])) return;
                successfulSettings=forms[0]; Visible(successfulSettings);
                Check(Selected(successfulSettings)==5,"Recovered post-install startup opens Connection");
                Check(!Recovery(successfulSettings),"Normal resident settings do not show the recovery action");
                CloseSettings(successfulSettings);
            } else {
                Check(forms.Length==0,"Recovered normal startup remains tray-only");
            }
            step=4; phase.Restart(); return;
        }
        if(step==4 && phase.ElapsedMilliseconds>=350) {
            Check(Forms().Length==0 && Application.MessageLoop && OwnsResidentMutex(),"Closing settings keeps the recovered app resident");
            Check(ResidentReady(),"Recovered app remains ready after closing settings");
            completed=true; timer.Stop(); StopOwnResident();
        }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        root=Path.GetFullPath(args[1]); string output=Path.GetFullPath(args[2]);
        report["checks"]=checks; report["fixtureRoot"]=root; report["realCredentialsAccessed"]=false;
        try {
            try { using(Mutex existing=Mutex.OpenExisting(ResidentMutex)) { throw new InvalidOperationException("A resident already exists; no application was stopped or changed."); } }
            catch(WaitHandleCannotBeOpenedException) { }
            Check(string.Equals(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName,root,StringComparison.OrdinalIgnoreCase),"Harness and application use the isolated installation root");
            var scenario=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"startup-test.json")));
            language=(string)scenario["language"]; postInstall=(bool)scenario["postInstall"]; recover=(bool)scenario["recover"];
            report["language"]=language; report["postInstall"]=postInstall; report["recover"]=recover;
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            main=assembly.GetType("Minus700K.Program",true).GetMethod("Main",Static);
            formType=assembly.GetType("Minus700K.SettingsForm",true);
            var storeType=assembly.GetType("Minus700K.PlanStore",true);
            var isolatedStore=storeType.GetConstructors(Instance)[0].Invoke(new object[]{Path.Combine(root,"connection")});
            storeType.GetField("Default",Static).SetValue(null,isolatedStore);
            Check(ReferenceEquals(storeType.GetField("Default",Static).GetValue(null),isolatedStore),"Credential store is isolated before application startup");
            originalConfig=File.ReadAllBytes(Path.Combine(root,"config.json"));
            var initial=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"config.json")));
            Check(Convert.ToInt32(initial["MicrophoneDevice"])==32767,"Fixture uses an unavailable microphone without changing Windows devices");
            total=Stopwatch.StartNew(); phase=Stopwatch.StartNew();
            using(var timer=new System.Windows.Forms.Timer { Interval=60 }) {
                timer.Tick+=delegate {
                    try { Observe(timer); }
                    catch(Exception e) {
                        timer.Stop(); report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString();
                        foreach(var form in Forms()) CloseSettings(form);
                        StopOwnResident();
                    }
                };
                EventHandler idle=null; idle=delegate { Application.Idle-=idle; timer.Start(); };
                Application.Idle+=idle;
                try {
                    int code=InvokeMain(postInstall?new[]{"--post-install"}:new string[0]);
                    Check(code==(recover?0:1),"Exit code distinguishes recovery success from closing without recovery");
                } finally { timer.Stop(); Application.Idle-=idle; }
            }
            Check(completed,"All recovery and resident-lifetime observations completed");
            Check(!File.Exists(Path.Combine(root,"connection","chatgpt.dat")),"Recovery does not sign in or create credentials");
            if(!recover) Check(originalConfig.SequenceEqual(File.ReadAllBytes(Path.Combine(root,"config.json"))),"Closing unrecovered settings preserves configuration");
        } catch(Exception e) { report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString(); }
        report["passed"]=completed && !report.ContainsKey("error");
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        return (bool)report["passed"]?0:1;
    }
}

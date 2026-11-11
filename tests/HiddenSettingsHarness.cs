using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class HiddenSettingsHarness {
    const BindingFlags InternalInstance=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance;
    const BindingFlags InternalStatic=BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Static;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out WindowRect bounds);
    [StructLayout(LayoutKind.Sequential)] struct WindowRect { internal int Left,Top,Right,Bottom; }
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static readonly List<object> samples=new List<object>();
    static string reportPath;
    static bool passed;
    static void WriteReport() {
        report["passed"]=passed;
        File.WriteAllText(reportPath,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
    }
    static object Sample(Form form,long elapsed) {
        WindowRect rect; GetWindowRect(form.Handle,out rect);
        var bounds=Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom);
        bool onScreen=false;
        foreach(Screen screen in Screen.AllScreens) if(screen.WorkingArea.IntersectsWith(bounds)) onScreen=true;
        bool visible=IsWindowVisible(form.Handle);
        passed=passed && visible && form.Visible && !IsIconic(form.Handle) && onScreen;
        return new { elapsedMs=elapsed,nativeVisible=visible,managedVisible=form.Visible,minimized=IsIconic(form.Handle),onScreen=onScreen,
            foreground=GetForegroundWindow()==form.Handle,handle=form.Handle.ToInt64(),bounds=new[]{rect.Left,rect.Top,rect.Right,rect.Bottom} };
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        reportPath=Path.GetFullPath(args[2]); Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
        report["startedAt"]=DateTimeOffset.Now.ToString("o"); report["processId"]=Process.GetCurrentProcess().Id;
        report["preview"]=false; report["launchWindowStyle"]="Hidden"; report["samples"]=samples;
        report["initialForeground"]=GetForegroundWindow().ToInt64();
        try {
            var assembly=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            // Keep diagnostic log writes beside this report. Live settings and connection data are read only.
            string logRoot=Path.GetDirectoryName(reportPath); Directory.CreateDirectory(Path.Combine(logRoot,"runtime"));
            assembly.GetType("Minus700K.Log",true).GetField("Root",InternalStatic).SetValue(null,logRoot);
            var settingsType=assembly.GetType("Minus700K.Settings",true);
            var settings=settingsType.GetMethod("Load",InternalStatic).Invoke(null,new object[]{args[1]});
            var formType=assembly.GetType("Minus700K.SettingsForm",true);
            var constructor=formType.GetConstructors(InternalInstance)[0];
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using(var context=new ApplicationContext())
            using(var start=new Timer { Interval=120 }) {
                start.Tick+=delegate {
                    start.Stop();
                    try {
                        var construction=Stopwatch.StartNew();
                        using(var form=(Form)constructor.Invoke(new object[]{args[1],settings,null,0,false,null}))
                        using(var observe=new Timer { Interval=50 }) {
                            report["constructorMs"]=construction.ElapsedMilliseconds;
                            var sinceShown=new Stopwatch(); bool sampled=false;
                            form.Shown+=delegate { report["shownEvent"]=true; sinceShown.Start(); observe.Start(); };
                            observe.Tick+=delegate {
                                if(sinceShown.ElapsedMilliseconds>=200) {
                                    if(!sampled) { passed=true; sampled=true; }
                                    samples.Add(Sample(form,sinceShown.ElapsedMilliseconds)); WriteReport();
                                }
                                if(sinceShown.ElapsedMilliseconds>=600) {
                                    observe.Stop();
                                    // Close without saving or prompting, even if a machine-specific option normalizes.
                                    formType.GetField("discard",InternalInstance).SetValue(form,true);
                                    form.Close();
                                }
                            };
                            report["modalResult"]=form.ShowDialog().ToString();
                            report["saved"]=formType.GetProperty("Saved",InternalInstance).GetValue(form,null);
                            passed=passed && sampled && !(bool)report["saved"];
                        }
                    } catch(Exception e) {
                        passed=false; report["error"]=(e is TargetInvocationException && e.InnerException!=null?e.InnerException:e).ToString();
                    } finally { context.ExitThread(); }
                };
                start.Start(); Application.Run(context);
            }
        } catch(Exception e) { passed=false; report["error"]=e.ToString(); }
        report["finishedAt"]=DateTimeOffset.Now.ToString("o"); WriteReport();
        return passed?0:1;
    }
}

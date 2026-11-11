using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// A disposable, noninteractive executable with the real process name. It does
// not use audio, credentials, the network, or any application data outside root.
class InstallerUpdateFixture {
    static string root;
    static readonly JavaScriptSerializer json=new JavaScriptSerializer();
    static void Write(string name,object value) { File.WriteAllText(Path.Combine(root,name),json.Serialize(value),new UTF8Encoding(false)); }
    static bool Exists(string name) { return File.Exists(Path.Combine(root,name)); }
    static bool GateExists() {
        try { using(var gate=Mutex.OpenExisting("Local\\Minus700K_Installing_7E940DB1")) return true; }
        catch(WaitHandleCannotBeOpenedException) { return false; }
    }
    static int Main(string[] args) {
        root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
        if(args.Length>=2 && args[0]=="--install-language") {
            var path=Path.Combine(root,"config.json");
            var config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            config["AppLanguage"]=args[1]; File.WriteAllText(path,json.Serialize(config),new UTF8Encoding(false));
            Write("language-applied.json",new { language=args[1],gatePresent=GateExists() }); return 0;
        }
        string mode=null,legacySignal="Local\\Minus700K_Stop_7E940DB1",residentMutex="Local\\Minus700K_PTT_7E940DB1"; int busyMs=1800;
        foreach(string arg in args) {
            if(arg.StartsWith("--fixture=")) mode=arg.Substring(10);
            if(arg.StartsWith("--busy-ms=")) busyMs=int.Parse(arg.Substring(10));
            if(arg.StartsWith("--legacy-signal=")) legacySignal=arg.Substring(16);
            if(arg.StartsWith("--resident-mutex=")) residentMutex=arg.Substring(17);
        }
        if(mode==null) { Write("restarted.json",new { arguments=String.Join(" ",args),gatePresent=GateExists(),languageAlreadyApplied=Exists("language-applied.json") }); return 0; }
        Directory.CreateDirectory(Path.Combine(root,"runtime"));
        int pid=Process.GetCurrentProcess().Id;
        var clock=Stopwatch.StartNew(); bool requested=false;
        bool legacy=mode=="legacy" || mode=="unknown";
        string signalName=legacy ? legacySignal : "Local\\Minus700K_Update_7E940DB1_"+pid;
        using(var signal=new EventWaitHandle(false,EventResetMode.AutoReset,signalName))
        using(var resident=mode=="install" ? new Mutex(false,residentMutex) : null) {
            Write("ready.json",new { pid=pid,mode=mode });
            while(clock.ElapsedMilliseconds<30000 && !Exists("fixture-exit")) {
                bool busy=clock.ElapsedMilliseconds<busyMs;
                if(legacy && mode!="unknown")
                    File.WriteAllText(Path.Combine(root,"runtime","status.json"),json.Serialize(new {pid=pid,state=busy?"asking":"ready",settingsOpen=false,updated=DateTimeOffset.Now.ToString("o")}),new UTF8Encoding(false));
                if(signal.WaitOne(30)) {
                    requested=true;
                    if(!Exists("request.json")) Write("request.json",new { elapsedMs=clock.ElapsedMilliseconds,busy=busy });
                }
                if(requested && mode!="refuse" && mode!="unknown" && mode!="observer" && !busy) {
                    Write("normal-exit.json",new {elapsedMs=clock.ElapsedMilliseconds,busy=busy}); return 0;
                }
            }
            Write("fixture-exit.json",new {requested=requested,elapsedMs=clock.ElapsedMilliseconds});
        }
        return 0;
    }
}

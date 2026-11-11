using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// A disposable process named Minus700K.exe. It never opens audio, signs in,
// sends shutdown signals, or reads anything outside its dedicated test root.
internal static class UninstallRunningFixture {
    static int Main(string[] args) {
        string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;
        var json=new JavaScriptSerializer();
        if(args.Length==2 && args[0]=="--install-language") {
            string path=Path.Combine(root,"config.json");
            var config=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            config["AppLanguage"]=args[1];
            File.WriteAllText(path,json.Serialize(config),new UTF8Encoding(false));
            return 0;
        }
        string token=null,mutex=null;
        foreach(string arg in args) {
            if(arg.StartsWith("--fixture-token=")) token=arg.Substring(16);
            if(arg.StartsWith("--resident-mutex=")) mutex=arg.Substring(17);
        }
        if(string.IsNullOrEmpty(token) || token.IndexOfAny(Path.GetInvalidFileNameChars())>=0) return 2;
        using(var resident=string.IsNullOrEmpty(mutex)?null:new Mutex(false,mutex)) {
            string prefix=Path.Combine(root,"fixture-"+token);
            File.WriteAllText(prefix+"-ready.json",json.Serialize(new {pid=Process.GetCurrentProcess().Id,mutex=mutex,arguments=args}),new UTF8Encoding(false));
            var timer=Stopwatch.StartNew();
            while(timer.ElapsedMilliseconds<180000 && !File.Exists(prefix+"-exit")) Thread.Sleep(30);
        }
        return 0;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Minus700K {
// One directory per submitted recording. All calls run on the processing worker,
// never on the keyboard monitor or UI thread.
internal sealed class RequestJournal : IDisposable {
    internal readonly string DirectoryPath;
    internal string TemporaryDirectory { get { return Temporary(DirectoryPath); } }
    internal readonly Dictionary<string,object> Data=new Dictionary<string,object>();
    readonly string requestsRoot; readonly bool keep;
    readonly Stopwatch processing=Stopwatch.StartNew();
    // Production passes RequestStorage.RequestsRoot; diagnostics pass an isolated root.
    internal RequestJournal(string requestsRoot,bool keep,DateTimeOffset started) {
        if(string.IsNullOrWhiteSpace(requestsRoot) || !Path.IsPathRooted(requestsRoot)) throw new ArgumentException(L10n.T("messages.RequestJournal.001"),"requestsRoot");
        this.keep=keep; this.requestsRoot=Path.GetFullPath(requestsRoot);
        string id=Guid.NewGuid().ToString("N");
        DirectoryPath=Path.Combine(this.requestsRoot,started.ToString("yyyyMMdd_HHmmss_fff",CultureInfo.InvariantCulture)+"_"+id);
        Directory.CreateDirectory(DirectoryPath);
        Data["schemaVersion"]=4; Data["requestId"]=id; Data["startedAt"]=started.ToString("o");
        Data["status"]="processing"; Data["phase"]="saving_input";
        Save();
    }
    internal void Save() {
        if(!keep) return;
        Data["updatedAt"]=DateTimeOffset.Now.ToString("o");
        // Write a sibling temporary file first so readers don't see a partially written JSON.
        try {
            string path=Path.Combine(DirectoryPath,"request.json"),temp=path+".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(Data),new UTF8Encoding(false));
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } catch(Exception e) { Log.Write("request metadata write failed: "+e.Message); }
    }
    internal void Phase(string phase) { Data["phase"]=phase; Save(); }
    internal void Finish(string status,Exception error) {
        Data["status"]=status; Data["finishedAt"]=DateTimeOffset.Now.ToString("o"); Data["processingMilliseconds"]=processing.ElapsedMilliseconds;
        if(error!=null) { Data["errorType"]=error.GetType().FullName; Data["error"]=error.ToString(); }
        Save();
    }
    internal string SaveScreenshot(OptionalScreenshot image) {
        // Keep the actual submitted PNG beside the audio when diagnostics are
        // enabled. Otherwise it belongs to the request's disposable work area.
        string path=Path.Combine(keep?DirectoryPath:TemporaryDirectory,"screen.png");
        string saved=image.Save(path);
        Data["screenshotFile"]=keep && saved!=null?"screen.png":null;
        return saved;
    }
    internal static void Text(bool enabled,string directory,string filename,string value) {
        if(!enabled) return;
        try { File.WriteAllText(Path.Combine(directory,filename),value??"",new UTF8Encoding(false)); }
        catch(Exception e) { Log.Write("debug file write failed ("+filename+"): "+e.Message); }
    }
    // Codex output files and non-retained inputs are needed only while processing.
    // The optional retained screen.png is outside this disposable work area.
    internal static string Temporary(string directory) {
        string path=Path.Combine(directory,"temporary"); Directory.CreateDirectory(path); return path;
    }
    public void Dispose() {
        try {
            string path=Path.GetFullPath(DirectoryPath),allowed=requestsRoot.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!path.StartsWith(allowed,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Request directory is outside the permitted root.");
            if(!keep) { if(Directory.Exists(path)) Directory.Delete(path,true); return; }
            foreach(string attempt in new[] { path,Path.Combine(path,"web-retry") }) {
                string temporary=Path.Combine(attempt,"temporary");
                if(Directory.Exists(temporary)) Directory.Delete(temporary,true);
            }
        } catch(Exception e) { Log.Write("cleanup "+e.Message); }
    }
}
}

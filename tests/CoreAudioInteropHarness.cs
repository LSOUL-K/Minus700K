using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Web.Script.Serialization;

// Read-only regression for several production coclasses wrapping the same COM
// identity. No capture stream is opened, and no session volume/name is changed.
internal static class CoreAudioInteropHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly app;
    static Exception Unwrap(Exception error) { return error is TargetInvocationException && error.InnerException!=null?error.InnerException:error; }
    static object HeldEnumerator() {
        Type shared=app.GetType("Minus700K.CoreAudioDevices");
        if(shared!=null) return shared.GetMethod("CreateEnumerator",Static).Invoke(null,null);
        // On the old build, make its native microphone coclass the first RCW.
        // Later production constructors for the same CLSID must reproduce the
        // InvalidCastException; a test-only coclass would not prove this path.
        Type native=app.GetType("Minus700K.NativeExclusiveCaptureSession",true);
        return Activator.CreateInstance(native.GetNestedType("EnumeratorClass",BindingFlags.NonPublic),true);
    }
    static void CaptureInventory() {
        app.GetType("Minus700K.NativeExclusiveCaptureSession",true).GetMethod("OpenCaptureProcesses",Static).Invoke(null,new object[]{-1});
    }
    static void PlaybackInventory() {
        Type type=app.GetType("Minus700K.CoreOtherAudioBackend",true); object backend=Activator.CreateInstance(type,true);
        try {
            foreach(object session in (IEnumerable)type.GetMethod("Sessions",Instance).Invoke(backend,null)) ((IDisposable)session).Dispose();
        } finally { ((IDisposable)backend).Dispose(); }
    }
    static void IdentityInventory() {
        Type type=app.GetType("Minus700K.AudioSessionIdentity",true);
        object identity=FormatterServices.GetUninitializedObject(type);
        // Refresh only labels sessions matching this field. UINT_MAX cannot be
        // a Windows process ID, so every real session follows the read-only path.
        type.GetField("processId",Instance).SetValue(identity,uint.MaxValue);
        type.GetMethod("Refresh",Instance).Invoke(identity,null);
    }
    static void RunApartment(string apartment,Barrier together,List<object> rows) {
        object held=null;
        try {
            held=HeldEnumerator();
            if(!together.SignalAndWait(5000)) throw new TimeoutException("Other apartment did not initialize.");
            string[] names={"capture-inventory","playback-inventory","identity-refresh"};
            Action[] paths={CaptureInventory,PlaybackInventory,IdentityInventory};
            for(int iteration=1;iteration<=2;iteration++) for(int i=0;i<paths.Length;i++) {
                var row=new Dictionary<string,object> { {"apartment",apartment},{"path",names[i]},{"iteration",iteration} };
                try { paths[i](); row["passed"]=true; }
                catch(Exception error) { row["passed"]=false; row["error"]=Unwrap(error).ToString(); }
                lock(rows) rows.Add(row);
            }
        } catch(Exception error) {
            lock(rows) rows.Add(new Dictionary<string,object> { {"apartment",apartment},{"path","held-enumerator"},{"passed",false},{"error",Unwrap(error).ToString()} });
        } finally { if(held!=null && Marshal.IsComObject(held)) Marshal.ReleaseComObject(held); }
    }
    static int Main(string[] args) {
        if(args.Length!=2) { Console.Error.WriteLine("Usage: CoreAudioInteropHarness app.exe report.json"); return 2; }
        var report=new Dictionary<string,object>(); var rows=new List<object>(); int result=1;
        try {
            app=Assembly.LoadFrom(Path.GetFullPath(args[0]));
            report["sharedFactory"]=app.GetType("Minus700K.CoreAudioDevices")!=null;
            using(var together=new Barrier(2)) {
                var mta=new Thread(()=>RunApartment("MTA",together,rows)) { IsBackground=true };
                var sta=new Thread(()=>RunApartment("STA",together,rows)) { IsBackground=true };
                mta.SetApartmentState(ApartmentState.MTA); sta.SetApartmentState(ApartmentState.STA); mta.Start(); sta.Start();
                bool ended=mta.Join(20000) && sta.Join(20000);
                lock(rows) report["rows"]=rows.ToArray();
                bool passed=ended && rows.Count==12;
                lock(rows) foreach(Dictionary<string,object> row in rows) passed&=(bool)row["passed"];
                report["passed"]=passed; if(!ended) report["error"]="COM regression threads exceeded timeout.";
                result=passed?0:1;
            }
        } catch(Exception error) { report["passed"]=false; report["error"]=Unwrap(error).ToString(); }
        finally { report["microphoneOpened"]=false; report["sessionPropertiesModified"]=false; File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(report)); }
        return result;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;

// Explicit hardware smoke test. Never invoked by verify.ps1, never saves audio.
internal static class ExclusiveMicrophoneLiveHarness {
    const BindingFlags Static=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static;
    const BindingFlags Instance=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance;
    static Type exclusiveType,sharedType;
    static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    static object Call(object target,string method,params object[] args) {
        try { return target.GetType().GetMethod(method,Instance,null,Type.EmptyTypes,null).Invoke(target,args); }
        catch(TargetInvocationException error) { throw error.InnerException??error; }
    }
    static object StaticCall(Type type,string method,params object[] args) {
        try { return type.GetMethod(method,Static).Invoke(null,args); }
        catch(TargetInvocationException error) { throw error.InnerException??error; }
    }
    static object Create(Type type,int id) {
        try { return type.GetConstructor(Instance,null,new[]{typeof(int)},null).Invoke(new object[]{id}); }
        catch(TargetInvocationException error) { throw error.InnerException??error; }
    }
    static List<uint> Processes(int id,string method) {
        var result=new List<uint>();
        foreach(object pid in (IEnumerable)StaticCall(exclusiveType,method,id)) result.Add(Convert.ToUInt32(pid));
        return result;
    }
    static void Dispose(object value) { if(value!=null) ((IDisposable)value).Dispose(); }
    static int ClearAudio(object capture) {
        byte[] bytes=(byte[])Call(capture,"Stop");
        try { Check(bytes.Length>0 && bytes.Length%2==0,"Capture must return nonempty PCM16 data."); return bytes.Length; }
        finally { Array.Clear(bytes,0,bytes.Length); }
    }
    static int SharedAfterRelease(int id) {
        object shared=null;
        try { shared=Create(sharedType,id); Call(shared,"Start"); Thread.Sleep(100); return ClearAudio(shared); }
        finally { Dispose(shared); }
    }
    static void Preemption(int id,Dictionary<string,object> report) {
        uint own=(uint)Process.GetCurrentProcess().Id;
        foreach(uint pid in Processes(id,"OpenCaptureProcesses")) Check(pid==own,"Another process has an open capture stream; preemption test refused: "+pid);
        object shared=null,exclusive=null,competitor=null;
        try {
            shared=Create(sharedType,id); Call(shared,"Start"); Thread.Sleep(100);
            bool acquired=false;
            try { exclusive=Create(exclusiveType,id); Call(exclusive,"Start"); acquired=true; }
            catch(Exception error) { report["preemptionError"]=error.GetType().FullName+": "+error.Message; }
            report["preemptionSupported"]=acquired;
            if(!acquired) {
                Dispose(exclusive); exclusive=null;
                report["sharedPcmBytesAfterDeniedPreemption"]=ClearAudio(shared);
            } else {
                Thread.Sleep(100); bool blocked=false;
                try { competitor=Create(sharedType,id); }
                catch(Exception) { blocked=true; }
                finally { Dispose(competitor); competitor=null; }
                Check(blocked,"Shared capture cannot reopen while preempting exclusive capture is active.");
                report["preemptingExclusivePcmBytes"]=ClearAudio(exclusive); Dispose(exclusive); exclusive=null;
                // The preempted stream may be invalidated by Windows. Dispose it
                // and verify a fresh shared stream works after exclusive release.
                Dispose(shared); shared=null;
                report["sharedPcmBytesAfterPreemption"]=SharedAfterRelease(id);
            }
        } finally { Dispose(competitor); Dispose(exclusive); Dispose(shared); }
    }
    static void Inventory(Dictionary<string,object> report) {
        var rows=new List<object>(); var devices=(string[])StaticCall(sharedType,"Devices");
        var identifiers=new List<int> { -1 }; var names=new List<string> { "Windows default microphone" };
        foreach(string device in devices) { identifiers.Add(int.Parse(device.Substring(0,device.IndexOf(':')))); names.Add(device); }
        for(int i=0;i<identifiers.Count;i++) {
            var row=new Dictionary<string,object> { {"deviceId",identifiers[i]},{"name",names[i]} };
            try { row["endpointId"]=StaticCall(exclusiveType,"ResolveEndpointId",identifiers[i]); row["activeCapturePids"]=Processes(identifiers[i],"ActiveCaptureProcesses"); row["openCapturePids"]=Processes(identifiers[i],"OpenCaptureProcesses"); }
            catch(Exception error) { row["error"]=error.ToString(); }
            rows.Add(row);
        }
        report["devices"]=rows; report["microphoneOpened"]=false;
    }
    static void RepeatOnly(int id,Dictionary<string,object> report) {
        report["mode"]="repeat-only"; report["deviceId"]=id; report["endpointId"]=StaticCall(exclusiveType,"ResolveEndpointId",id);
        List<uint> active=Processes(id,"OpenCaptureProcesses"); report["openCapturePidsBefore"]=active;
        uint own=(uint)Process.GetCurrentProcess().Id;
        foreach(uint pid in active) Check(pid==own,"Selected microphone has an open stream from process "+pid+". Repeat test refused.");
        Type wrapperType=exclusiveType.Assembly.GetType("Minus700K.RecordingMicrophone",true);
        object recorder=wrapperType.GetConstructor(Instance,null,new[]{typeof(int),typeof(bool)},null).Invoke(new object[]{id,true});
        var rows=new List<object>(); Exception failure=null; bool timedOut=false;
        report["mainApartment"]=Thread.CurrentThread.GetApartmentState().ToString();
        using(var finished=new ManualResetEvent(false))
        using(var dispatcher=new System.Windows.Forms.Control())
        using(var context=new System.Windows.Forms.ApplicationContext())
        using(var timeout=new System.Windows.Forms.Timer { Interval=45000 }) {
            dispatcher.CreateControl(); IntPtr handle=dispatcher.Handle;
            var worker=new Thread(delegate() {
                try {
                    report["recordingApartment"]=Thread.CurrentThread.GetApartmentState().ToString();
                    for(int cycle=1;cycle<=5;cycle++) {
                        var row=new Dictionary<string,object> { {"cycle",cycle} }; var elapsed=Stopwatch.StartNew();
                        try {
                            Call(recorder,"Start"); row["startMs"]=elapsed.ElapsedMilliseconds;
                            Thread.Sleep(250);
                            var stopping=Stopwatch.StartNew(); row["pcmBytes"]=ClearAudio(recorder); row["stopMs"]=stopping.ElapsedMilliseconds;
                            row["passed"]=true;
                        } catch(Exception error) { row["passed"]=false; row["error"]=error.ToString(); throw; }
                        finally { row["elapsedMs"]=elapsed.ElapsedMilliseconds; lock(rows) rows.Add(row); }
                        // Deliberately no shared stream, GC, UI operation, or
                        // endpoint query between recordings: any can mask leaks.
                        Thread.Sleep(100);
                    }
                } catch(Exception error) { failure=error; }
                finally {
                    try { finished.Set(); } catch(ObjectDisposedException) { }
                    try { dispatcher.BeginInvoke((System.Windows.Forms.MethodInvoker)delegate { context.ExitThread(); }); }
                    catch(InvalidOperationException) { }
                }
            }) { IsBackground=true,Name="Exclusive microphone repeated PTT fixture" };
            worker.SetApartmentState(ApartmentState.MTA);
            timeout.Tick+=delegate { timedOut=true; context.ExitThread(); };
            try {
                timeout.Start(); worker.Start();
                // Resident Start/Stop runs on its keyboard MTA while this STA
                // keeps its ordinary idle message loop. No settings UI is opened.
                System.Windows.Forms.Application.Run(context);
                timeout.Stop();
                lock(rows) report["cycles"]=rows.ToArray();
                Check(!timedOut && finished.WaitOne(0),"Repeated capture did not finish within 45 seconds.");
                worker.Join(1000);
                if(failure!=null) throw failure;
            } finally {
                // Do not block the reporting thread on a timed-out driver's gate.
                // Process exit releases a still-running background fixture.
                if(finished.WaitOne(0)) Dispose(recorder);
            }
        }
        report["microphoneOpened"]=true; report["audioSaved"]=false;
        report["checks"]="five exclusive Start/Stop cycles / production RecordingMicrophone wrapper / background MTA caller / idle WinForms STA / no intervening shared capture, forced GC, endpoint queries, or settings UI";
    }
    static void Live(int id,Dictionary<string,object> report) {
        report["deviceId"]=id; report["endpointId"]=StaticCall(exclusiveType,"ResolveEndpointId",id);
        List<uint> active=Processes(id,"OpenCaptureProcesses"); report["openCapturePidsBefore"]=active; report["activeCapturePidsBefore"]=Processes(id,"ActiveCaptureProcesses");
        uint own=(uint)Process.GetCurrentProcess().Id;
        foreach(uint pid in active) Check(pid==own,"Selected microphone is being captured by process "+pid+". Live test refused.");
        object first=null,competitor=null,shared=null;
        try {
            first=Create(exclusiveType,id); Call(first,"Start"); Thread.Sleep(250);
            bool exclusiveBlocked=false;
            try { competitor=Create(exclusiveType,id); Call(competitor,"Start"); }
            catch(Exception error) { exclusiveBlocked=true; report["competingExclusiveError"]=error.GetType().FullName+": "+error.Message; }
            finally { Dispose(competitor); competitor=null; }
            Check(exclusiveBlocked,"A competing exclusive capture must fail while the endpoint is reserved.");
            bool sharedBlocked=false;
            try { shared=Create(sharedType,id); }
            catch(Exception error) { sharedBlocked=true; report["competingSharedError"]=error.GetType().FullName+": "+error.Message; }
            finally { Dispose(shared); shared=null; }
            Check(sharedBlocked,"A competing shared capture must fail while exclusive capture is active.");
            report["exclusivePcmBytes"]=ClearAudio(first); Dispose(first); first=null;
            report["sharedPcmBytesAfterStop"]=SharedAfterRelease(id);
            first=Create(exclusiveType,id); Call(first,"Start"); Thread.Sleep(100); Dispose(first); first=null;
            report["sharedPcmBytesAfterDispose"]=SharedAfterRelease(id);
            Preemption(id,report);
            report["checks"]="exclusive capture / competing exclusive denied / shared open denied / PCM16 output / Stop releases endpoint / active Dispose releases endpoint / shared recording works after each release / own shared-stream preemption or fail-closed denial";
            report["microphoneOpened"]=true; report["audioSaved"]=false;
        } finally { Dispose(shared); Dispose(competitor); Dispose(first); }
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) { Console.Error.WriteLine("Usage: ExclusiveMicrophoneLiveHarness app.exe report.json inventory|deviceId|deviceId:repeat"); return 2; }
        var report=new Dictionary<string,object>(); var elapsed=Stopwatch.StartNew(); int result=1;
        try {
            var app=Assembly.LoadFrom(Path.GetFullPath(args[0])); exclusiveType=app.GetType("Minus700K.ExclusiveMicrophone",true); sharedType=app.GetType("Minus700K.WaveRecorder",true);
            string root=Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(args[0]))).FullName;
            app.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,root);
            if(args[2]=="inventory") Inventory(report);
            else if(args[2].EndsWith(":repeat",StringComparison.Ordinal)) RepeatOnly(int.Parse(args[2].Substring(0,args[2].Length-7)),report);
            else Live(int.Parse(args[2]),report);
            report["passed"]=true; result=0;
        } catch(Exception error) { report["passed"]=false; report["error"]=error.ToString(); }
        finally {
            report["elapsedMs"]=elapsed.ElapsedMilliseconds;
            File.WriteAllText(args[1],new JavaScriptSerializer().Serialize(report));
        }
        return result;
    }
}

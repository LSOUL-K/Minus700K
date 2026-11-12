using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minus700K {
// Keep the bookkeeping separate from COM so restoration can be verified without
// touching the user's mixer. Each enumeration supplies separately owned handles.
internal interface IOtherAudioSession : IDisposable {
    string Id { get; }
    uint ProcessId { get; }
    float Volume { get; set; }
}
internal interface IOtherAudioBackend : IDisposable {
    IEnumerable<IOtherAudioSession> Sessions();
}
internal interface IOtherAudioWatch { void StopWatching(); }

// Public visibility is required for the CLR to expose this callback to COM.
[ComVisible(true),Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOtherAudioSessionNotification {
    [PreserveSig] int OnSessionCreated(IntPtr session);
}
[ComVisible(true),ClassInterface(ClassInterfaceType.None)]
public sealed class OtherAudioSessionNotification : IOtherAudioSessionNotification,IDisposable {
    readonly object gate=new object(); readonly Queue<IntPtr> pending=new Queue<IntPtr>(); bool disposed;
    public int OnSessionCreated(IntPtr session) {
        lock(gate) {
            if(!disposed && session!=IntPtr.Zero) { Marshal.AddRef(session); pending.Enqueue(session); }
        }
        return 0;
    }
    internal IntPtr Take() { lock(gate) { return pending.Count==0?IntPtr.Zero:pending.Dequeue(); } }
    public void Dispose() {
        lock(gate) { disposed=true; while(pending.Count>0) Marshal.Release(pending.Dequeue()); }
    }
}

internal sealed class OtherApplicationAudioState : IDisposable {
    sealed class Saved {
        internal IOtherAudioSession Session; internal float Original; internal int Applied=-1;
    }
    readonly IOtherAudioBackend backend; readonly uint processId;
    readonly Dictionary<string,Saved> saved=new Dictionary<string,Saved>(StringComparer.Ordinal);
    int percentage=100; bool disposed;
    internal int PendingRestores { get { return saved.Count; } }
    internal OtherApplicationAudioState(IOtherAudioBackend backend,uint ownProcessId) { this.backend=backend; processId=ownProcessId; }
    internal void Start(int value) {
        if(value<0 || value>100) throw new ArgumentOutOfRangeException("value");
        if(disposed) return;
        if(value==100) { Stop(); return; }
        percentage=value; Refresh();
    }
    internal void Refresh() {
        if(disposed) return;
        if(percentage==100) { Restore(); return; }
        try {
            foreach(var session in backend.Sessions()) {
                bool keep=false;
                try {
                    if(session.ProcessId==processId || saved.ContainsKey(session.Id)) continue;
                    var entry=new Saved { Session=session,Original=session.Volume };
                    // Save before writing: even a failed COM call may have taken effect.
                    saved.Add(session.Id,entry); keep=true;
                } catch(Exception e) { Report("session",e); }
                finally { if(!keep) session.Dispose(); }
            }
            foreach(var entry in saved.Values) {
                try {
                    float target=entry.Original*percentage/100f;
                    if(entry.Applied==percentage && Math.Abs(entry.Session.Volume-target)<0.000001f) continue;
                    entry.Session.Volume=target; entry.Applied=percentage;
                }
                catch(Exception e) { Report("adjust",e); }
            }
        } catch(Exception e) {
            Report("enumeration",e); Stop();
        }
    }
    internal void Stop() {
        percentage=100; Restore();
        var watch=backend as IOtherAudioWatch;
        if(watch!=null) try { watch.StopWatching(); } catch(Exception e) { Report("watch cleanup",e); }
    }
    void Restore() {
        foreach(var pair in new List<KeyValuePair<string,Saved>>(saved)) {
            try { pair.Value.Session.Volume=pair.Value.Original; }
            catch(Exception e) { pair.Value.Applied=-1; Report("restore",e); continue; }
            saved.Remove(pair.Key); pair.Value.Session.Dispose();
        }
    }
    internal static void Report(string operation,Exception error) { Log.Write("other application audio "+operation+": "+error.GetType().Name+" "+error.Message); }
    public void Dispose() {
        if(disposed) return;
        Stop();
        // Retry transient failures before releasing the final session references.
        Restore(); disposed=true;
        foreach(var entry in saved.Values) entry.Session.Dispose();
        saved.Clear(); backend.Dispose();
    }
}

// All Core Audio interfaces are created, used and released on this MTA thread.
// Start/Stop acknowledge the change only after the mixer operation has completed.
internal sealed class OtherApplicationAudio : IDisposable {
    readonly object gate=new object(); Thread worker;
    readonly IOtherAudioBackend backend; readonly uint processId;
    int requested=100; long version,completed; bool disposed,finished;
    internal OtherApplicationAudio() : this(new CoreOtherAudioBackend(),(uint)Process.GetCurrentProcess().Id) { }
    internal OtherApplicationAudio(IOtherAudioBackend backend,uint ownProcessId) { this.backend=backend; processId=ownProcessId; }
    internal void Start(int percentage) {
        if(percentage<0 || percentage>100) throw new ArgumentOutOfRangeException("percentage");
        Change(percentage);
    }
    internal void Stop() { Change(100); }
    void Change(int percentage) {
        lock(gate) {
            if(disposed || finished) return;
            if(worker==null) {
                if(percentage==100) return;
                worker=new Thread(Run) { IsBackground=true,Name="Minus700K other application audio" };
                worker.SetApartmentState(ApartmentState.MTA); worker.Start();
            }
            requested=percentage; long ticket=++version; Monitor.PulseAll(gate);
            while(completed<ticket && !finished) Monitor.Wait(gate);
        }
    }
    void Run() {
        OtherApplicationAudioState state=null;
        try {
            state=new OtherApplicationAudioState(backend,processId);
            while(true) {
                int percentage; long ticket; bool exit,changed;
                lock(gate) {
                    while(!disposed && completed==version && requested==100 && state.PendingRestores==0) Monitor.Wait(gate);
                    percentage=requested; ticket=version; exit=disposed; changed=completed!=version;
                }
                if(exit) break;
                if(changed) state.Start(percentage); else state.Refresh();
                lock(gate) {
                    completed=ticket; Monitor.PulseAll(gate);
                    if(!disposed && completed==version && (requested<100 || state.PendingRestores>0)) Monitor.Wait(gate,200);
                }
            }
        } catch(Exception e) { OtherApplicationAudioState.Report("worker",e); }
        finally {
            try { if(state!=null) state.Dispose(); }
            catch(Exception e) { OtherApplicationAudioState.Report("final restoration",e); }
            finally { lock(gate) { finished=true; Monitor.PulseAll(gate); } }
        }
    }
    public void Dispose() {
        Thread wait;
        lock(gate) { if(disposed) return; disposed=true; requested=100; Monitor.PulseAll(gate); wait=worker; }
        if(wait!=null) wait.Join(); else backend.Dispose();
    }
}

internal sealed class CoreOtherAudioBackend : IOtherAudioBackend,IOtherAudioWatch {
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevices {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IDevicesList devices);
    }
    [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevicesList {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index,out IDevice device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
        [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object result);
        void OpenPropertyStore();
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IManager {
        void GetAudioSessionControl(); void GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out ISessions sessions);
        [PreserveSig] int RegisterSessionNotification(IOtherAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IOtherAudioSessionNotification notification);
    }
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessions {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)] out object control);
    }
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IControl {
        void GetState(); void GetDisplayName(); void SetDisplayName(); void GetIconPath(); void SetIconPath();
        void GetGroupingParam(); void SetGroupingParam(); void RegisterAudioSessionNotification(); void UnregisterAudioSessionNotification();
        void GetSessionIdentifier();
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
    [ComImport,Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IVolume {
        [PreserveSig] int SetMasterVolume(float volume,ref Guid context);
        [PreserveSig] int GetMasterVolume(out float volume);
    }
    sealed class Endpoint : IDisposable {
        internal readonly IManager Manager; internal readonly OtherAudioSessionNotification Notification=new OtherAudioSessionNotification();
        internal Endpoint(IDevice device) {
            object manager=null;
            try {
                Guid iid=typeof(IManager).GUID; Check(device.Activate(ref iid,23,IntPtr.Zero,out manager));
                Manager=(IManager)manager; Check(Manager.RegisterSessionNotification(Notification));
            } catch { Notification.Dispose(); Release(manager); throw; }
        }
        internal void Collect(List<IOtherAudioSession> target) {
            ISessions sessions=null;
            try {
                Check(Manager.GetSessionEnumerator(out sessions)); int count; Check(sessions.GetCount(out count));
                for(int i=0;i<count;i++) {
                    object control=null;
                    try { Check(sessions.GetSession(i,out control)); Add(target,control); control=null; }
                    catch(COMException) { }
                    finally { Release(control); }
                }
                IntPtr pointer;
                while((pointer=Notification.Take())!=IntPtr.Zero) {
                    object control=null;
                    try { control=Marshal.GetObjectForIUnknown(pointer); Add(target,control); control=null; }
                    catch(COMException) { }
                    finally { Release(control); Marshal.Release(pointer); }
                }
            } finally { Release(sessions); }
        }
        static void Add(List<IOtherAudioSession> target,object control) { target.Add(new Session(control)); }
        public void Dispose() {
            try { Manager.UnregisterSessionNotification(Notification); }
            finally { Notification.Dispose(); Release(Manager); }
        }
    }
    sealed class Session : IOtherAudioSession {
        object control; readonly IVolume volume;
        static readonly Guid EventContext=new Guid("B03EBE86-F395-4CA3-B18C-773028DA6846");
        public string Id { get; private set; }
        public uint ProcessId { get; private set; }
        internal Session(object control) {
            var detail=(IControl)control; string id; uint pid;
            Check(detail.GetSessionInstanceIdentifier(out id)); Check(detail.GetProcessId(out pid));
            volume=(IVolume)control; Id=id; ProcessId=pid; this.control=control;
        }
        public float Volume {
            get { float result; Check(volume.GetMasterVolume(out result)); return result; }
            set { Guid context=EventContext; Check(volume.SetMasterVolume(value,ref context)); }
        }
        public void Dispose() { if(control!=null) { Release(control); control=null; } }
    }
    readonly Dictionary<string,Endpoint> endpoints=new Dictionary<string,Endpoint>(StringComparer.Ordinal);
    static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public IEnumerable<IOtherAudioSession> Sessions() {
        var result=new List<IOtherAudioSession>(); var seen=new HashSet<string>(StringComparer.Ordinal);
        IDevices enumerator=null; IDevicesList devices=null;
        try {
            enumerator=(IDevices)CoreAudioDevices.CreateEnumerator(); Check(enumerator.EnumAudioEndpoints(0,1,out devices));
            uint count; Check(devices.GetCount(out count));
            for(uint i=0;i<count;i++) {
                IDevice device=null; string id=null;
                try {
                    Check(devices.Item(i,out device)); Check(device.GetId(out id)); seen.Add(id);
                    Endpoint endpoint;
                    if(!endpoints.TryGetValue(id,out endpoint)) { endpoint=new Endpoint(device); endpoints.Add(id,endpoint); }
                    endpoint.Collect(result);
                } catch(COMException) {
                    // Endpoints can disappear during enumeration; reopen them on the next pass.
                    Endpoint old;
                    if(id!=null && endpoints.TryGetValue(id,out old)) { endpoints.Remove(id); old.Dispose(); }
                } finally { Release(device); }
            }
            foreach(string id in new List<string>(endpoints.Keys)) {
                if(!seen.Contains(id)) { var old=endpoints[id]; endpoints.Remove(id); old.Dispose(); }
            }
            return result;
        } catch { foreach(var session in result) session.Dispose(); throw; }
        finally { Release(devices); Release(enumerator); }
    }
    public void StopWatching() {
        foreach(var endpoint in endpoints.Values) {
            try { endpoint.Dispose(); } catch(Exception e) { OtherApplicationAudioState.Report("endpoint cleanup",e); }
        }
        endpoints.Clear();
    }
    public void Dispose() { StopWatching(); }
}
}

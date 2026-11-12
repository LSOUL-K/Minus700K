using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minus700K {
internal interface IExclusiveCaptureSession : IDisposable {
    int SampleRate { get; } int Channels { get; } int BitsPerSample { get; } int ValidBits { get; } bool FloatingPoint { get; }
    void Start(); bool TryRead(out byte[] packet); void Stop();
}

// No microphone is opened by the constructor. A single STA owns every native
// object for one recording, and releases the endpoint before PCM conversion.
internal sealed class ExclusiveMicrophone : IAudioRecorder {
    sealed class RecordingRun {
        internal readonly ManualResetEvent Requested=new ManualResetEvent(false),Ready=new ManualResetEvent(false),Done=new ManualResetEvent(false);
        internal readonly MemoryStream Pcm=new MemoryStream(); internal Thread Worker;
        internal Exception Error; internal bool Started; internal int Rate,Channels,Bits,Valid; internal bool Float;
        int references=1;
        internal void AddRef() { Interlocked.Increment(ref references); }
        internal void Release() { if(Interlocked.Decrement(ref references)==0) { Requested.Dispose(); Ready.Dispose(); Done.Dispose(); Pcm.Dispose(); } }
    }
    readonly object gate=new object(); readonly int deviceId,startTimeout;
    readonly Func<int,IExclusiveCaptureSession> factory; RecordingRun current; bool disposed;
    internal ExclusiveMicrophone(int waveDeviceId) : this(waveDeviceId,id=>new NativeExclusiveCaptureSession(id),5000) { }
    internal ExclusiveMicrophone(int waveDeviceId,Func<int,IExclusiveCaptureSession> factory) : this(waveDeviceId,factory,5000) { }
    internal ExclusiveMicrophone(int waveDeviceId,Func<int,IExclusiveCaptureSession> factory,int startTimeoutMs) {
        if(waveDeviceId < -1) throw new ArgumentOutOfRangeException("waveDeviceId");
        if(factory==null) throw new ArgumentNullException("factory");
        if(startTimeoutMs<1 || startTimeoutMs>60000) throw new ArgumentOutOfRangeException("startTimeoutMs");
        deviceId=waveDeviceId; this.factory=factory; startTimeout=startTimeoutMs;
    }
    internal static string ResolveEndpointId(int waveDeviceId) { return NativeExclusiveCaptureSession.ResolveEndpointId(waveDeviceId); }
    internal static uint[] ActiveCaptureProcesses(int waveDeviceId) { return NativeExclusiveCaptureSession.ActiveCaptureProcesses(waveDeviceId); }
    internal static uint[] OpenCaptureProcesses(int waveDeviceId) { return NativeExclusiveCaptureSession.OpenCaptureProcesses(waveDeviceId); }
    internal Exception CaptureError { get { lock(gate) { return current==null?null:Volatile.Read(ref current.Error); } } }
    public void Start() { Start(null); }
    internal void Start(Func<bool> cancelled) {
        if(cancelled!=null && cancelled()) throw new OperationCanceledException();
        RecordingRun value;
        lock(gate) {
            if(disposed) throw new ObjectDisposedException("ExclusiveMicrophone");
            if(current!=null) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.failed"));
            value=new RecordingRun(); current=value; value.AddRef(); value.AddRef(); // caller and worker
            value.Worker=new Thread(()=>Capture(value)) { IsBackground=true,Name="Minus700K exclusive microphone" };
            value.Worker.SetApartmentState(ApartmentState.STA);
            try { value.Worker.Start(); }
            catch { current=null; value.Release(); value.Release(); value.Release(); throw; }
        }
        try {
            var waiting=Stopwatch.StartNew();
            while(!value.Ready.WaitOne(0)) {
                // Poll on the caller (keyboard) thread so a released PTT or
                // cancellation key is latched while a device is opening.
                if(cancelled!=null && cancelled()) throw new OperationCanceledException();
                int remaining=startTimeout-(int)waiting.ElapsedMilliseconds;
                if(remaining<=0) throw new TimeoutException(L10n.T("messages.ExclusiveMicrophone.timeout"));
                value.Ready.WaitOne(Math.Min(10,remaining));
            }
            if((cancelled!=null && cancelled()) || value.Requested.WaitOne(0)) throw new OperationCanceledException();
            if(!value.Started || value.Error!=null) {
                if(value.Error!=null) throw value.Error;
                throw new OperationCanceledException(L10n.T("messages.ExclusiveMicrophone.failed"));
            }
        } catch {
            value.Requested.Set();
            // A slow driver may still be returning from Initialize. It will
            // observe cancellation and release its objects without recording.
            if(value.Done.WaitOne(2000)) Detach(value);
            throw;
        } finally { value.Release(); }
    }
    void Capture(RecordingRun value) {
        IExclusiveCaptureSession session=null; bool stopped=false;
        try {
            if(value.Requested.WaitOne(0)) return;
            session=factory(deviceId);
            value.Rate=session.SampleRate; value.Channels=session.Channels; value.Bits=session.BitsPerSample; value.Valid=session.ValidBits; value.Float=session.FloatingPoint;
            if(value.Requested.WaitOne(0)) return;
            session.Start(); value.Started=true; value.Ready.Set(); var elapsed=Stopwatch.StartNew();
            while(!value.Requested.WaitOne(5)) {
                Drain(session,value,false);
                if(elapsed.Elapsed.TotalSeconds>190) throw new TimeoutException(L10n.T("messages.ExclusiveMicrophone.timeout"));
            }
            session.Stop(); stopped=true; Drain(session,value,true);
        } catch(Exception e) { value.Error=e; }
        finally {
            if(session!=null) {
                try { if(!stopped) session.Stop(); }
                catch(Exception e) { if(value.Error==null) value.Error=e; }
                try { session.Dispose(); }
                catch(Exception e) { if(value.Error==null) value.Error=e; }
            }
            value.Ready.Set(); value.Done.Set(); value.Release();
        }
    }
    static void Drain(IExclusiveCaptureSession session,RecordingRun value,bool final) {
        for(int n=0;n<1024;n++) {
            if(!final && value.Requested.WaitOne(0)) return;
            byte[] packet; if(!session.TryRead(out packet)) return;
            if(packet==null || packet.Length==0 || value.Pcm.Length+packet.Length>256L*1024*1024) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.format"));
            value.Pcm.Write(packet,0,packet.Length);
        }
        throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.failed"));
    }
    void Detach(RecordingRun value) { lock(gate) { if(current==value) { current=null; value.Release(); } } }
    public byte[] Stop() {
        RecordingRun value;
        lock(gate) { value=current; if(value==null) return new byte[0]; value.AddRef(); value.Requested.Set(); }
        try {
            if(!value.Done.WaitOne(5000)) throw new TimeoutException(L10n.T("messages.ExclusiveMicrophone.timeout"));
            Detach(value);
            if(value.Error!=null) throw value.Error;
            if(!value.Started) return new byte[0];
            // Done is signaled only after all native capture references are gone.
            return MicrophonePcm.ToMono16k(value.Pcm.ToArray(),value.Rate,value.Channels,value.Bits,value.Valid,value.Float);
        } finally { value.Release(); }
    }
    public void Dispose() {
        RecordingRun value;
        lock(gate) { if(disposed) return; disposed=true; value=current; if(value==null) return; value.AddRef(); value.Requested.Set(); }
        try {
            if(!value.Done.WaitOne(5000)) Log.Write("exclusive microphone driver cleanup is still pending");
            Detach(value); // The worker retains ownership until native cleanup finishes.
        } finally { value.Release(); }
    }
}

internal sealed class NativeExclusiveCaptureSession : IExclusiveCaptureSession {
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevices {
        void EnumAudioEndpoints(); void GetDefaultAudioEndpoint();
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id,out IDevice device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
        [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object result);
        void OpenPropertyStore(); void GetId();
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IClient {
        [PreserveSig] int Initialize(int shareMode,uint flags,long duration,long periodicity,IntPtr format,IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        void GetStreamLatency(); void GetCurrentPadding();
        [PreserveSig] int IsFormatSupported(int shareMode,IntPtr format,IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long normal,out long minimum);
        [PreserveSig] int Start(); [PreserveSig] int Stop(); void Reset(); void SetEventHandle();
        [PreserveSig] int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)] out object service);
    }
    [ComImport,Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ICapture {
        [PreserveSig] int GetBuffer(out IntPtr data,out uint frames,out uint flags,out ulong position,out ulong timestamp);
        [PreserveSig] int ReleaseBuffer(uint frames);
    }
    [DllImport("winmm.dll")] static extern uint waveInMessage(IntPtr device,uint message,IntPtr first,IntPtr second);
    const int UnsupportedFormat=unchecked((int)0x88890008),BufferError=unchecked((int)0x88890018);
    IDevices enumerator; IDevice device; IClient client; ICapture capture;
    NativeFormat format; bool started,disposed; int emptyErrors;
    public int SampleRate { get { return format.Rate; } } public int Channels { get { return format.Channels; } }
    public int BitsPerSample { get { return format.Bits; } } public int ValidBits { get { return format.Valid; } }
    public bool FloatingPoint { get { return format.Float; } }
    internal NativeExclusiveCaptureSession(int deviceId) {
        try {
            string id=ResolveEndpointId(deviceId); enumerator=(IDevices)CoreAudioDevices.CreateEnumerator();
            Check(enumerator.GetDevice(id,out device)); uint state; Check(device.GetState(out state));
            if((state&1)==0) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            object result=null; Guid iid=typeof(IClient).GUID;
            try { Check(device.Activate(ref iid,23,IntPtr.Zero,out result)); client=(IClient)result; result=null; }
            finally { Release(result); }
            format=SelectFormat(client); long normal,minimum; Check(client.GetDevicePeriod(out normal,out minimum));
            IntPtr pointer=format.Allocate();
            try { Check(client.Initialize(1,0,Math.Max(normal,1000000L),0,pointer,IntPtr.Zero)); } // EXCLUSIVE, never shared.
            finally { Marshal.FreeCoTaskMem(pointer); }
            iid=typeof(ICapture).GUID;
            try { Check(client.GetService(ref iid,out result)); capture=(ICapture)result; result=null; }
            finally { Release(result); }
        } catch { Dispose(); throw; }
    }
    public void Start() { Check(client.Start()); started=true; }
    public void Stop() { if(started) { started=false; Check(client.Stop()); } }
    public bool TryRead(out byte[] packet) {
        packet=null; IntPtr pointer; uint frames,flags; ulong position,timestamp;
        int hr=capture.GetBuffer(out pointer,out frames,out flags,out position,out timestamp);
        if(hr==BufferError && ++emptyErrors<100) return false;
        Check(hr); emptyErrors=0; if(frames==0) return false;
        try {
            if(frames>(uint)SampleRate*2) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.format"));
            packet=new byte[checked((int)frames*format.Align)];
            if((flags&2)==0) { if(pointer==IntPtr.Zero) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.failed")); Marshal.Copy(pointer,packet,0,packet.Length); }
            else if(!FloatingPoint && BitsPerSample==8) for(int i=0;i<packet.Length;i++) packet[i]=128;
            return true;
        } finally { Check(capture.ReleaseBuffer(frames)); }
    }
    public void Dispose() {
        if(disposed) return; disposed=true;
        try { Stop(); } catch { }
        Release(capture); capture=null; Release(client); client=null; Release(device); device=null; Release(enumerator); enumerator=null;
    }
    static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    internal static string ResolveEndpointId(int waveDeviceId) {
        if(waveDeviceId < -1) throw new ArgumentOutOfRangeException("waveDeviceId");
        IntPtr size=Marshal.AllocHGlobal(8),flags=Marshal.AllocHGlobal(4),buffer=IntPtr.Zero;
        try {
            if(waveDeviceId==-1) {
                Marshal.WriteInt32(size,-1); Marshal.WriteInt32(flags,0);
                if(waveInMessage(new IntPtr((long)uint.MaxValue),0x2015,size,flags)!=0 || (waveDeviceId=Marshal.ReadInt32(size))<0) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            }
            Marshal.WriteInt64(size,0);
            if(waveInMessage(new IntPtr(waveDeviceId),0x812,size,IntPtr.Zero)!=0) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            long bytes=Marshal.ReadInt64(size);
            if(bytes<2 || bytes>32768 || (bytes&1)!=0) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            buffer=Marshal.AllocHGlobal((int)bytes); Marshal.WriteInt16(buffer,(int)bytes-2,0);
            if(waveInMessage(new IntPtr(waveDeviceId),0x811,buffer,new IntPtr(bytes))!=0) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            string id=Marshal.PtrToStringUni(buffer); if(string.IsNullOrEmpty(id)) throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.disconnected"));
            return id;
        } finally { if(buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(flags); Marshal.FreeHGlobal(size); }
    }
    sealed class NativeFormat {
        internal byte[] Bytes; internal int Rate,Channels,Bits,Valid,Align; internal bool Float;
        internal IntPtr Allocate() { IntPtr pointer=Marshal.AllocCoTaskMem(Bytes.Length); Marshal.Copy(Bytes,0,pointer,Bytes.Length); return pointer; }
        internal static NativeFormat Parse(byte[] bytes) {
            if(bytes.Length<18) return null;
            int tag=BitConverter.ToUInt16(bytes,0),channels=BitConverter.ToUInt16(bytes,2),rate=BitConverter.ToInt32(bytes,4),align=BitConverter.ToUInt16(bytes,12),bits=BitConverter.ToUInt16(bytes,14),valid=bits;
            if(tag==0xfffe) {
                if(bytes.Length<40 || BitConverter.ToUInt16(bytes,16)<22) return null;
                valid=BitConverter.ToUInt16(bytes,18); byte[] sub=new byte[16]; Array.Copy(bytes,24,sub,0,16); Guid subtype=new Guid(sub);
                if(subtype==new Guid("00000001-0000-0010-8000-00aa00389b71")) tag=1;
                else if(subtype==new Guid("00000003-0000-0010-8000-00aa00389b71")) tag=3;
                else return null;
            }
            bool floating=tag==3;
            if((tag!=1 && tag!=3) || rate<8000 || rate>384000 || channels<1 || channels>32 || align!=channels*(bits/8) || valid<1 || valid>bits) return null;
            if(floating?(bits!=32 && bits!=64 || valid!=bits):(bits!=8 && bits!=16 && bits!=24 && bits!=32)) return null;
            return new NativeFormat { Bytes=bytes,Rate=rate,Channels=channels,Bits=bits,Valid=valid,Align=align,Float=floating };
        }
        internal static NativeFormat Create(int rate,int channels,int bits,int valid,bool floating,bool extended) {
            using(var stream=new MemoryStream()) using(var writer=new BinaryWriter(stream)) {
                int align=channels*(bits/8); writer.Write((ushort)(extended?0xfffe:floating?3:1)); writer.Write((ushort)channels); writer.Write(rate); writer.Write(rate*align); writer.Write((ushort)align); writer.Write((ushort)bits); writer.Write((ushort)(extended?22:0));
                if(extended) { writer.Write((ushort)valid); writer.Write(channels==1?4:channels==2?3:0); writer.Write(new Guid(floating?"00000003-0000-0010-8000-00aa00389b71":"00000001-0000-0010-8000-00aa00389b71").ToByteArray()); }
                return Parse(stream.ToArray());
            }
        }
    }
    static bool Supports(IClient client,NativeFormat format) {
        if(format==null) return false; IntPtr pointer=format.Allocate();
        try { int hr=client.IsFormatSupported(1,pointer,IntPtr.Zero); if(hr==0) return true; if(hr==UnsupportedFormat || hr==1) return false; Check(hr); return false; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }
    static NativeFormat SelectFormat(IClient client) {
        NativeFormat choice=NativeFormat.Create(16000,1,16,16,false,false);
        if(Supports(client,choice)) return choice;
        IntPtr mix=IntPtr.Zero; NativeFormat original=null;
        try {
            Check(client.GetMixFormat(out mix)); int size=18+Marshal.ReadInt16(mix,16);
            if(size>=18 && size<=4096) { byte[] bytes=new byte[size]; Marshal.Copy(mix,bytes,0,size); original=NativeFormat.Parse(bytes); if(Supports(client,original)) return original; }
        } finally { if(mix!=IntPtr.Zero) Marshal.FreeCoTaskMem(mix); }
        var rates=new List<int>(); if(original!=null) rates.Add(original.Rate);
        foreach(int rate in new[] {48000,44100,16000,32000,96000,8000,88200,192000}) if(!rates.Contains(rate)) rates.Add(rate);
        var channels=new List<int>(); if(original!=null) channels.Add(original.Channels);
        foreach(int count in new[] {1,2}) if(!channels.Contains(count)) channels.Add(count);
        foreach(int rate in rates) foreach(int count in channels) foreach(int bits in new[] {16,24,32}) {
            choice=NativeFormat.Create(rate,count,bits,bits,false,true); if(Supports(client,choice)) return choice;
            if(count<=2) { choice=NativeFormat.Create(rate,count,bits,bits,false,false); if(Supports(client,choice)) return choice; }
            if(bits==32) {
                choice=NativeFormat.Create(rate,count,32,24,false,true); if(Supports(client,choice)) return choice;
                choice=NativeFormat.Create(rate,count,32,32,true,true); if(Supports(client,choice)) return choice;
            }
        }
        throw new InvalidOperationException(L10n.T("messages.ExclusiveMicrophone.format"));
    }
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IManager {
        void GetAudioSessionControl(); void GetSimpleAudioVolume(); [PreserveSig] int GetSessionEnumerator(out ISessions sessions);
    }
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessions {
        [PreserveSig] int GetCount(out int count); [PreserveSig] int GetSession(int index,[MarshalAs(UnmanagedType.IUnknown)] out object session);
    }
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IControl {
        [PreserveSig] int GetState(out int state);
        void GetDisplayName(); void SetDisplayName(); void GetIconPath(); void SetIconPath(); void GetGroupingParam(); void SetGroupingParam(); void RegisterAudioSessionNotification(); void UnregisterAudioSessionNotification(); void GetSessionIdentifier(); void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
    }
    internal static uint[] ActiveCaptureProcesses(int waveDeviceId) { return CaptureProcesses(waveDeviceId,true); }
    internal static uint[] OpenCaptureProcesses(int waveDeviceId) { return CaptureProcesses(waveDeviceId,false); }
    static uint[] CaptureProcesses(int waveDeviceId,bool onlyActive) {
        IDevices devices=null; IDevice endpoint=null; object manager=null; ISessions sessions=null;
        try {
            devices=(IDevices)CoreAudioDevices.CreateEnumerator(); Check(devices.GetDevice(ResolveEndpointId(waveDeviceId),out endpoint));
            Guid iid=typeof(IManager).GUID; Check(endpoint.Activate(ref iid,23,IntPtr.Zero,out manager)); Check(((IManager)manager).GetSessionEnumerator(out sessions));
            int count; Check(sessions.GetCount(out count)); var processes=new HashSet<uint>();
            for(int i=0;i<count;i++) {
                object session=null;
                try {
                    Check(sessions.GetSession(i,out session)); var control=(IControl)session; int state; Check(control.GetState(out state));
                    if(state==2 || (onlyActive && state!=1)) continue;
                    // Windows exposes an idle cross-process system session even
                    // on unused inputs. Prove its identity; PID 0 alone is not enough.
                    int systemSession=control.IsSystemSoundsSession(); Check(systemSession);
                    if(state==0 && systemSession==0) continue;
                    uint pid; Check(control.GetProcessId(out pid)); processes.Add(pid);
                }
                finally { Release(session); }
            }
            var result=new uint[processes.Count]; processes.CopyTo(result); return result;
        } finally { Release(sessions); Release(manager); Release(endpoint); Release(devices); }
    }
}
}

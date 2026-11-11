using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minus700K {
// A dedicated waveOut stream keeps short cues independent of SoundPlayer and UI activity.
// Silent buffers keep the output active between cues, including when no game is playing.
internal sealed class CuePlayer : IDisposable {
    const int Rate=48000,Frames=960,ToneFrames=8640; // 20 ms buffers, 180 ms tones.
    const uint Done=1;
    [StructLayout(LayoutKind.Sequential,Pack=2)] struct Format { public ushort Tag,Channels; public uint Samples,Average; public ushort Align,Bits,Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public IntPtr Data; public uint Length,Recorded; public IntPtr User; public uint Flags,Loops; public IntPtr Next,Reserved; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Caps { public ushort Manufacturer,Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; public uint Formats; public ushort Channels,Reserved; public uint Support; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr wave,uint device,ref Format format,IntPtr callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveOutGetID(IntPtr wave,out uint id);
    [DllImport("winmm.dll",CharSet=CharSet.Unicode)] static extern uint waveOutGetDevCapsW(UIntPtr id,out Caps caps,uint size);
    sealed class Tone { internal int Id,Hertz,Position,Length=ToneFrames,Version; internal double Gain=1.0; internal bool Processing; }
    sealed class Buffer { internal IntPtr Header,Data; internal bool Queued,Prepared,ProcessingCompleted; internal int CompletedId,Hertz; internal byte[] Pcm=new byte[Frames*4]; }
    static readonly uint HeaderSize=(uint)Marshal.SizeOf(typeof(Header));
    readonly object gate=new object(); readonly Queue<Tone> pending=new Queue<Tone>();
    readonly AutoResetEvent done=new AutoResetEvent(false),wake=new AutoResetEvent(false);
    readonly ManualResetEvent stop=new ManualResetEvent(false),ready=new ManualResetEvent(false);
    readonly Thread worker; readonly double amplitude;
    readonly string speakerDevice;
    readonly Stopwatch clock=Stopwatch.StartNew(); readonly ProcessingCueSchedule processing=new ProcessingCueSchedule();
    int sequence,completed,processingCompleted; volatile bool disposed;
    string deviceName="",error="";
    internal string DeviceName { get { lock(gate) return deviceName; } }
    internal string OutputDeviceId { get; private set; }
    internal string Error { get { lock(gate) return error; } }
    internal int CompletedId { get { return Volatile.Read(ref completed); } }
    internal int CompletedProcessingCues { get { return Volatile.Read(ref processingCompleted); } }
    internal bool WaitReady(int milliseconds) { return ready.WaitOne(milliseconds); }
    internal CuePlayer(int volume,string speakerDevice="") {
        if(volume<0 || volume>100) throw new ArgumentOutOfRangeException("volume");
        this.speakerDevice=speakerDevice??"";
        amplitude=32767*0.25*volume/100.0;
        worker=new Thread(Run) { IsBackground=true,Name="Minus700K cue audio" }; worker.Start();
    }
    internal int Play(int hertz) {
        if(hertz<100 || hertz>4000) throw new ArgumentOutOfRangeException("hertz");
        lock(gate) {
            if(disposed) return 0;
            // Repeated busy-key presses must not build up seconds of obsolete sounds.
            if(pending.Count>=4) pending.Clear();
            int id=++sequence; pending.Enqueue(new Tone { Id=id,Hertz=hertz }); wake.Set(); return id;
        }
    }
    internal void SetProcessing(bool enabled,int intervalMilliseconds) {
        lock(gate) { if(disposed) return; processing.SetEnabled(enabled,intervalMilliseconds,clock.ElapsedMilliseconds); wake.Set(); }
    }
    internal void SetSpeaking(bool speaking) {
        lock(gate) { if(disposed) return; processing.SetSpeaking(speaking,clock.ElapsedMilliseconds); wake.Set(); }
    }
    static void Check(uint code,string operation) { if(code!=0) throw new InvalidOperationException(operation+" code="+code); }
    void Run() {
        WaitHandle[] signals={stop,done,wake};
        while(!stop.WaitOne(0)) {
            IntPtr wave=IntPtr.Zero; List<Buffer> buffers=new List<Buffer>(); Tone tone=null;
            try {
                Format f=new Format { Tag=1,Channels=2,Samples=Rate,Average=Rate*4,Align=4,Bits=16 };
                uint chosen=SpeakerDevices.WaveDevice(speakerDevice);
                Check(waveOutOpen(out wave,chosen,ref f,done.SafeWaitHandle.DangerousGetHandle(),IntPtr.Zero,0x50000),"waveOutOpen");
                uint device; Caps caps; string name="Windows default output";
                if(waveOutGetID(wave,out device)==0 && waveOutGetDevCapsW((UIntPtr)device,out caps,(uint)Marshal.SizeOf(typeof(Caps)))==0) name=caps.Name;
                OutputDeviceId=SpeakerDevices.EndpointForWaveDevice(device);
                lock(gate) { deviceName=name; error=""; }
                for(int i=0;i<3;i++) {
                    Buffer b=new Buffer(); buffers.Add(b); b.Data=Marshal.AllocHGlobal(b.Pcm.Length); b.Header=Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new Header { Data=b.Data,Length=(uint)b.Pcm.Length },b.Header,false);
                    Check(waveOutPrepareHeader(wave,b.Header,HeaderSize),"waveOutPrepareHeader"); b.Prepared=true;
                }
                // Submit silence before signalling ready, so the output is already running.
                foreach(Buffer b in buffers) Write(wave,b,ref tone,false);
                ready.Set(); ThreadPool.QueueUserWorkItem(delegate { Log.Write("cue output ready: "+name+"; 48000 Hz stereo, 180 ms tones"); });
                while(!stop.WaitOne(0)) {
                    if(WaitHandle.WaitAny(signals,250)==0) break;
                    foreach(Buffer b in buffers) {
                        Header h=(Header)Marshal.PtrToStructure(b.Header,typeof(Header));
                        if(b.Queued && (h.Flags&Done)==0) continue;
                        if(b.CompletedId>0) {
                            Volatile.Write(ref completed,b.CompletedId);
                            int id=b.CompletedId,hz=b.Hertz;
                            // The install folder can be on SMB: never log from the render loop.
                            ThreadPool.QueueUserWorkItem(delegate { Log.Write("cue played id="+id+" hz="+hz); });
                            b.CompletedId=0;
                        }
                        if(b.ProcessingCompleted) {
                            Interlocked.Increment(ref processingCompleted); b.ProcessingCompleted=false;
                            ThreadPool.QueueUserWorkItem(delegate { Log.Write("processing cue played"); });
                        }
                        Write(wave,b,ref tone,true);
                    }
                }
            } catch(Exception e) {
                lock(gate) error=e.Message;
                ready.Reset(); Log.Write("cue output failed: "+e.Message);
            } finally {
                if(wave!=IntPtr.Zero) waveOutReset(wave);
                foreach(Buffer b in buffers) {
                    if(b.Prepared && waveOutUnprepareHeader(wave,b.Header,HeaderSize)!=0) continue; // Never free a driver-owned buffer.
                    if(b.Header!=IntPtr.Zero) Marshal.FreeHGlobal(b.Header);
                    if(b.Data!=IntPtr.Zero) Marshal.FreeHGlobal(b.Data);
                }
                if(wave!=IntPtr.Zero) waveOutClose(wave);
            }
            if(!stop.WaitOne(1000)) { lock(gate) pending.Clear(); }
        }
    }
    void Write(IntPtr wave,Buffer b,ref Tone tone,bool allowTone) {
        Array.Clear(b.Pcm,0,b.Pcm.Length);
        lock(gate) {
            if(tone!=null && tone.Processing && (!processing.CanPlay || tone.Version!=processing.Version || pending.Count>0)) tone=null;
            if(allowTone && tone==null) {
                if(pending.Count>0) tone=pending.Dequeue();
                else if(processing.TryStart(clock.ElapsedMilliseconds)) tone=new Tone { Hertz=294,Length=Rate*320/1000,Gain=0.38,Processing=true,Version=processing.Version };
            }
        }
        if(tone!=null) {
            for(int i=0;i<Frames && tone.Position<tone.Length;i++,tone.Position++) {
                double envelope=Math.Min(1.0,Math.Min(tone.Position/480.0,(tone.Length-1-tone.Position)/480.0));
                double phase=2*Math.PI*tone.Hertz*tone.Position/Rate,waveform=Math.Sin(phase);
                if(tone.Processing) {
                    // A low, rounded chime: soft 45 ms attack, gently decaying body,
                    // and a smooth 120 ms release instead of a short electronic beep.
                    double attack=Math.Min(1.0,tone.Position/(Rate*0.045));
                    double release=Math.Min(1.0,(tone.Length-1-tone.Position)/(Rate*0.120));
                    envelope=(0.5-0.5*Math.Cos(Math.PI*attack))*(0.5-0.5*Math.Cos(Math.PI*release))*Math.Exp(-3.0*tone.Position/(tone.Length-1));
                    waveform=0.94*Math.Sin(phase)+0.06*Math.Sin(2*phase);
                }
                short sample=(short)(waveform*amplitude*tone.Gain*envelope);
                int offset=i*4; b.Pcm[offset]=b.Pcm[offset+2]=(byte)sample; b.Pcm[offset+1]=b.Pcm[offset+3]=(byte)(sample>>8);
            }
            if(tone.Position==tone.Length) { b.CompletedId=tone.Id; b.Hertz=tone.Hertz; b.ProcessingCompleted=tone.Processing; tone=null; }
        }
        Marshal.Copy(b.Pcm,0,b.Data,b.Pcm.Length);
        Check(waveOutWrite(wave,b.Header,HeaderSize),"waveOutWrite"); b.Queued=true;
    }
    public void Dispose() {
        lock(gate) { if(disposed) return; disposed=true; stop.Set(); }
        if(worker.Join(3000)) { ready.Dispose(); stop.Dispose(); done.Dispose(); wake.Dispose(); }
    }
}
}

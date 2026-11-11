using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Minus700K {
internal sealed class WaveRecorder : IDisposable {
    [StructLayout(LayoutKind.Sequential,Pack=2)] struct Format { public ushort Tag,Channels; public uint Samples,Average; public ushort Align,Bits,Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header { public IntPtr Data; public uint Length,Recorded; public IntPtr User; public uint Flags,Loops; public IntPtr Next,Reserved; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Caps { public ushort Manufacturer,Product; public uint Version; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; public uint Formats; public ushort Channels,Reserved; }
    delegate void Callback(IntPtr wave,uint message,IntPtr instance,IntPtr header,IntPtr extra);
    [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr wave,uint id,ref Format format,Callback callback,IntPtr instance,uint flags);
    [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr wave,IntPtr header,uint size);
    [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll",CharSet=CharSet.Unicode)] static extern uint waveInGetDevCapsW(UIntPtr id,out Caps caps,uint size);
    IntPtr wave; readonly Callback callback; readonly List<IntPtr> headers=new List<IntPtr>();
    readonly object gate=new object(); readonly MemoryStream pcm=new MemoryStream();
    volatile bool running; readonly ManualResetEvent allReturned=new ManualResetEvent(true); int pending;
    static readonly uint HeaderSize=(uint)Marshal.SizeOf(typeof(Header));
    internal static string[] Devices() {
        List<string> list=new List<string>();
        for(uint i=0;i<waveInGetNumDevs();i++) { Caps c; if(waveInGetDevCapsW((UIntPtr)i,out c,(uint)Marshal.SizeOf(typeof(Caps)))==0) list.Add(i+": "+c.Name); }
        return list.ToArray();
    }
    internal WaveRecorder(int id) {
        callback=OnData; Format f=new Format { Tag=1,Channels=1,Samples=16000,Average=32000,Align=2,Bits=16 };
        Check(waveInOpen(out wave,unchecked((uint)id),ref f,callback,IntPtr.Zero,0x30000));
        try {
            for(int i=0;i<6;i++) { Header h=new Header { Data=Marshal.AllocHGlobal(1600),Length=1600 }; IntPtr p=Marshal.AllocHGlobal((int)HeaderSize); Marshal.StructureToPtr(h,p,false); headers.Add(p); Check(waveInPrepareHeader(wave,p,HeaderSize)); }
        } catch { Dispose(); throw; }
    }
    static void Check(uint code) { if(code!=0) throw new InvalidOperationException(L10n.F("messages.Audio.001",code)); }
    internal void Start() {
        lock(gate) { pcm.SetLength(0); running=true; pending=0; allReturned.Reset(); }
        try { foreach(IntPtr p in headers) { Interlocked.Increment(ref pending); uint r=waveInAddBuffer(wave,p,HeaderSize); if(r!=0) Interlocked.Decrement(ref pending); Check(r); } Check(waveInStart(wave)); }
        catch { Stop(); throw; }
    }
    void OnData(IntPtr handle,uint msg,IntPtr instance,IntPtr ptr,IntPtr extra) {
        if(msg!=0x3C0) return;
        try {
            Header h=(Header)Marshal.PtrToStructure(ptr,typeof(Header));
            lock(gate) {
                if(h.Recorded>0) { byte[] bytes=new byte[h.Recorded]; Marshal.Copy(h.Data,bytes,0,bytes.Length); pcm.Write(bytes,0,bytes.Length); }
                if(running && waveInAddBuffer(handle,ptr,HeaderSize)==0) return;
            }
        } catch { running=false; }
        if(Interlocked.Decrement(ref pending)==0) allReturned.Set();
    }
    internal byte[] Stop() {
        lock(gate) running=false;
        if(wave!=IntPtr.Zero) waveInReset(wave);
        if(pending>0 && !allReturned.WaitOne(2000)) throw new InvalidOperationException(L10n.T("messages.Audio.002"));
        lock(gate) return pcm.ToArray();
    }
    internal static void Save(string path,byte[] data) {
        using(BinaryWriter w=new BinaryWriter(File.Create(path))) { w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36+data.Length); w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((ushort)1); w.Write((ushort)1); w.Write(16000); w.Write(32000); w.Write((ushort)2); w.Write((ushort)16); w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data.Length); w.Write(data); }
    }
    internal static bool Audible(byte[] data) {
        double squares=0; int peak=0;
        for(int i=0;i+1<data.Length;i+=2) { int s=(short)(data[i]|(data[i+1]<<8)); squares+=(double)s*s; peak=Math.Max(peak,Math.Abs(s)); }
        return data.Length>=12800 && peak>=120 && Math.Sqrt(squares/Math.Max(1,data.Length/2))>=25;
    }
    public void Dispose() {
        if(wave==IntPtr.Zero) return;
        try { Stop(); } catch { }
        foreach(IntPtr p in headers) { waveInUnprepareHeader(wave,p,HeaderSize); Header h=(Header)Marshal.PtrToStructure(p,typeof(Header)); Marshal.FreeHGlobal(h.Data); Marshal.FreeHGlobal(p); }
        headers.Clear(); waveInClose(wave); wave=IntPtr.Zero; allReturned.Dispose(); pcm.Dispose(); GC.KeepAlive(callback);
    }
}
}

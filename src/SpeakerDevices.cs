using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Minus700K {
internal sealed class SpeakerDevice {
    internal string Id,Name,TokenId;
}
// Match SAPI outputs to WinMM endpoints by persistent ID, never by a truncated
// device name or a device number whose order can change after reconnecting USB.
internal static class SpeakerDevices {
    [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll")] static extern uint waveOutMessage(IntPtr device,uint message,IntPtr first,IntPtr second);
    internal static string EndpointForWaveDevice(uint device) {
        IntPtr size=Marshal.AllocHGlobal(4),buffer=IntPtr.Zero;
        try {
            Marshal.WriteInt32(size,0);
            if(waveOutMessage(new IntPtr(device),0x0812,size,IntPtr.Zero)!=0) return "";
            int bytes=Marshal.ReadInt32(size); if(bytes<2 || bytes>65536) return "";
            buffer=Marshal.AllocHGlobal(bytes);
            if(waveOutMessage(new IntPtr(device),0x0811,buffer,new IntPtr(bytes))!=0) return "";
            return Marshal.PtrToStringUni(buffer)??"";
        } finally { if(buffer!=IntPtr.Zero) Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(size); }
    }
    internal static uint WaveDevice(string endpoint) {
        if(string.IsNullOrEmpty(endpoint)) return uint.MaxValue;
        for(uint i=0;i<waveOutGetNumDevs();i++) if(string.Equals(EndpointForWaveDevice(i),endpoint,StringComparison.OrdinalIgnoreCase)) return i;
        return uint.MaxValue;
    }
    internal static List<SpeakerDevice> List() {
        var result=new List<SpeakerDevice>(); var endpoints=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(uint i=0;i<waveOutGetNumDevs();i++) { string id=EndpointForWaveDevice(i); if(id.Length>0) endpoints.Add(id); }
        object voice=null,tokens=null;
        try {
            voice=ComSpeech.Create(); tokens=ComSpeech.Call(voice,"GetAudioOutputs","","");
            int count=Convert.ToInt32(ComSpeech.Get(tokens,"Count"));
            for(int i=0;i<count;i++) {
                object token=ComSpeech.Call(tokens,"Item",i);
                try {
                    string tokenId=(string)ComSpeech.Get(token,"Id"); string id=tokenId.Substring(tokenId.LastIndexOf('\\')+1);
                    if(endpoints.Contains(id)) result.Add(new SpeakerDevice { Id=id,Name=(string)ComSpeech.Call(token,"GetDescription",0),TokenId=tokenId });
                } finally { ComSpeech.Release(token); }
            }
        } finally { ComSpeech.Release(tokens); ComSpeech.Release(voice); }
        return result;
    }
    internal static SpeakerDevice Resolve(string id) {
        if(string.IsNullOrEmpty(id)) return null;
        try { var device=List().FirstOrDefault(d=>d.Id.Equals(id,StringComparison.OrdinalIgnoreCase)); if(device!=null) return device; }
        catch(Exception e) { Log.Write("speaker enumeration failed: "+e.GetType().Name); }
        Log.Write("configured speaker unavailable; using Windows audio settings"); return null;
    }
}
// Public SAPI Automation API. No Windows mixer preferences or global default
// devices are modified by selecting an output for this voice instance.
internal static class ComSpeech {
    internal static object Create() { return Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice",true)); }
    static object Invoke(object target,string name,BindingFlags flags,object[] arguments) {
        try { return target.GetType().InvokeMember(name,flags,null,target,arguments); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    internal static object Call(object target,string name,params object[] arguments) { return Invoke(target,name,BindingFlags.InvokeMethod,arguments); }
    internal static object Get(object target,string name) { return Invoke(target,name,BindingFlags.GetProperty,null); }
    internal static void Set(object target,string name,object value) { Invoke(target,name,BindingFlags.SetProperty,new[]{value}); }
    internal static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
}

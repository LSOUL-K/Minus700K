using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minus700K {
// A tray application has no titled main window for Windows' mixer to use.
// Name and identify our sessions explicitly, including sessions created by SAPI or rerouting.
// Never change their volume, mute state, grouping, or selected output device.
internal sealed class AudioSessionIdentity : IDisposable {
    [ComImport,Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class EnumeratorClass { }
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevices {
        [PreserveSig] int EnumAudioEndpoints(int flow,uint mask,out IDevicesList devices);
    }
    [ComImport,Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevicesList {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index,out IDevice device);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IDevice {
        [PreserveSig] int Activate(ref Guid iid,uint context,IntPtr parameters,[MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport,Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IManager {
        void GetAudioSessionControl(); void GetSimpleAudioVolume();
        [PreserveSig] int GetSessionEnumerator(out ISessions sessions);
    }
    [ComImport,Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface ISessions {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index,out IControl control);
    }
    [ComImport,Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IControl {
        void GetState();
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name,IntPtr context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path,IntPtr context);
        void GetGroupingParam(); void SetGroupingParam();
        void RegisterAudioSessionNotification(); void UnregisterAudioSessionNotification();
        void GetSessionIdentifier(); void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid);
    }
    readonly uint processId=(uint)Process.GetCurrentProcess().Id;
    readonly string iconPath=typeof(AudioSessionIdentity).Assembly.Location+",0";
    readonly object gate=new object(); readonly Thread worker; bool stopped;
    internal AudioSessionIdentity() {
        worker=new Thread(Run) { IsBackground=true,Name="Minus700K mixer identity" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    static void Check(int hr) { Marshal.ThrowExceptionForHR(hr); }
    static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    void Run() {
        string lastError="";
        while(true) {
            lock(gate) if(stopped) return;
            try { Refresh(); lastError=""; }
            catch(Exception e) { if(lastError!=e.Message) { lastError=e.Message; Log.Write("mixer identity: "+e.Message); } }
            lock(gate) { if(stopped) return; Monitor.Wait(gate,1000); }
        }
    }
    void Refresh() {
        IDevices enumerator=null; IDevicesList devices=null;
        try {
            enumerator=(IDevices)new EnumeratorClass(); Check(enumerator.EnumAudioEndpoints(0,1,out devices));
            uint count; Check(devices.GetCount(out count));
            for(uint i=0;i<count;i++) {
                IDevice device=null; object manager=null; ISessions sessions=null;
                try {
                    Check(devices.Item(i,out device)); Guid iid=typeof(IManager).GUID;
                    Check(device.Activate(ref iid,23,IntPtr.Zero,out manager)); Check(((IManager)manager).GetSessionEnumerator(out sessions));
                    int sessionCount; Check(sessions.GetCount(out sessionCount));
                    for(int n=0;n<sessionCount;n++) {
                        IControl control=null;
                        try {
                            Check(sessions.GetSession(n,out control)); uint pid; Check(control.GetProcessId(out pid));
                            if(pid!=processId) continue;
                            string name; Check(control.GetDisplayName(out name));
                            if(name!="Minus700K") Check(control.SetDisplayName("Minus700K",IntPtr.Zero));
                            string currentIcon; Check(control.GetIconPath(out currentIcon));
                            if(!string.Equals(currentIcon,iconPath,StringComparison.OrdinalIgnoreCase)) Check(control.SetIconPath(iconPath,IntPtr.Zero));
                        } finally { Release(control); }
                    }
                } catch(COMException) { /* An endpoint may disappear while it is being enumerated. Retry next tick. */ }
                finally { Release(sessions); Release(manager); Release(device); }
            }
        } finally { Release(devices); Release(enumerator); }
    }
    public void Dispose() { lock(gate) { stopped=true; Monitor.PulseAll(gate); } worker.Join(2000); }
}
}

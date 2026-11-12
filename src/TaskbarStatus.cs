using System;
using System.Runtime.InteropServices;

namespace Minus700K {
// Windows 11's grouped taskbar keeps the application's launch icon even when
// WM_SETICON changes a window's icon. Use the shell's status overlay as well.
internal sealed class TaskbarStatus : IDisposable {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    internal static readonly int ButtonCreatedMessage=unchecked((int)RegisterWindowMessage("TaskbarButtonCreated"));
    static readonly Guid TaskbarClass=new Guid("56FDF344-FD6D-11D0-958A-006097C9A090");
    [ComImport,Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITaskbarList3 {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr window);
        [PreserveSig] int DeleteTab(IntPtr window);
        [PreserveSig] int ActivateTab(IntPtr window);
        [PreserveSig] int SetActiveAlt(IntPtr window);
        [PreserveSig] int MarkFullscreenWindow(IntPtr window,[MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        [PreserveSig] int SetProgressValue(IntPtr window,ulong completed,ulong total);
        [PreserveSig] int SetProgressState(IntPtr window,uint state);
        [PreserveSig] int RegisterTab(IntPtr tab,IntPtr owner);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab,IntPtr before);
        [PreserveSig] int SetTabActive(IntPtr tab,IntPtr owner,uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr window,uint count,IntPtr buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr window,uint count,IntPtr buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr window,IntPtr images);
        [PreserveSig] int SetOverlayIcon(IntPtr window,IntPtr icon,[MarshalAs(UnmanagedType.LPWStr)] string description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr window,[MarshalAs(UnmanagedType.LPWStr)] string tip);
        [PreserveSig] int SetThumbnailClip(IntPtr window,IntPtr clip);
    }
    readonly TrayIcons icons=new TrayIcons();
    ITaskbarList3 taskbar; IntPtr window; string state="ready",lastError=""; bool disposed;
    internal bool IsButtonReady { get; private set; }
    internal int LastResult { get; private set; }
    internal int AppliedCount { get; private set; }
    internal void HandleCreated(IntPtr handle) { window=handle; IsButtonReady=false; }
    internal void HandleDestroyed() { window=IntPtr.Zero; IsButtonReady=false; ReleaseTaskbar(); }
    internal void ButtonCreated(IntPtr handle) {
        window=handle; IsButtonReady=true;
        // Explorer may have restarted. Recreate the interface and restore the
        // current state only after its replacement taskbar button exists.
        ReleaseTaskbar(); Apply();
    }
    internal void SetState(string value) { state=value; Apply(); }
    void Apply() {
        if(disposed || !IsButtonReady || window==IntPtr.Zero) return;
        try {
            if(taskbar==null) {
                taskbar=(ITaskbarList3)Activator.CreateInstance(Type.GetTypeFromCLSID(TaskbarClass));
                Marshal.ThrowExceptionForHR(taskbar.HrInit());
            }
            bool active=state=="recording" || state=="recognizing" || state=="asking" || state=="speaking" || state=="paused";
            string key=state=="recording"?"027":state=="recognizing"?"028":state=="asking"?"029":state=="speaking"?"030":state=="paused"?"009":"026";
            LastResult=taskbar.SetOverlayIcon(window,active?icons.ForState(state).Handle:IntPtr.Zero,L10n.T("messages.Program."+key));
            Marshal.ThrowExceptionForHR(LastResult); AppliedCount++; lastError="";
        } catch(Exception error) {
            LastResult=error.HResult;
            string message=error.GetType().Name+": "+error.Message;
            if(lastError!=message) { lastError=message; Log.Write("taskbar status: "+message); }
            ReleaseTaskbar();
        }
    }
    void ReleaseTaskbar() {
        var value=taskbar; taskbar=null;
        if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
    public void Dispose() {
        if(disposed) return;
        disposed=true; HandleDestroyed(); icons.Dispose();
    }
}
}

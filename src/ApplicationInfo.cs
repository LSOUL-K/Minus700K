using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Minus700K {
internal sealed class ApplicationInfo {
    public string windowTitle="",executableName="",productName="",fileDescription="";
    public uint processId;
    public string detectionNote="";
    // Internal identity stays local; only the basename and descriptive metadata go to Codex.
    internal long WindowHandle;
    internal string ExecutablePath="";
    [DllImport("user32.dll",SetLastError=true)] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    internal static ApplicationInfo Current() {
        IntPtr window=Native.GetForegroundWindow();
        if(window==IntPtr.Zero) throw new InvalidOperationException(L10n.T("messages.Capture.001"));
        var title=new StringBuilder(2048); Native.GetWindowText(window,title,title.Capacity);
        return FromWindow(window,title.ToString());
    }
    internal static ApplicationInfo FromWindow(IntPtr window,string title) {
        ApplicationInfo info=new ApplicationInfo { WindowHandle=window.ToInt64(),windowTitle=title??"" };
        GetWindowThreadProcessId(window,out info.processId);
        info.ExecutablePath=ImagePath(info.processId);
        if(info.ExecutablePath.Length>0) info.executableName=Path.GetFileName(info.ExecutablePath);
        else info.detectionNote=L10n.T("messages.application.executableUnavailable");
        return info;
    }
    internal static string ImagePath(uint pid) {
        if(pid==0) return "";
        IntPtr process=OpenProcess(0x1000,false,pid); // PROCESS_QUERY_LIMITED_INFORMATION, no mutation/injection.
        if(process==IntPtr.Zero) return "";
        try { StringBuilder name=new StringBuilder(32768); uint length=(uint)name.Capacity; return QueryFullProcessImageName(process,0,name,ref length)?name.ToString():""; }
        finally { CloseHandle(process); }
    }
    // File-version resource reads can hit disk/SMB, so call only on the processing worker.
    internal void ResolveDescriptions() {
        if(ExecutablePath.Length==0) return;
        try { FileVersionInfo version=FileVersionInfo.GetVersionInfo(ExecutablePath); productName=(version.ProductName??"").Trim(); fileDescription=(version.FileDescription??"").Trim(); }
        catch(Exception) { detectionNote=L10n.T("messages.application.productUnavailable"); }
    }
    internal string ContextKey { get { return WindowHandle.ToString(CultureInfo.InvariantCulture)+":"+processId.ToString(CultureInfo.InvariantCulture)+":"+ExecutablePath.ToUpperInvariant(); } }
}
}

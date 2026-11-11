using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
internal static class HiddenDesktopLauncher {
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Startup {
        public int cb; public string reserved,desktop,title; public uint x,y,width,height,xChars,yChars,fill,flags; public short show,reservedSize; public IntPtr reservedPtr,input,output,error;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr process,thread; public uint pid,tid; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,IntPtr security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string cwd,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static string Quote(string value) { if(value.IndexOf('"')>=0) throw new ArgumentException("Unexpected quote in path"); return "\""+value+"\""; }
    static int Main(string[] args) {
        if(args.Length!=4) return 2;
        string name="Minus700K_SettingsTest_"+Guid.NewGuid().ToString("N");
        IntPtr desktop=CreateDesktop(name,IntPtr.Zero,IntPtr.Zero,0,0x10000000,IntPtr.Zero);
        if(desktop==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        ProcessInfo child=new ProcessInfo(); IntPtr initial=GetForegroundWindow(); int changes=0;
        try {
            var startup=new Startup { cb=Marshal.SizeOf(typeof(Startup)),desktop="WinSta0\\"+name,flags=1,show=0 };
            var command=new StringBuilder(Quote(args[0])+" "+Quote(args[1])+" "+Quote(args[2])+" "+Quote(args[3]));
            if(!CreateProcess(args[0],command,IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,Path.GetDirectoryName(args[0]),ref startup,out child)) throw new Win32Exception(Marshal.GetLastWin32Error());
            int elapsed=0;
            while(WaitForSingleObject(child.process,10)==258) {
                if(GetForegroundWindow()!=initial) changes++;
                if((elapsed+=10)>=20000) { TerminateProcess(child.process,9); WaitForSingleObject(child.process,5000); throw new TimeoutException("Only the diagnostic child was terminated."); }
            }
            uint code; GetExitCodeProcess(child.process,out code);
            Console.WriteLine("childExit="+code+" activeDesktopForegroundMismatchSamples="+changes);
            return (int)code;
        } finally { if(child.thread!=IntPtr.Zero) CloseHandle(child.thread); if(child.process!=IntPtr.Zero) CloseHandle(child.process); CloseDesktop(desktop); }
    }
}

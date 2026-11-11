using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Minus700K {
// Only the focused shortcut editor owns this short-lived observer. It sees
// keys before registered global shortcuts consume normal window messages,
// and always passes the input on to the other application.
internal sealed class ShortcutCapture : IDisposable {
    delegate IntPtr KeyboardProc(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {
        internal uint Key,Scan,Flags,Time;
        internal UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookExW(int kind,KeyboardProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    readonly KeyboardProc callback;
    readonly Func<bool> canCapture;
    readonly Action<Keys> captured;
    readonly bool[] down=new bool[256];
    IntPtr hook;
    bool disposed;
    internal bool Installed { get { return hook!=IntPtr.Zero; } }
    internal ShortcutCapture(Func<bool> canCapture,Action<Keys> captured) {
        this.canCapture=canCapture; this.captured=captured;
        // Ignore primary keys already held when the editor receives focus.
        for(int key=8;key<255;key++) down[key]=Native.Down(key);
        callback=ObserveNative;
        hook=SetWindowsHookExW(13,callback,GetModuleHandle(null),0); // WH_KEYBOARD_LL
        if(hook==IntPtr.Zero) Log.Write("shortcut input observer unavailable: "+Marshal.GetLastWin32Error());
    }
    IntPtr ObserveNative(int code,IntPtr message,IntPtr data) {
        if(code==0 && !disposed) {
            try {
                var input=(KeyboardInput)Marshal.PtrToStructure(data,typeof(KeyboardInput));
                Observe(message.ToInt32(),(int)input.Key);
            } catch { /* No managed exception may escape a native hook. */ }
        }
        return CallNextHookEx(hook,code,message,data);
    }
    internal void Observe(int message,int key) {
        if(disposed || key<8 || key>=255) return;
        bool pressed=message==0x0100 || message==0x0104;
        if(!pressed && message!=0x0101 && message!=0x0105) return;
        bool repeated=down[key]; down[key]=pressed;
        // A low-level callback runs before GetAsyncKeyState changes. Track
        // modifier events ourselves, including independent left/right keys.
        if(key==160 || key==161) down[16]=false;
        if(key==162 || key==163) down[17]=false;
        if(key==164 || key==165) down[18]=false;
        if(!pressed || repeated || !canCapture()) return;
        if(key==16 || key==17 || key==18 || (key>=160 && key<=165) || key==91 || key==92) return;
        // Windows-key combinations are not part of the app's shortcut format.
        if(down[91] || down[92]) return;
        Keys value=(Keys)key;
        if(down[17] || down[162] || down[163]) value|=Keys.Control;
        if(down[18] || down[164] || down[165]) value|=Keys.Alt;
        if(down[16] || down[160] || down[161]) value|=Keys.Shift;
        captured(value);
    }
    public void Dispose() {
        if(disposed) return; disposed=true;
        if(hook!=IntPtr.Zero) { UnhookWindowsHookEx(hook); hook=IntPtr.Zero; }
        GC.KeepAlive(callback);
    }
}
}

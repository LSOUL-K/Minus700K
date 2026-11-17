using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Minus700K {
internal static class Native {
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool RegisterHotKey(IntPtr hwnd,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd,int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out Rect rect, int size);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } } }
    internal static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }
}
internal sealed class CaptureTarget {
    internal IntPtr Window, Monitor;
    internal Rectangle Bounds;
    internal string BoundsSource;
    internal ApplicationInfo App;
    internal string Title { get { return App.windowTitle; } }
    internal static CaptureTarget Current() { ApplicationInfo app; return Current(out app); }
    internal static CaptureTarget Current(out ApplicationInfo app) {
        app=new ApplicationInfo();
        IntPtr hwnd=Native.GetForegroundWindow();
        if(hwnd==IntPtr.Zero) throw new InvalidOperationException(L10n.T("messages.Capture.001"));
        StringBuilder text=new StringBuilder(2048); Native.GetWindowText(hwnd,text,text.Capacity);
        app=ApplicationInfo.FromWindow(hwnd,text.ToString());
        // Include native title bars and menus, which are useful context for ordinary apps.
        // DWM excludes invisible resize borders. All coordinates are physical pixels (PerMonitorV2).
        Native.Rect r; string source="dwm_window";
        if(Native.DwmGetWindowAttribute(hwnd,9,out r,Marshal.SizeOf(typeof(Native.Rect)))!=0 || r.Right<=r.Left || r.Bottom<=r.Top) {
            source="window";
            if(!Native.GetWindowRect(hwnd,out r) || r.Right<=r.Left || r.Bottom<=r.Top) {
                source="client_fallback"; Point p=new Point();
                if(!Native.GetClientRect(hwnd,out r) || !Native.ClientToScreen(hwnd,ref p)) throw new InvalidOperationException(L10n.T("messages.Capture.002"));
                r.Right+=p.X; r.Bottom+=p.Y; r.Left=p.X; r.Top=p.Y;
            }
        }
        return new CaptureTarget { Window=hwnd, Monitor=Native.MonitorFromWindow(hwnd,2), Bounds=r.Rectangle, BoundsSource=source, App=app };
    }
}
// DXGI Desktop Duplication: no window, picker, clipboard, focus change, or game hook.
// Vtable slots follow the Windows SDK ABI (IUnknown / DXGI / D3D11).
internal sealed class DesktopCapture : IDisposable {
    IntPtr device,context,duplication,staging,monitor,lastFrameWindow;
    OutputDesc output; uint textureWidth,textureHeight; Bitmap lastFrame;
    readonly object gate=new object();
    internal long LastPresentTime { get; private set; }
    internal int SkippedPointerOnlyFrames { get; private set; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct OutputDesc {
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name;
        public Native.Rect Bounds; public int Attached; public uint Rotation; public IntPtr Monitor;
    }
    [StructLayout(LayoutKind.Sequential)] struct TextureDesc { public uint Width,Height,MipLevels,ArraySize,Format,SampleCount,SampleQuality,Usage,BindFlags,CpuAccessFlags,MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch,DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] struct FrameInfo { public long LastPresent,LastMouse; public uint Frames; public int Coalesced,Protected; public Point Pointer; public int PointerVisible; public uint MetadataSize,PointerSize; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumItem(IntPtr self,uint index,out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesc(IntPtr self,out OutputDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Duplicate(IntPtr self,IntPtr dev,out IntPtr duplicate);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Acquire(IntPtr self,uint timeout,out FrameInfo info,out IntPtr resource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFrame(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void GetTextureDesc(IntPtr self,out TextureDesc desc);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture(IntPtr self,ref TextureDesc desc,IntPtr initial,out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResource(IntPtr self,IntPtr dest,IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Map(IntPtr self,IntPtr resource,uint subresource,uint map,uint flags,out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Unmap(IntPtr self,IntPtr resource,uint subresource);
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter,uint driver,IntPtr software,uint flags,IntPtr levels,uint count,uint sdk,out IntPtr dev,out uint level,out IntPtr ctx);
    [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] static extern void CopyMemory(IntPtr dest,IntPtr src,UIntPtr length);
    static T V<T>(IntPtr obj,int slot) where T:class { return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size),typeof(T)) as T; }
    static void Check(int hr,string operation) {
        if(hr<0) { Log.Write("capture api="+operation+" hresult=0x"+hr.ToString("X8")); Marshal.ThrowExceptionForHR(hr); }
    }
    static void Release(ref IntPtr p) { if(p!=IntPtr.Zero) { Marshal.Release(p); p=IntPtr.Zero; } }
    void Initialize(IntPtr wanted) {
        Clear(); IntPtr factory=IntPtr.Zero,adapter=IntPtr.Zero,outp=IntPtr.Zero,out1=IntPtr.Zero;
        try {
            Guid iid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); Check(CreateDXGIFactory1(ref iid,out factory),"CreateDXGIFactory1");
            for(uint a=0;V<EnumItem>(factory,12)(factory,a,out adapter)>=0;a++) {
                for(uint o=0;V<EnumItem>(adapter,7)(adapter,o,out outp)>=0;o++) {
                    OutputDesc d; Check(V<GetDesc>(outp,7)(outp,out d),"GetOutputDesc");
                    if(d.Attached!=0 && d.Monitor==wanted) {
                        uint level; Check(D3D11CreateDevice(adapter,0,IntPtr.Zero,0,IntPtr.Zero,0,7,out device,out level,out context),"D3D11CreateDevice");
                        iid=new Guid("00cddea8-939b-4b83-a340-a685226666cc"); Check(Marshal.QueryInterface(outp,ref iid,out out1),"QueryOutput1");
                        Check(V<Duplicate>(out1,22)(out1,device,out duplication),"DuplicateOutput"); output=d; monitor=wanted; return;
                    }
                    Release(ref outp);
                }
                Release(ref adapter);
            }
            throw new InvalidOperationException(L10n.T("messages.Capture.003"));
        } catch { Clear(); throw; }
        finally { Release(ref out1); Release(ref outp); Release(ref adapter); Release(ref factory); }
    }
    internal Bitmap Capture(CaptureTarget target,int maxWidth) { return Capture(target,maxWidth,CancellationToken.None); }
    internal Bitmap Capture(CaptureTarget target,int maxWidth,CancellationToken token) {
        lock(gate) {
            return CaptureRecovery.Run(delegate(int remaining) {
                Stopwatch attempt=Stopwatch.StartNew();
                if(duplication==IntPtr.Zero || monitor!=target.Monitor) Initialize(target.Monitor);
                token.ThrowIfCancellationRequested(); ValidateTarget(target.Window);
                int frameBudget=Math.Min(600,Math.Max(1,remaining-(int)attempt.ElapsedMilliseconds));
                using(Bitmap full=ReadFrame(target.Window,frameBudget,token)) {
                    if(output.Rotation==2) full.RotateFlip(RotateFlipType.Rotate90FlipNone);
                    if(output.Rotation==3) full.RotateFlip(RotateFlipType.Rotate180FlipNone);
                    if(output.Rotation==4) full.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    Rectangle screen=output.Bounds.Rectangle;
                    Rectangle crop=Rectangle.Intersect(target.Bounds,screen);
                    if(crop.Width<8 || crop.Height<8) throw new InvalidOperationException(L10n.T("messages.Capture.004"));
                    crop.Offset(-screen.Left,-screen.Top);
                    int w=Math.Min(maxWidth,crop.Width),h=Math.Max(1,(int)((long)crop.Height*w/crop.Width));
                    Bitmap result=new Bitmap(w,h,PixelFormat.Format32bppArgb);
                    try {
                        using(Graphics g=Graphics.FromImage(result)) { g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(full,new Rectangle(0,0,w,h),crop,GraphicsUnit.Pixel); }
                        token.ThrowIfCancellationRequested(); ValidateTarget(target.Window); ValidateFrame(result);
                        Bitmap accepted=result; result=null; return accepted;
                    } finally { if(result!=null) result.Dispose(); }
                }
            },Clear,delegate { ValidateTarget(target.Window); },token);
        }
    }
    static void ValidateTarget(IntPtr expectedWindow) {
        if(Native.GetForegroundWindow()!=expectedWindow) throw new InvalidOperationException(L10n.T("messages.Capture.006"));
    }
    internal void ValidateFrame(Bitmap image) {
        if(OptionalScreenshot.IsEntirelyBlack(image)) {
            DiscardLastFrame();
            throw new CaptureFrameUnavailableException(L10n.T("messages.OptionalScreenshot.002"));
        }
    }
    Bitmap CachedFrame(IntPtr expectedWindow,string reason) {
        if(lastFrame==null || lastFrameWindow!=expectedWindow) return null;
        Log.Write("capture cached_frame reason="+reason);
        return (Bitmap)lastFrame.Clone();
    }
    Bitmap ReadFrame(IntPtr expectedWindow,int budgetMilliseconds,CancellationToken token) {
        Stopwatch wait=Stopwatch.StartNew();
        while(wait.ElapsedMilliseconds<budgetMilliseconds) {
            token.ThrowIfCancellationRequested(); ValidateTarget(expectedWindow);
            IntPtr resource=IntPtr.Zero,texture=IntPtr.Zero; bool acquired=false,mapped=false;
            try {
                uint timeoutMilliseconds=(uint)Math.Min(100,Math.Max(1,budgetMilliseconds-(int)wait.ElapsedMilliseconds));
                FrameInfo info; int hr=V<Acquire>(duplication,8)(duplication,timeoutMilliseconds,out info,out resource);
                bool timeout=hr==unchecked((int)0x887A0027);
                if(!timeout) { Check(hr,"AcquireNextFrame"); acquired=true; }
                token.ThrowIfCancellationRequested(); ValidateTarget(expectedWindow);
                if(timeout) {
                    Bitmap cached=CachedFrame(expectedWindow,"timeout"); if(cached!=null) return cached;
                    continue;
                }
                LastPresentTime=info.LastPresent;
                if(info.Protected!=0) throw new InvalidOperationException(L10n.T("messages.Capture.007"));
                // A successful AcquireNextFrame can contain only a pointer update. Its
                // initial texture is not a desktop image; wait for LastPresentTime != 0.
                if(info.LastPresent==0) {
                    SkippedPointerOnlyFrames++;
                    Bitmap cached=CachedFrame(expectedWindow,"pointer_only"); if(cached!=null) return cached;
                    continue;
                }
                Guid iid=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); Check(Marshal.QueryInterface(resource,ref iid,out texture),"QueryTexture2D");
                TextureDesc d; V<GetTextureDesc>(texture,10)(texture,out d);
                if(staging==IntPtr.Zero || textureWidth!=d.Width || textureHeight!=d.Height) {
                    Release(ref staging); d.Usage=3; d.BindFlags=0; d.CpuAccessFlags=0x20000; d.MiscFlags=0;
                    Check(V<CreateTexture>(device,5)(device,ref d,IntPtr.Zero,out staging),"CreateTexture2D"); textureWidth=d.Width; textureHeight=d.Height;
                }
                V<CopyResource>(context,47)(context,staging,texture);
                Mapped m; Check(V<Map>(context,14)(context,staging,0,1,0,out m),"Map"); mapped=true;
                Bitmap bitmap=new Bitmap((int)d.Width,(int)d.Height,PixelFormat.Format32bppRgb);
                try {
                    BitmapData data=bitmap.LockBits(new Rectangle(0,0,bitmap.Width,bitmap.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
                    try { for(int y=0;y<bitmap.Height;y++) CopyMemory(IntPtr.Add(data.Scan0,y*data.Stride),IntPtr.Add(m.Data,checked((int)(y*m.RowPitch))),(UIntPtr)(d.Width*4)); }
                    finally { bitmap.UnlockBits(data); }
                    DiscardLastFrame(); lastFrame=(Bitmap)bitmap.Clone(); lastFrameWindow=expectedWindow;
                    Bitmap accepted=bitmap; bitmap=null; return accepted;
                } finally { if(bitmap!=null) bitmap.Dispose(); }
            } finally {
                if(mapped) V<Unmap>(context,15)(context,staging,0);
                Release(ref texture); Release(ref resource);
                if(acquired) V<ReleaseFrame>(duplication,14)(duplication);
            }
        }
        throw new InvalidOperationException(L10n.T("messages.Capture.008"));
    }
    void DiscardLastFrame() { if(lastFrame!=null) { lastFrame.Dispose(); lastFrame=null; } lastFrameWindow=IntPtr.Zero; }
    void Clear() { DiscardLastFrame(); Release(ref staging); Release(ref duplication); Release(ref context); Release(ref device); monitor=IntPtr.Zero; }
    public void Dispose() { lock(gate) Clear(); }
}
}

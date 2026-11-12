using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace Minus700K {
// A GUID identifies this installation's tray entry across restarts and updates.
// A different executable path gets a different GUID: Windows binds unsigned
// notification icons to their executable path, including side-by-side builds.
internal sealed class TrayNotificationIcon : IDisposable {
    const uint Add=0,Modify=1,Delete=2,SetFocus=3,SetVersion=4;
    const uint MessageFlag=1,IconFlag=2,TipFlag=4,GuidFlag=0x20,ShowTipFlag=0x80;
    const int CallbackMessage=0x8000+71,ContextMenuMessage=0x007B;
    const int LeftDoubleClick=0x0203,RightButtonUp=0x0205,KeyboardSelect=0x0401;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct NotifyData {
        internal uint Size; internal IntPtr Window; internal uint Id,Flags,Callback;
        internal IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] internal string Tip;
        internal uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] internal string Info;
        internal uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] internal string InfoTitle;
        internal uint InfoFlags; internal Guid Guid; internal IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool Shell_NotifyIconW(uint message,ref NotifyData data);
    [StructLayout(LayoutKind.Sequential)] struct IconIdentifier {
        internal uint Size; internal IntPtr Window; internal uint Id; internal Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)] struct IconRectangle { internal int Left,Top,Right,Bottom; }
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier,out IconRectangle rectangle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string text);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] static extern bool PostMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    sealed class MessageWindow : NativeWindow,IDisposable {
        readonly TrayNotificationIcon owner;
        internal MessageWindow(TrayNotificationIcon owner) {
            this.owner=owner;
            // A hidden top-level window receives Explorer's TaskbarCreated
            // broadcast; message-only windows do not receive broadcasts.
            CreateHandle(new CreateParams { Caption="Minus700K tray notifications",ExStyle=0x80 });
        }
        protected override void WndProc(ref Message message) {
            if(owner.Receive(ref message)) return;
            base.WndProc(ref message);
        }
        public void Dispose() { DestroyHandle(); }
    }
    readonly Guid identity; readonly MessageWindow window; readonly Timer retry;
    readonly uint taskbarCreated;
    Icon icon; string text=""; ContextMenuStrip menu;
    bool visible,registered,version4,disposed,failureLogged;
    internal TrayNotificationIcon() : this(InstallationIdentity(Application.ExecutablePath)) { }
    // Explicit identities keep installed diagnostics separate from user settings.
    internal TrayNotificationIcon(Guid identity) {
        if(identity==Guid.Empty) throw new ArgumentException("A tray identity is required.","identity");
        this.identity=identity; taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        window=new MessageWindow(this);
        retry=new Timer { Interval=1000 }; retry.Tick+=delegate { UpdateIcon(); };
    }
    internal static Guid InstallationIdentity(string executable) {
        string path=Path.GetFullPath(executable).ToUpperInvariant();
        using(var hash=SHA256.Create()) {
            byte[] bytes=hash.ComputeHash(Encoding.UTF8.GetBytes("Minus700K.NotificationIcon.v1\0"+path));
            byte[] guid=new byte[16]; Array.Copy(bytes,guid,guid.Length); return new Guid(guid);
        }
    }
    internal Guid Identity { get { return identity; } }
    internal IntPtr Handle { get { return window.Handle; } }
    internal bool Registered { get { return registered; } }
    internal event EventHandler DoubleClick;
    internal Icon Icon {
        get { return icon; }
        set { if(ReferenceEquals(icon,value)) return; icon=value; UpdateIcon(); }
    }
    internal string Text {
        get { return text; }
        set {
            value=value??""; if(value.Length>127) value=value.Substring(0,127);
            if(text==value) return; text=value; UpdateIcon();
        }
    }
    internal bool Visible {
        get { return visible; }
        set { if(visible==value) return; visible=value; UpdateIcon(); }
    }
    internal ContextMenuStrip ContextMenuStrip {
        get { return menu; }
        set {
            if(menu!=null) menu.Closed-=MenuClosed;
            menu=value;
            if(menu!=null) menu.Closed+=MenuClosed;
        }
    }
    NotifyData Data(uint flags) {
        return new NotifyData { Size=(uint)Marshal.SizeOf(typeof(NotifyData)),Window=window.Handle,Id=1,
            Flags=flags|GuidFlag,Callback=CallbackMessage,Icon=icon==null?IntPtr.Zero:icon.Handle,
            Tip=text,Info="",InfoTitle="",Guid=identity };
    }
    void UpdateIcon() {
        if(disposed) return;
        if(!visible || icon==null) { RemoveIcon(); retry.Stop(); return; }
        var data=Data(MessageFlag|IconFlag|TipFlag|ShowTipFlag);
        if(registered && Shell_NotifyIconW(Modify,ref data)) { retry.Stop(); failureLogged=false; return; }
        registered=false; version4=false;
        // A transient failure or repeated TaskbarCreated message can leave the
        // old entry present. Updating that same GUID makes recovery idempotent.
        if(Shell_NotifyIconW(Add,ref data) || Shell_NotifyIconW(Modify,ref data)) {
            registered=true;
            var version=Data(0); version.Version=4;
            version4=Shell_NotifyIconW(SetVersion,ref version);
            retry.Stop(); failureLogged=false;
        } else {
            // Explorer may be starting or restarting. Keep the same identity
            // when retrying; never rewrite the user's promotion preference.
            retry.Start();
            if(!failureLogged) { failureLogged=true; Log.Write("tray icon registration unavailable; retrying"); }
        }
    }
    void RemoveIcon() {
        if(!registered) return;
        var data=Data(0); Shell_NotifyIconW(Delete,ref data); registered=false; version4=false;
    }
    bool Receive(ref Message message) {
        if(disposed) return false;
        if(taskbarCreated!=0 && message.Msg==taskbarCreated) {
            registered=false; version4=false; UpdateIcon(); return true;
        }
        if(message.Msg!=CallbackMessage || !visible) return false;
        int notification=version4?(int)(message.LParam.ToInt64()&0xffff):message.LParam.ToInt32();
        if(notification==LeftDoubleClick || notification==KeyboardSelect) {
            var handler=DoubleClick; if(handler!=null) handler(this,EventArgs.Empty);
        } else if(notification==ContextMenuMessage || (!version4 && notification==RightButtonUp)) {
            if(menu!=null && !menu.IsDisposed) {
                SetForegroundWindow(window.Handle);
                var identifier=new IconIdentifier { Size=(uint)Marshal.SizeOf(typeof(IconIdentifier)),Guid=identity };
                IconRectangle rectangle;
                if(Shell_NotifyIconGetRect(ref identifier,out rectangle)==0) {
                    // Keyboard activation must also open beside the tray icon,
                    // even when the mouse cursor is on another monitor.
                    var center=new Point((rectangle.Left+rectangle.Right)/2,(rectangle.Top+rectangle.Bottom)/2);
                    var working=Screen.FromPoint(center).WorkingArea;
                    bool above=rectangle.Top>=working.Bottom || rectangle.Left>=working.Right;
                    menu.Show(above?new Point(rectangle.Right,rectangle.Top):new Point(rectangle.Left,rectangle.Bottom),
                        above?ToolStripDropDownDirection.AboveLeft:ToolStripDropDownDirection.BelowRight);
                } else menu.Show(Cursor.Position);
                PostMessage(window.Handle,0,IntPtr.Zero,IntPtr.Zero);
            }
        }
        return true;
    }
    void MenuClosed(object sender,ToolStripDropDownClosedEventArgs args) {
        if(disposed || !registered) return;
        var data=Data(0); Shell_NotifyIconW(SetFocus,ref data);
        PostMessage(window.Handle,0,IntPtr.Zero,IntPtr.Zero);
    }
    public void Dispose() {
        if(disposed) return;
        retry.Stop(); RemoveIcon(); disposed=true;
        if(menu!=null) menu.Closed-=MenuClosed;
        retry.Dispose(); window.Dispose();
        // The caller owns the shared state icons and context menu.
    }
}
}

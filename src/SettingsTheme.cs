using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Minus700K {
internal static class SettingsTheme {
    // Bind resource keys at creation instead of attempting a reverse lookup of
    // translated text. Weak ownership avoids retaining closed settings windows.
    static readonly ConditionalWeakTable<Control,List<Action>> languageBindings=new ConditionalWeakTable<Control,List<Action>>();
    internal static string ResolveText(string text) { return text!=null && text.StartsWith("ui.",StringComparison.Ordinal)?L10n.T(text):text; }
    internal static void BindLanguage(Control control,Action update) { languageBindings.GetOrCreateValue(control).Add(update); update(); }
    internal static void BindText(Control control,string key) { if(key!=null && key.StartsWith("ui.",StringComparison.Ordinal)) BindLanguage(control,delegate { control.Text=L10n.T(key); }); }
    internal static void RefreshLanguage(Control control) {
        if(control==null || control.IsDisposed) return;
        List<Action> actions; if(languageBindings.TryGetValue(control,out actions)) foreach(var update in actions) update();
        foreach(Control child in control.Controls) RefreshLanguage(child);
        var combo=control as LanguageComboBox; if(combo!=null) combo.RefreshLabels();
        control.Invalidate();
    }
    internal static readonly Color Background=Color.FromArgb(15,23,34),Sidebar=Color.FromArgb(19,30,43),Card=Color.FromArgb(26,39,54),Input=Color.FromArgb(19,30,43),Border=Color.FromArgb(47,65,84),Text=Color.FromArgb(235,242,249),Muted=Color.FromArgb(153,173,192),Accent=Color.FromArgb(98,207,232),Selected=Color.FromArgb(29,58,76),Error=Color.FromArgb(255,157,157);
    internal static Font Font(float size=10,FontStyle style=FontStyle.Regular) { return new Font("Yu Gothic UI",size,style); }
    internal static Label Label(string text,bool muted=false,float size=10) { var label=new Label { Text=text,AutoSize=true,ForeColor=muted?Muted:Text,BackColor=Color.Transparent,Font=Font(size),Margin=new Padding(0,0,0,4) }; BindText(label,text); return label; }
    internal static Button Button(string text,bool primary=false,int width=160) {
        var button=new Button { Text=text,Width=width,Height=38,FlatStyle=FlatStyle.Flat,BackColor=primary?Accent:Card,ForeColor=primary?Background:Text,Cursor=Cursors.Hand,Font=Font(9.5f,FontStyle.Bold),Margin=new Padding(0,0,10,0),UseVisualStyleBackColor=false };
        button.FlatAppearance.BorderColor=primary?Accent:Border; button.FlatAppearance.BorderSize=1; button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(134,222,241):Selected;
        BindText(button,text); return button;
    }
    internal static TextBox TextBox(string text,bool multiline=false,string placeholder=null) {
        TextBox input=placeholder!=null?new PlaceholderTextBox { PlaceholderText=placeholder }:multiline?new AutoScrollTextBox():new TextBox();
        input.Multiline=multiline; input.Text=multiline?EditorText(text):text??""; input.AcceptsReturn=multiline; input.ScrollBars=multiline?ScrollBars.Vertical:ScrollBars.None;
        input.BorderStyle=BorderStyle.FixedSingle; input.BackColor=Input; input.ForeColor=Text; input.Font=Font(10); input.Width=280; input.Height=multiline?170:30; input.Margin=new Padding(0);
        return input;
    }
    internal static string EditorText(string text) { return (text??"").Replace("\r\n","\n").Replace("\r","\n").Replace("\n","\r\n"); }
    internal static ComboBox Combo() {
        var combo=new LanguageComboBox { DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat,BackColor=Input,ForeColor=Text,Font=Font(10),Width=280,DropDownWidth=400,IntegralHeight=false,MaxDropDownItems=10,Margin=new Padding(0),DrawMode=DrawMode.OwnerDrawFixed,ItemHeight=26 };
        combo.DrawItem+=delegate(object sender,DrawItemEventArgs e) {
            using(var brush=new SolidBrush((e.State&DrawItemState.Selected)!=0?Selected:Input)) e.Graphics.FillRectangle(brush,e.Bounds);
            string value=e.Index>=0?Convert.ToString(combo.Items[e.Index]):combo.Text;
            TextRenderer.DrawText(e.Graphics,value,combo.Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-12,e.Bounds.Height),combo.Enabled?Text:Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);
            e.DrawFocusRectangle();
        };
        return combo;
    }
    internal static GraphicsPath Rounded(RectangleF bounds,float radius) {
        float d=radius*2; var path=new GraphicsPath();
        path.AddArc(bounds.X,bounds.Y,d,d,180,90); path.AddArc(bounds.Right-d,bounds.Y,d,d,270,90); path.AddArc(bounds.Right-d,bounds.Bottom-d,d,d,0,90); path.AddArc(bounds.X,bounds.Bottom-d,d,d,90,90); path.CloseFigure(); return path;
    }
}
internal sealed class LanguageComboBox : ComboBox {
    internal void RefreshLabels() {
        // Owner drawing reads ToString dynamically. Refresh the native item text
        // too, for keyboard lookup, ComboBox.Text and accessibility clients.
        if(Items.Count==0 || !(Items[0] is SettingsChoice)) return;
        BeginUpdate();
        try { int selection=SelectedIndex; base.RefreshItems(); SelectedIndex=selection; }
        finally { EndUpdate(); }
    }
}
// Keep the native editor and undo buffer intact: only the scrollbar's native
// visibility changes. Setting TextBox.ScrollBars repeatedly recreates its handle.
internal class AutoScrollTextBox : TextBox {
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { internal int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct TextMetric {
        internal int Height,Ascent,Descent,InternalLeading,ExternalLeading,AverageWidth,MaximumWidth,Weight,Overhang,AspectX,AspectY;
        internal ushort FirstChar,LastChar,DefaultChar,BreakChar;
        internal byte Italic,Underlined,StruckOut,PitchAndFamily,CharSet;
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,ref NativeRect rectangle);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr window,int bar,bool show);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window,IntPtr context);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr context,IntPtr value);
    [DllImport("gdi32.dll",CharSet=CharSet.Unicode)] static extern bool GetTextMetrics(IntPtr context,out TextMetric metric);
    bool updating,queued; int handleGeneration;
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); handleGeneration++; queued=false; QueueScrollbarUpdate(); }
    protected override void OnHandleDestroyed(EventArgs e) { handleGeneration++; queued=false; base.OnHandleDestroyed(e); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); QueueScrollbarUpdate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); QueueScrollbarUpdate(); }
    protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); QueueScrollbarUpdate(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); QueueScrollbarUpdate(); }
    void QueueScrollbarUpdate() {
        if(updating || queued || !Multiline || !IsHandleCreated || IsDisposed || Disposing) return;
        queued=true; int generation=handleGeneration;
        BeginInvoke((MethodInvoker)delegate {
            if(generation!=handleGeneration) return;
            queued=false;
            if(!IsDisposed && !Disposing && IsHandleCreated) UpdateScrollbar();
        });
    }
    int NativeLineHeight() {
        IntPtr context=GetDC(Handle); if(context==IntPtr.Zero) return Math.Max(1,Font.Height);
        IntPtr previous=IntPtr.Zero;
        try {
            IntPtr font=SendMessage(Handle,0x0031,IntPtr.Zero,IntPtr.Zero); // WM_GETFONT
            if(font!=IntPtr.Zero) previous=SelectObject(context,font);
            TextMetric metric;
            return GetTextMetrics(context,out metric) && metric.Height>0?metric.Height:Math.Max(1,Font.Height);
        } finally { if(previous!=IntPtr.Zero) SelectObject(context,previous); ReleaseDC(Handle,context); }
    }
    void UpdateScrollbar() {
        if(updating || !Multiline) return;
        updating=true;
        try {
            bool wasVisible=(GetWindowLong(Handle,-16)&0x00200000)!=0;
            int firstLine=SendMessage(Handle,0x00CE,IntPtr.Zero,IntPtr.Zero).ToInt32(); // EM_GETFIRSTVISIBLELINE
            // Measure at full width. Measuring only with the existing bar would
            // retain it when its own width is the reason one extra line wraps.
            if(wasVisible) ShowScrollBar(Handle,1,false);
            bool overflow=false;
            if(TextLength>0) {
                var rectangle=new NativeRect(); SendMessage(Handle,0x00B2,IntPtr.Zero,ref rectangle); // EM_GETRECT
                int height=Math.Max(0,rectangle.Bottom-rectangle.Top);
                int lines=SendMessage(Handle,0x00BA,IntPtr.Zero,IntPtr.Zero).ToInt32(); // EM_GETLINECOUNT includes native word wrapping
                overflow=height>0 && (long)lines*NativeLineHeight()>height;
            }
            if(overflow) {
                ShowScrollBar(Handle,1,true);
                // Probing the wider format can clamp its scroll position. Restore
                // the viewport when the final format still has the same scrollbar.
                if(wasVisible) {
                    int current=SendMessage(Handle,0x00CE,IntPtr.Zero,IntPtr.Zero).ToInt32();
                    if(current!=firstLine) SendMessage(Handle,0x00B6,IntPtr.Zero,new IntPtr(firstLine-current)); // EM_LINESCROLL
                }
            }
        } finally { updating=false; }
    }
}
// WinForms on .NET Framework does not support multiline cue banners. Paint the
// hint over the native editor without ever placing it in Text or its undo stack.
internal sealed class PlaceholderTextBox : AutoScrollTextBox {
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { internal int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,ref NativeRect rectangle);
    string placeholder="";
    internal string PlaceholderText { get { return placeholder; } set { placeholder=SettingsTheme.EditorText(value); Invalidate(); } }
    internal bool PlaceholderVisible { get { return TextLength==0 && placeholder.Length>0; } }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void WndProc(ref Message message) {
        base.WndProc(ref message);
        // TextLength and EM_GETRECT send nested window messages. Filter those
        // messages before inspecting TextLength, otherwise WM_GETTEXTLENGTH
        // recursively re-enters this method until the stack is exhausted.
        if((message.Msg!=0x000F && message.Msg!=0x0317 && message.Msg!=0x0318) || !IsHandleCreated || !PlaceholderVisible) return;
        // WM_PRINT / WM_PRINTCLIENT use the supplied HDC for native snapshots.
        using(var graphics=message.Msg!=0x000F && message.WParam!=IntPtr.Zero?Graphics.FromHdc(message.WParam):Graphics.FromHwnd(Handle)) {
            var bounds=new NativeRect(); SendMessage(Handle,0x00B2,IntPtr.Zero,ref bounds); // EM_GETRECT
            Rectangle area=Rectangle.Intersect(ClientRectangle,Rectangle.FromLTRB(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom));
            if(area.Width<=0 || area.Height<=0) return;
            graphics.SetClip(area);
            TextRenderer.DrawText(graphics,placeholder,Font,area,SettingsTheme.Muted,TextFormatFlags.Left|TextFormatFlags.Top|TextFormatFlags.WordBreak|TextFormatFlags.NoPrefix|TextFormatFlags.NoPadding|TextFormatFlags.TextBoxControl|TextFormatFlags.PreserveGraphicsClipping);
        }
    }
}
internal sealed class ToggleSwitch : CheckBox {
    internal ToggleSwitch(bool value) {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque|ControlStyles.ResizeRedraw,true);
        BackColor=SettingsTheme.Card; UseVisualStyleBackColor=false;
        Checked=value; AutoSize=false; Size=new Size(48,28); Cursor=Cursors.Hand; AccessibleRole=AccessibleRole.CheckButton; Margin=new Padding(0,4,0,0);
    }
    protected override void OnPaint(PaintEventArgs e) {
        // Own the entire surface: a themed/transparent CheckBox background can
        // retain or replay surrounding text when Windows repaints this switch.
        using(var background=new SolidBrush(BackColor)) e.Graphics.FillRectangle(background,ClientRectangle);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        float scale=Math.Min(Width/48f,Height/28f); var bounds=new RectangleF(1,3*scale,44*scale,22*scale);
        using(var path=SettingsTheme.Rounded(bounds,11*scale)) using(var brush=new SolidBrush(!Enabled?SettingsTheme.Border:Checked?SettingsTheme.Accent:Color.FromArgb(74,92,110))) e.Graphics.FillPath(brush,path);
        using(var brush=new SolidBrush(Enabled?SettingsTheme.Text:SettingsTheme.Muted)) e.Graphics.FillEllipse(brush,(Checked?25:4)*scale,6*scale,16*scale,16*scale);
        if(Focused) using(var pen=new Pen(SettingsTheme.Accent)) e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
    }
    protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}
internal sealed class ShortcutBox : TextBox {
    readonly Func<IntPtr> foregroundWindow=Native.GetForegroundWindow;
    ShortcutCapture capture;
    bool captureActive;
    int captureRevision;
    internal ShortcutBox(string value) {
        Text=value; ReadOnly=true; BorderStyle=BorderStyle.FixedSingle; BackColor=SettingsTheme.Input; ForeColor=SettingsTheme.Accent; Font=SettingsTheme.Font(11,FontStyle.Bold); Width=190; TextAlign=HorizontalAlignment.Center; Cursor=Cursors.Hand; SettingsTheme.BindLanguage(this,delegate { AccessibleDescription=L10n.T("ui.shortcut.help"); });
    }
    internal void SetCaptureActive(bool value) {
        if(value && !captureActive) captureRevision++;
        captureActive=value; RefreshCapture();
    }
    void RefreshCapture() {
        if(captureActive && Focused && Enabled && Visible && !IsDisposed && !Disposing) {
            if(capture==null) capture=new ShortcutCapture(CanCapture,QueueShortcut);
        } else if(capture!=null) { capture.Dispose(); capture=null; }
    }
    bool CanCapture() {
        Form form=FindForm();
        return captureActive && Focused && Enabled && Visible && form!=null && form.IsHandleCreated && foregroundWindow()==form.Handle;
    }
    static bool IsNavigation(KeyEventArgs key) { return (key.KeyCode==Keys.Tab && key.Modifiers==Keys.None) || key.KeyCode==Keys.Escape; }
    static string ShortcutText(Keys value) {
        var key=new KeyEventArgs(value);
        int code=(int)key.KeyCode;
        if(IsNavigation(key) || code<8 || code>=255 || code==16 || code==17 || code==18 || (code>=160 && code<=165) || code==91 || code==92) return null;
        return (key.Control?"Ctrl+":"")+(key.Alt?"Alt+":"")+(key.Shift?"Shift+":"")+key.KeyCode;
    }
    void QueueShortcut(Keys value) {
        string text=ShortcutText(value); if(text==null) return;
        int revision=++captureRevision;
        // Keep the hook callback short. A competing shortcut may activate
        // another window before this runs; retain the press already captured.
        BeginInvoke((MethodInvoker)delegate {
            if(!IsDisposed && !Disposing && revision==captureRevision) Text=text;
        });
    }
    protected override void OnLostFocus(EventArgs e) { SetCaptureActive(false); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); RefreshCapture(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); RefreshCapture(); }
    protected override void OnKeyDown(KeyEventArgs e) {
        if(IsNavigation(e)) { base.OnKeyDown(e); return; }
        e.SuppressKeyPress=true; e.Handled=true;
        // Native messages remain a fallback, but must not overwrite a captured
        // chord on autorepeat after its modifier has already been released.
        if(capture!=null && capture.Installed) return;
        string text=ShortcutText(e.KeyData); if(text!=null) { captureRevision++; Text=text; }
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData) {
        var key=new KeyEventArgs(keyData);
        if(IsNavigation(key)) return base.ProcessCmdKey(ref msg,keyData);
        OnKeyDown(key); return key.Handled || base.ProcessCmdKey(ref msg,keyData);
    }
    protected override void Dispose(bool disposing) {
        if(disposing) { captureRevision++; SetCaptureActive(false); }
        base.Dispose(disposing);
    }
}
internal sealed class SettingsChoice {
    internal readonly object Value; readonly string label; readonly Func<string> display;
    internal SettingsChoice(object value,string label) { Value=value; this.label=label; }
    internal SettingsChoice(object value,Func<string> display) { Value=value; this.display=display; }
    public override string ToString() { return display==null?SettingsTheme.ResolveText(label):display(); }
}
internal sealed class SettingsPage : Panel {
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window,int index);
    [DllImport("user32.dll")] static extern bool ShowScrollBar(IntPtr window,int bar,bool show);
    bool fitting,fitPending,fitQueued;
    internal SettingsPage() {
        Dock=DockStyle.Fill; AutoScroll=true; BackColor=SettingsTheme.Background; Padding=new Padding(0,0,8,18); AccessibleRole=AccessibleRole.PropertyPage;
        Resize+=delegate { PerformLayout(); }; Layout+=delegate { Fit(); }; ControlAdded+=delegate(object sender,ControlEventArgs e) { e.Control.SizeChanged+=delegate { PerformLayout(); }; PerformLayout(); };
    }
    void Fit() {
        if(fitting) { fitPending=true; return; } fitting=true; fitPending=false;
        try {
            // A page begins at 200px before docking. Enforcing a larger minimum
            // here creates a native horizontal range that can survive later resizing.
            int scrollY=AutoScrollPosition.Y;
            int width=Math.Max(0,ClientSize.Width-Padding.Horizontal-SystemInformation.VerticalScrollBarWidth),y=Padding.Top+scrollY;
            foreach(Control child in Controls) { child.Width=width; child.Location=new Point(Padding.Left,y); y+=child.Height+child.Margin.Bottom; }
            AutoScrollMinSize=new Size(0,y-scrollY+Padding.Bottom);
            bool fits=true; foreach(Control child in Controls) if(child.Right+Padding.Right>ClientSize.Width) { fits=false; break; }
            if(fits) {
                HScroll=false; HorizontalScroll.Enabled=false;
                if(AutoScrollPosition.X!=0) AutoScrollPosition=new Point(0,-scrollY);
                // WinForms may report Visible=false while WS_HSCROLL is still set.
                // Clear that stale native bar only after the cards fit the viewport.
                if(IsHandleCreated && (GetWindowLong(Handle,-16)&0x00100000)!=0) ShowScrollBar(Handle,0,false);
            }
        } finally {
            fitting=false;
            // A vertical bar or auto-sized card can change the available area
            // during this pass. Apply the resulting dimensions after layout settles.
            if(fitPending && !fitQueued && IsHandleCreated && !IsDisposed) {
                fitQueued=true;
                BeginInvoke((MethodInvoker)delegate { fitQueued=false; if(!IsDisposed) Fit(); });
            }
        }
    }
}
internal sealed class SettingsCard : Panel {
    readonly TableLayoutPanel body=new TableLayoutPanel { AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Dock=DockStyle.Top,ColumnCount=1,Padding=new Padding(20,18,20,18),BackColor=SettingsTheme.Card };
    internal SettingsCard(string title,string detail=null) {
        DoubleBuffered=true; BackColor=SettingsTheme.Card; AutoSize=false; Margin=new Padding(0,0,0,16); Padding=new Padding(1);
        body.SizeChanged+=delegate { Height=body.Height+Padding.Vertical; };
        Controls.Add(body); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        var heading=SettingsTheme.Label(title,false,11); heading.Font=SettingsTheme.Font(11,FontStyle.Bold); heading.Margin=new Padding(0,0,0,detail==null?10:5); Add(heading);
        if(detail!=null) { var description=SettingsTheme.Label(detail,true,9); description.Margin=new Padding(0,0,0,12); Add(description); }
        Resize+=delegate { foreach(Control c in body.Controls) if(c is Label) ((Label)c).MaximumSize=new Size(Math.Max(200,Width-44),0); };
    }
    internal void Add(Control child) { child.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top; body.Controls.Add(child,0,body.RowCount++); }
    internal void Field(string title,string detail,Control input) {
        var row=new TableLayoutPanel { ColumnCount=2,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Margin=new Padding(0,8,0,8),Dock=DockStyle.Top };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,53)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,47));
        var labels=new FlowLayoutPanel { AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Dock=DockStyle.Fill,Margin=new Padding(0,0,16,0) };
        var name=SettingsTheme.Label(title); labels.Controls.Add(name);
        if(!string.IsNullOrEmpty(detail)) { var help=SettingsTheme.Label(detail,true,9); labels.Controls.Add(help); }
        labels.Resize+=delegate { foreach(Control c in labels.Controls) if(c is Label) ((Label)c).MaximumSize=new Size(Math.Max(100,labels.ClientSize.Width-2),0); };
        SettingsTheme.BindLanguage(input,delegate { input.AccessibleName=SettingsTheme.ResolveText(title); input.AccessibleDescription=SettingsTheme.ResolveText(detail); }); input.Anchor=AnchorStyles.Top|AnchorStyles.Right; input.Margin=new Padding(0,2,0,0);
        if(!(input is ToggleSwitch)) { input.Dock=DockStyle.Top; input.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top; }
        row.Controls.Add(labels,0,0); row.Controls.Add(input,1,0); Add(row);
    }
    internal void Buttons(params Control[] buttons) { var flow=new FlowLayoutPanel { AutoSize=true,WrapContents=true,Dock=DockStyle.Top,Margin=new Padding(0,12,0,0) }; foreach(var b in buttons) flow.Controls.Add(b); Add(flow); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using(var pen=new Pen(SettingsTheme.Border)) e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1); }
}
}

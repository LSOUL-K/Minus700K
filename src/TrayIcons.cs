using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace Minus700K {
// Own the native icon handles for the lifetime of a notification icon or window.
// All states are loaded once from the executable; key transitions never read SMB files.
internal sealed class TrayIcons : IDisposable {
    readonly Dictionary<string,Icon> icons=new Dictionary<string,Icon>(StringComparer.Ordinal);
    bool disposed;
    internal TrayIcons() : this(SystemInformation.SmallIconSize) { }
    internal TrayIcons(Size size) {
        try {
            foreach(string name in new[]{"tray-ready","tray-recording","tray-processing","tray-speaking","tray-paused"}) {
                using(Stream stream=typeof(TrayIcons).Assembly.GetManifestResourceStream("Minus700K.Icons."+name+".ico")) {
                    if(stream==null) throw new InvalidOperationException("Embedded tray icon is missing: "+name);
                    // Detach a native copy: .NET Framework's raw PNG-backed Icon can
                    // fail in ToBitmap(), while a native HICON works in both paths.
                    using(Icon source=new Icon(stream,size))
                    using(Icon native=Icon.FromHandle(source.Handle)) icons.Add(name,(Icon)native.Clone());
                }
            }
        } catch { Dispose(); throw; }
    }
    internal Icon ForState(string state) {
        if(disposed) throw new ObjectDisposedException("TrayIcons");
        string name=state=="recording"?"tray-recording":
            state=="recognizing" || state=="asking"?"tray-processing":
            state=="speaking"?"tray-speaking":state=="paused"?"tray-paused":"tray-ready";
        return icons[name];
    }
    public void Dispose() {
        if(disposed) return;
        disposed=true;
        foreach(Icon icon in icons.Values) icon.Dispose();
        icons.Clear();
    }
}
}

using System;
using System.Diagnostics;
using System.Threading;

namespace Minus700K {
// The installer renews a short lease while waiting for this exact process.
// Cancelling or losing the installer releases the input gate automatically.
internal sealed class UpdateShutdown : IDisposable {
    readonly EventWaitHandle signal;
    readonly System.Windows.Forms.Timer timer;
    readonly Func<bool> idle;
    readonly Action begin,resume,shutdown;
    readonly Stopwatch clock=Stopwatch.StartNew();
    long lastSignal,requestedAt; bool disposed;
    internal bool Pending { get; private set; }
    internal static string SignalName(int pid) { return "Local\\Minus700K_Update_7E940DB1_"+pid; }
    internal UpdateShutdown(Func<bool> idle,Action begin,Action resume,Action shutdown) {
        this.idle=idle; this.begin=begin; this.resume=resume; this.shutdown=shutdown;
        signal=new EventWaitHandle(false,EventResetMode.AutoReset,SignalName(Process.GetCurrentProcess().Id));
        timer=new System.Windows.Forms.Timer { Interval=100 };
        timer.Tick+=delegate { Poll(); }; timer.Start();
    }
    void Poll() {
        if(disposed) return;
        long now=clock.ElapsedMilliseconds;
        if(signal.WaitOne(0)) {
            lastSignal=now;
            if(!Pending) { Pending=true; requestedAt=now; begin(); }
        }
        if(!Pending) return;
        if(now-lastSignal>2000) { Pending=false; resume(); return; }
        // Let queued UI completion callbacks settle before the final idle check.
        if(now-requestedAt>=100 && idle()) { timer.Stop(); shutdown(); }
    }
    public void Dispose() {
        if(disposed) return; disposed=true;
        timer.Stop(); timer.Dispose(); signal.Dispose();
    }
}
}

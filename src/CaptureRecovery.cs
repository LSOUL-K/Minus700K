using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace Minus700K {
// A blank frame during a display transition can recover just like ACCESS_LOST.
internal sealed class CaptureFrameUnavailableException : InvalidOperationException {
    internal CaptureFrameUnavailableException(string message) : base(message) { }
}
internal static class CaptureRecovery {
    internal const int BudgetMilliseconds=2000,MaximumAttempts=4;
    static bool Recoverable(Exception error) {
        if(error is CaptureFrameUnavailableException) return true;
        var com=error as COMException;
        return com!=null && (com.ErrorCode==unchecked((int)0x887A0026) ||
            com.ErrorCode==unchecked((int)0x887A0005) || com.ErrorCode==unchecked((int)0x887A0007));
    }
    internal static Bitmap Run(Func<int,Bitmap> take,Action reset,Action validateTarget,CancellationToken token) {
        Stopwatch elapsed=Stopwatch.StartNew();
        for(int attempt=0;attempt<MaximumAttempts;attempt++) {
            Bitmap image=null;
            try {
                token.ThrowIfCancellationRequested(); validateTarget();
                image=take(Math.Max(1,BudgetMilliseconds-(int)elapsed.ElapsedMilliseconds));
                token.ThrowIfCancellationRequested(); validateTarget();
                if(attempt!=0) Log.Write("capture recovery succeeded attempts="+(attempt+1)+" elapsed_ms="+elapsed.ElapsedMilliseconds);
                Bitmap accepted=image; image=null; return accepted;
            } catch(Exception error) {
                if(image!=null) { image.Dispose(); image=null; }
                reset(); token.ThrowIfCancellationRequested();
                if(!Recoverable(error) || attempt==MaximumAttempts-1) throw;
                int delay=100<<attempt;
                if(elapsed.ElapsedMilliseconds+delay>=BudgetMilliseconds) throw;
                var com=error as COMException;
                Log.Write("capture recovery attempt="+(attempt+1)+" reason="+(com==null?"blank_frame":"0x"+com.ErrorCode.ToString("X8"))+" elapsed_ms="+elapsed.ElapsedMilliseconds+" retry_delay_ms="+delay);
                validateTarget();
                if(token.WaitHandle.WaitOne(delay)) token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested(); validateTarget();
                if(elapsed.ElapsedMilliseconds>=BudgetMilliseconds) throw;
            }
        }
        throw new InvalidOperationException("Capture recovery exhausted.");
    }
}
}

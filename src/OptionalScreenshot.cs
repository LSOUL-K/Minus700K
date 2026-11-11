using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace Minus700K {
// Capture is optional input. An unavailable image must not discard recorded speech.
internal sealed class OptionalScreenshot : IDisposable {
    internal Bitmap Image;
    public string status="captured",failureStage="",errorType="",error="";
    public bool attached;
    public long elapsedMilliseconds;
    public int width,height;
    internal bool Available { get { return Image!=null; } }
    internal bool Failed { get { return status=="failed"; } }
    internal static OptionalScreenshot Take(Func<Bitmap> capture,CancellationToken token,string stage="capture",bool enabled=true) {
        token.ThrowIfCancellationRequested();
        // Disabled capture is intentional: never call the pixel source and never
        // report a failure or announce that the screen could not be acquired.
        if(!enabled) return new OptionalScreenshot { status="disabled" };
        OptionalScreenshot result=new OptionalScreenshot(); Stopwatch timer=Stopwatch.StartNew();
        try {
            token.ThrowIfCancellationRequested(); result.Image=capture(); token.ThrowIfCancellationRequested();
            if(result.Image==null) throw new InvalidOperationException(L10n.T("messages.OptionalScreenshot.001"));
            result.width=result.Image.Width; result.height=result.Image.Height;
            if(IsEntirelyBlack(result.Image)) throw new InvalidOperationException(L10n.T("messages.OptionalScreenshot.002"));
        } catch(Exception e) {
            result.Dispose(); if(e is OperationCanceledException && token.IsCancellationRequested) throw;
            result.Fail(stage,e);
        } finally { result.elapsedMilliseconds=timer.ElapsedMilliseconds; }
        return result;
    }
    internal string Save(string path) {
        if(Image==null) return null;
        try { Image.Save(path,ImageFormat.Png); attached=true; status="attached"; return path; }
        catch(Exception e) {
            Fail("save",e);
            try { if(File.Exists(path)) File.Delete(path); } catch { }
            return null;
        } finally { Dispose(); }
    }
    void Fail(string stage,Exception e) {
        attached=false; status="failed"; failureStage=stage; errorType=e.GetType().FullName; error=e.Message;
        Log.Write("screenshot unavailable; continuing with voice only; stage="+stage+" error="+e.Message);
    }
    static bool Black(Color pixel) { return pixel.R==0 && pixel.G==0 && pixel.B==0; }
    internal static unsafe bool IsEntirelyBlack(Bitmap image) {
        // Normal screens exit after a handful of samples. Verify every pixel before
        // rejecting a black image, so small text on a dark screen is not overlooked.
        for(int y=0;y<image.Height;y+=Math.Max(1,image.Height/36))
            for(int x=0;x<image.Width;x+=Math.Max(1,image.Width/64)) if(!Black(image.GetPixel(x,y))) return false;
        BitmapData data=image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {
            for(int y=0;y<image.Height;y++) {
                byte* row=(byte*)data.Scan0+y*data.Stride;
                for(int x=0;x<image.Width;x++) if(row[x*4]!=0 || row[x*4+1]!=0 || row[x*4+2]!=0) return false;
            }
            return true;
        } finally { image.UnlockBits(data); }
    }
    public void Dispose() { if(Image!=null) { Image.Dispose(); Image=null; } }
}
}

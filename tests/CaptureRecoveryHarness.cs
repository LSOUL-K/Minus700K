using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Loads the built application, but never starts it or calls native screen capture.
// Every frame is an in-memory fixture; no microphone, input, credentials or network.
internal static class CaptureRecoveryHarness {
    const BindingFlags Instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    const BindingFlags Static=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly List<string> checks=new List<string>();
    static readonly Dictionary<string,object> report=new Dictionary<string,object>();
    static Assembly assembly;
    static Type recovery,unavailable,desktop,optional;
    static string fixture;

    static void Check(bool condition,string message) { if(!condition) throw new Exception(message); checks.Add(message); }
    static object Invoke(MethodInfo method,object instance,params object[] arguments) {
        try { return method.Invoke(instance,arguments); }
        catch(TargetInvocationException e) { throw e.InnerException??e; }
    }
    static object Field(object value,string name) { return value.GetType().GetField(name,Instance).GetValue(value); }
    static void Set(object value,string name,object data) { value.GetType().GetField(name,Instance).SetValue(value,data); }
    static object Call(object value,string name,params object[] arguments) { return Invoke(value.GetType().GetMethod(name,Instance),value,arguments); }
    static Bitmap Frame(Color color) { var bitmap=new Bitmap(128,72,PixelFormat.Format32bppArgb); using(var graphics=Graphics.FromImage(bitmap)) graphics.Clear(color); return bitmap; }
    static bool Disposed(Bitmap bitmap) { try { bitmap.GetPixel(0,0); return false; } catch(ArgumentException) { return true; } }
    static Exception Catch(Action action) { try { action(); return null; } catch(Exception e) { return e; } }
    static Exception Unavailable() { return (Exception)Activator.CreateInstance(unavailable,Instance,null,new object[]{"synthetic unavailable frame"},null); }
    static Bitmap Run(Func<int,Bitmap> take,Action reset,Action validate,CancellationToken token) {
        return (Bitmap)Invoke(recovery.GetMethod("Run",Static),null,take,reset,validate,token);
    }
    static object Take(Func<Bitmap> take,CancellationToken token,bool enabled=true) {
        return Invoke(optional.GetMethod("Take",Static),null,take,token,"capture",enabled);
    }
    static void Dispose(object value) { if(value!=null) ((IDisposable)value).Dispose(); }

    static void VerifyRecovery() {
        int attempts=0,resets=0,validations=0,firstBudget=0;
        Bitmap image=Frame(Color.CornflowerBlue);
        using(var result=Run(delegate(int budget) { attempts++; firstBudget=budget; return image; },()=>resets++,()=>validations++,CancellationToken.None)) {
            Check(object.ReferenceEquals(image,result) && !Disposed(image),"A normal frame is returned intact on the first attempt");
        }
        Check(attempts==1 && resets==0 && validations>0 && firstBudget>0 && firstBudget<=2000,"Normal capture validates its target and receives a bounded remaining-time budget");

        foreach(int hr in new[]{unchecked((int)0x887A0026),unchecked((int)0x887A0005),unchecked((int)0x887A0007)}) {
            attempts=0; resets=0; var budgets=new List<int>(); var failure=new COMException("synthetic transient DXGI failure",hr);
            using(var result=Run(delegate(int budget) { budgets.Add(budget); if(++attempts==1) throw failure; return Frame(Color.CornflowerBlue); },()=>resets++,delegate {},CancellationToken.None)) {
                Check(result.GetPixel(0,0).B>0 && attempts==2 && resets==1,"Recoverable DXGI HRESULT "+hr.ToString("X8")+" resets capture and retries successfully");
                Check(budgets[1]<budgets[0]-70,"The recovery delay for "+hr.ToString("X8")+" consumes the shared time budget");
            }
        }
        foreach(Exception failure in new Exception[]{new COMException("access denied",unchecked((int)0x80070005)),new COMException("invalid call",unchecked((int)0x887A0001)),new InvalidOperationException("protected frame"),new InvalidOperationException("target changed")}) {
            attempts=0; resets=0;
            Exception error=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; throw failure; },()=>resets++,delegate {},CancellationToken.None)) {} });
            Check(object.ReferenceEquals(error,failure) && attempts==1 && resets==1,"A permanent "+failure.Message+" failure resets state and propagates without retry");
        }

        attempts=0; resets=0; Exception repeated=Unavailable();
        Exception exhausted=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; throw repeated; },()=>resets++,delegate {},CancellationToken.None)) {} });
        Check(object.ReferenceEquals(exhausted,repeated) && attempts==4 && resets==4,"Persistently unavailable frames stop after four attempts and clear every failed attempt");

        attempts=0; resets=0; repeated=new COMException("slow lost device",unchecked((int)0x887A0026));
        Exception budgetError=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; Thread.Sleep(2025); throw repeated; },()=>resets++,delegate {},CancellationToken.None)) {} });
        Check(object.ReferenceEquals(budgetError,repeated) && attempts==1 && resets==1,"An attempt that exhausts the overall time budget is not followed by another attempt");

        using(var cancellation=new CancellationTokenSource()) {
            attempts=0; cancellation.Cancel();
            Exception error=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; return Frame(Color.White); },delegate {},delegate {},cancellation.Token)) {} });
            Check(error is OperationCanceledException && attempts==0,"Cancellation already requested prevents all capture attempts");
        }
        using(var cancellation=new CancellationTokenSource()) {
            attempts=0; resets=0; Stopwatch elapsed=Stopwatch.StartNew();
            Exception error=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; cancellation.CancelAfter(30); throw Unavailable(); },()=>resets++,delegate {},cancellation.Token)) {} });
            Check(error is OperationCanceledException && attempts==1 && resets==1 && elapsed.ElapsedMilliseconds<1000,"Cancellation interrupts a recovery wait without taking another frame");
        }
        using(var cancellation=new CancellationTokenSource()) {
            attempts=0; resets=0; Bitmap interrupted=Frame(Color.White);
            Exception error=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; cancellation.Cancel(); return interrupted; },()=>resets++,delegate {},cancellation.Token)) {} });
            Check(error is OperationCanceledException && attempts==1 && resets==1 && Disposed(interrupted),"Cancellation during capture rejects and disposes the returned bitmap");
        }

        attempts=0; Exception foregroundFailure=new InvalidOperationException("foreground changed before capture");
        Exception foregroundError=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; return Frame(Color.White); },delegate {},delegate { throw foregroundFailure; },CancellationToken.None)) {} });
        Check(object.ReferenceEquals(foregroundError,foregroundFailure) && attempts==0,"A foreground mismatch prevents the first capture");
        attempts=0; resets=0; bool sameTarget=true; Bitmap changed=Frame(Color.White);
        foregroundError=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; sameTarget=false; return changed; },()=>resets++,delegate { if(!sameTarget) throw foregroundFailure; },CancellationToken.None)) {} });
        Check(object.ReferenceEquals(foregroundError,foregroundFailure) && attempts==1 && resets==1 && Disposed(changed),"A foreground change during capture rejects and disposes the returned bitmap");
        int targetChanged=0;
        using(var timer=new Timer(delegate { Interlocked.Exchange(ref targetChanged,1); },null,Timeout.Infinite,Timeout.Infinite)) {
            attempts=0; foregroundFailure=new InvalidOperationException("foreground changed during recovery");
            foregroundError=Catch(delegate { using(var ignored=Run(delegate(int budget) { attempts++; timer.Change(30,Timeout.Infinite); throw Unavailable(); },delegate {},delegate { if(Interlocked.CompareExchange(ref targetChanged,0,0)!=0) throw foregroundFailure; },CancellationToken.None)) {} });
            Check(object.ReferenceEquals(foregroundError,foregroundFailure) && attempts==1,"A foreground change during a retry wait prevents capturing the replacement window");
        }
    }

    static void VerifyBlackFrameCache() {
        object capture=Activator.CreateInstance(desktop,true);
        try {
            Bitmap stale=Frame(Color.Black); Set(capture,"lastFrame",stale); Set(capture,"lastFrameWindow",new IntPtr(17));
            using(var black=Frame(Color.Black)) {
                Exception error=Catch(()=>Call(capture,"ValidateFrame",black));
                Check(error!=null && error.GetType()==unavailable,"The production desktop validator marks a black frame as recoverable");
                Check(Field(capture,"lastFrame")==null && (IntPtr)Field(capture,"lastFrameWindow")==IntPtr.Zero && Disposed(stale),"Rejecting a black frame disposes the cached frame and clears its window association");
                Check(!Disposed(black),"Desktop validation leaves ownership of the input bitmap with its caller");
            }
            using(var sparse=Frame(Color.Black)) {
                sparse.SetPixel(1,1,Color.White); Call(capture,"ValidateFrame",sparse);
                Check(sparse.GetPixel(1,1).R==255,"A single visible pixel between sampling points is accepted by the production validator");
            }
            int attempts=0,resets=0;
            using(var result=Run(delegate(int budget) {
                attempts++;
                var frame=Frame(attempts==1?Color.Black:Color.CornflowerBlue);
                Set(capture,"lastFrame",(Bitmap)frame.Clone()); Set(capture,"lastFrameWindow",new IntPtr(18));
                try { Call(capture,"ValidateFrame",frame); return frame; } catch { frame.Dispose(); throw; }
            },delegate { resets++; Call(capture,"Clear"); },delegate {},CancellationToken.None)) {
                Check(attempts==2 && resets==1 && result.GetPixel(0,0).B>0,"Production black validation and recovery replace the rejected cache with a usable frame");
            }
        } finally { Dispose(capture); }
    }

    static void VerifyOptionalScreenshot() {
        int calls=0;
        object disabled=Take(delegate { calls++; return Frame(Color.White); },CancellationToken.None,false);
        try { Check(calls==0 && (string)Field(disabled,"status")=="disabled" && Field(disabled,"Image")==null,"Disabled screenshots never request pixels and do not report a capture failure"); } finally { Dispose(disabled); }

        object failed=Take(delegate { throw new COMException("synthetic permanent capture failure",unchecked((int)0x80070005)); },CancellationToken.None);
        try { Check((string)Field(failed,"status")=="failed" && (string)Field(failed,"failureStage")=="capture" && !(bool)Field(failed,"attached") && Field(failed,"Image")==null,"A capture failure remains optional input with no attachment, permitting voice-only processing"); } finally { Dispose(failed); }

        Bitmap black=Frame(Color.Black); failed=Take(()=>black,CancellationToken.None);
        try { Check((string)Field(failed,"status")=="failed" && Disposed(black),"The final optional-image guard rejects and disposes any remaining black frame"); } finally { Dispose(failed); }

        Bitmap sparse=Frame(Color.Black); sparse.SetPixel(1,1,Color.White); object usable=Take(()=>sparse,CancellationToken.None);
        try {
            string output=Path.Combine(fixture,"screen.png"); string saved=(string)Call(usable,"Save",output);
            using(var read=new Bitmap(output)) Check(saved==output && (bool)Field(usable,"attached") && read.GetPixel(1,1).R==255,"A sparse visible frame is attached and saved without a false black-image rejection");
            Check(Disposed(sparse),"Saving releases the source bitmap after writing the attachment");
        } finally { Dispose(usable); }

        Bitmap unsaved=Frame(Color.CornflowerBlue); failed=Take(()=>unsaved,CancellationToken.None);
        try {
            object path=Call(failed,"Save",Path.Combine(fixture,"missing-parent","screen.png"));
            Check(path==null && (string)Field(failed,"failureStage")=="save" && !(bool)Field(failed,"attached") && Disposed(unsaved),"A screenshot save failure also preserves voice-only processing and releases its bitmap");
        } finally { Dispose(failed); }

        using(var cancellation=new CancellationTokenSource()) {
            Bitmap interrupted=Frame(Color.White);
            Exception error=Catch(delegate { Dispose(Take(delegate { cancellation.Cancel(); return interrupted; },cancellation.Token)); });
            Check(error is OperationCanceledException && Disposed(interrupted),"Cancellation during optional capture propagates and disposes the returned image");
        }
    }

    [STAThread] static int Main(string[] args) {
        if(args.Length!=3) return 64;
        fixture=Path.GetFullPath(args[1]); string output=Path.GetFullPath(args[2]); bool passed=false;
        report["checks"]=checks; report["syntheticFramesOnly"]=true; report["nativeCaptureInvoked"]=false; report["microphoneOrNetworkUsed"]=false;
        try {
            if(Directory.Exists(fixture)) throw new InvalidOperationException("Use a new empty fixture directory.");
            Directory.CreateDirectory(Path.Combine(fixture,"runtime"));
            assembly=Assembly.LoadFrom(Path.GetFullPath(args[0])); report["executable"]=assembly.Location;
            recovery=assembly.GetType("Minus700K.CaptureRecovery",true); unavailable=assembly.GetType("Minus700K.CaptureFrameUnavailableException",true);
            desktop=assembly.GetType("Minus700K.DesktopCapture",true); optional=assembly.GetType("Minus700K.OptionalScreenshot",true);
            assembly.GetType("Minus700K.Log",true).GetField("Root",Static).SetValue(null,fixture);
            VerifyRecovery(); VerifyBlackFrameCache(); VerifyOptionalScreenshot(); passed=true;
        } catch(Exception error) { report["error"]=error.ToString(); }
        report["passed"]=passed;
        File.WriteAllText(output,new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        return passed?0:1;
    }
}

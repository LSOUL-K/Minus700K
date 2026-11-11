using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Minus700K;

internal static class SpeechModelHarness {
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic,Instance=BindingFlags.Instance|BindingFlags.NonPublic;
    static Assembly app=typeof(Settings).Assembly; static List<string> checks=new List<string>(); static string root;
    static void Check(bool value,string name) { if(!value) throw new Exception(name); checks.Add(name); }
    static object Invoke(object target,string method,params object[] args) {
        try { return target.GetType().GetMethod(method,Instance).Invoke(target,args); }
        catch(TargetInvocationException e) { throw e.InnerException; }
    }
    static object Call(string type,string method,params object[] args) {
        try { return app.GetType("Minus700K."+type).GetMethod(method,Static).Invoke(null,args); }
        catch(TargetInvocationException e) { throw e.InnerException; }
    }
    static string Hash(byte[] bytes) { using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
    static object FileFor(string path,Uri source,byte[] bytes) { return Activator.CreateInstance(app.GetType("Minus700K.VerifiedModelFile"),Instance,null,new object[]{path,source,(long)bytes.Length,Hash(bytes)},null); }
    static void Ensure(object file,CancellationToken token,Action<long,long> progress=null) { Invoke(file,"Ensure",progress,token); }
    static void Reject(Action action,string name) { bool failed=false; try { action(); } catch { failed=true; } Check(failed,name); }
    sealed class Server : IDisposable {
        readonly TcpListener listener; readonly Thread worker; volatile bool closed;
        internal int Requests; internal byte[] Payload; internal int DeclaredLength,Status=200,Pause;
        internal Uri Url;
        internal Server(byte[] data) {
            Payload=data; DeclaredLength=data.Length; listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
            Url=new Uri("http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/model");
            worker=new Thread(Run) { IsBackground=true }; worker.Start();
        }
        void Run() {
            while(!closed) try {
                using(var client=listener.AcceptTcpClient()) using(var stream=client.GetStream()) {
                    client.ReceiveTimeout=5000; Interlocked.Increment(ref Requests);
                    var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true); string line;
                    while(!string.IsNullOrEmpty(line=reader.ReadLine())) { }
                    if(Pause>0) Thread.Sleep(Pause);
                    byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 "+Status+" Test\r\nContent-Length: "+DeclaredLength+"\r\nConnection: close\r\n\r\n");
                    stream.Write(header,0,header.Length); stream.Write(Payload,0,Payload.Length);
                }
            } catch { if(closed) return; }
        }
        public void Dispose() { closed=true; listener.Stop(); worker.Join(5000); }
    }
    static void NetworkChecks() {
        byte[] bytes=new byte[150000]; new Random(42).NextBytes(bytes);
        using(var server=new Server(bytes)) {
            string path=Path.Combine(root,"download","model.bin"); var file=FileFor(path,server.Url,bytes); long received=0;
            Check(!File.Exists(path),"fresh model absent");
            Ensure(file,CancellationToken.None,(r,t)=>received=r);
            Check(Hash(File.ReadAllBytes(path))==Hash(bytes) && received==bytes.Length,"download verifies size/hash and reports completion");
            Check(!File.Exists(path+".download"),"completed download is atomically published");
            int requests=server.Requests; DateTime modified=File.GetLastWriteTimeUtc(path); server.Status=503;
            Ensure(file,CancellationToken.None);
            Check(server.Requests==requests && File.GetLastWriteTimeUtc(path)==modified,"later launch reuses model without a request or rewrite");
            File.WriteAllText(Path.Combine(root,"application-update.txt"),"replacement executable");
            Ensure(FileFor(path,server.Url,bytes),CancellationToken.None);
            Check(server.Requests==requests,"new application instance reuses downloaded model even with unavailable server");
            string bad=Path.Combine(root,"bad.bin"); var invalid=FileFor(bad,server.Url,bytes);
            Reject(()=>Ensure(invalid,CancellationToken.None),"HTTP failure is reported"); Check(!File.Exists(bad),"HTTP failure publishes no model");
            server.Status=200; server.Payload=(byte[])bytes.Clone(); server.Payload[5]^=1;
            Reject(()=>Ensure(invalid,CancellationToken.None),"same-length incorrect hash is rejected"); Check(!File.Exists(bad),"hash failure publishes no model");
            server.Payload=new byte[10]; server.DeclaredLength=bytes.Length;
            Reject(()=>Ensure(invalid,CancellationToken.None),"interrupted short response is rejected"); Check(!File.Exists(bad),"interruption publishes no model");
            server.DeclaredLength=10;
            Reject(()=>Ensure(invalid,CancellationToken.None),"wrong content length is rejected");
            server.Payload=bytes; server.DeclaredLength=bytes.Length;
            Ensure(invalid,CancellationToken.None);
            Check(Hash(File.ReadAllBytes(bad))==Hash(bytes),"retry replaces failed staging data with a verified model");
            File.WriteAllText(bad,"incomplete previous model"); Ensure(invalid,CancellationToken.None);
            Check(Hash(File.ReadAllBytes(bad))==Hash(bytes),"incomplete existing model is repaired");
            string cancelled=Path.Combine(root,"cancelled.bin"); server.Pause=1000;
            using(var token=new CancellationTokenSource()) {
                token.CancelAfter(150); var started=System.Diagnostics.Stopwatch.StartNew();
                bool cancelledCorrectly=false; try { Ensure(FileFor(cancelled,server.Url,bytes),token.Token); } catch(OperationCanceledException) { cancelledCorrectly=true; }
                Check(cancelledCorrectly && started.ElapsedMilliseconds<3000 && !File.Exists(cancelled),"cancel aborts a pending request promptly without publishing a model");
            }
            server.Pause=0;
            Ensure(FileFor(cancelled,server.Url,bytes),CancellationToken.None);
            Check(Hash(File.ReadAllBytes(cancelled))==Hash(bytes),"restart after cancellation completes successfully");
        }
    }
    static void ConfigurationChecks() {
        string fixture=Path.Combine(root,"settings"); Directory.CreateDirectory(fixture);
        var settings=new Settings();
        Check((bool)Call("SpeechModel","NeedsDownload",fixture,settings),"missing default model needs first-launch preparation");
        Call("SettingsStorage","Validate",settings,fixture); checks.Add("settings accept default model before first download");
        settings.WhisperModel="missing-custom.bin";
        Check(!(bool)Call("SpeechModel","NeedsDownload",fixture,settings),"custom model never triggers automatic replacement");
        Reject(()=>Call("SettingsStorage","Validate",settings,fixture),"missing custom path still fails settings validation");
        settings.WhisperModel=Path.Combine(fixture,"vendor","ggml-small-q5_1.bin");
        Check((bool)Call("SpeechModel","NeedsDownload",fixture,settings),"absolute default model path is recognized");
        settings.WhisperModel="vendor\\ggml-small-q5_1.bin";
        Check((bool)Call("SpeechModel","NeedsDownload",fixture,settings),"Windows separators preserve default model recognition");
    }
    static Form CreateForm(Func<Action<long,long>,CancellationToken,Task> download) {
        return (Form)Activator.CreateInstance(app.GetType("Minus700K.SpeechModelForm"),Instance,null,new object[]{download},null);
    }
    static void SaveImage(Form form,string name) { using(var bitmap=new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bitmap,form.ClientRectangle.Size==form.Size?form.ClientRectangle:new Rectangle(0,0,form.Width,form.Height)); bitmap.Save(Path.Combine(root,name+".png")); } }
    static void UiChecks(string language) {
        Call("L10n","SetLanguage",language); int calls=0;
        using(var form=CreateForm(async (progress,token)=>{
            int attempt=++calls; progress(50000000,190085487); await Task.Delay(250,token);
            if(attempt==1) throw new WebException("offline fixture");
        })) using(var timer=new System.Windows.Forms.Timer { Interval=50 }) {
            var deadline=System.Diagnostics.Stopwatch.StartNew(); bool captured=false,errorCaptured=false; string failure=null;
            timer.Tick+=delegate {
                if(deadline.ElapsedMilliseconds>5000) { failure="UI retry timed out"; form.Close(); return; }
                if(!captured && deadline.ElapsedMilliseconds>100) { SaveImage(form,"download-"+language); captured=true; }
                var retry=(Button)form.Controls.Find("ModelDownloadRetry",true)[0];
                if(retry.Visible && retry.Enabled) { SaveImage(form,"error-"+language); errorCaptured=true; retry.PerformClick(); }
            };
            timer.Start(); Application.Run(form); timer.Stop();
            Check(failure==null && captured && errorCaptured && calls==2 && (bool)form.GetType().GetProperty("Completed",Instance).GetValue(form,null),language+" download UI shows progress, error, retry and success");
        }
        bool cancelled=false;
        using(var form=CreateForm(async (progress,token)=>{
            try { await Task.Delay(10000,token); } catch(OperationCanceledException) { cancelled=true; throw; }
        })) using(var timer=new System.Windows.Forms.Timer { Interval=100 }) {
            timer.Tick+=delegate { timer.Stop(); form.Close(); }; timer.Start(); Application.Run(form);
            Check(cancelled && !(bool)form.GetType().GetProperty("Completed",Instance).GetValue(form,null),language+" closing cancels pending download and prevents startup");
        }
    }
    [STAThread] static int Main(string[] args) {
        root=args[0]; Directory.CreateDirectory(root); string error=null;
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>1 && args[1]=="live") {
                object[] values={root,new Settings(),true};
                Check((bool)Call("SpeechModelForm","Prepare",values),"first-launch setup downloads the official model");
                string model=Path.Combine(root,"vendor","ggml-small-q5_1.bin");
                using(var hash=SHA256.Create()) using(var stream=File.OpenRead(model)) Check(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant()=="ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb","official model SHA-256 verified independently");
                DateTime modified=File.GetLastWriteTimeUtc(model);
                Check((bool)Call("SpeechModelForm","Prepare",values) && File.GetLastWriteTimeUtc(model)==modified,"second startup skips download UI and retains model");
            } else { NetworkChecks(); ConfigurationChecks(); UiChecks("ja"); UiChecks("en"); }
        } catch(Exception e) { error=e.ToString(); }
        File.WriteAllText(Path.Combine(root,"report.json"),new JavaScriptSerializer().Serialize(new {passed=error==null,checks=checks,error=error}),new UTF8Encoding(false));
        return error==null?0:1;
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Minus700K {
internal static class CodexStorageDiagnostics {
    static int checks;
    static void Check(bool value,string message) { checks++; if(!value) throw new Exception("Codex storage: "+message); }
    static object JsonObject(object value) { return JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(value)); }
    static void Reject(Action action,string message) { bool rejected=false; try { action(); } catch(PlanException e) { rejected=e.Code=="conversation_storage"; } Check(rejected,message); }
    internal static void Run(string root,Settings settings,Dictionary<string,object> report) {
        checks=0;
        string fixtureName="M7-codex-"+Guid.NewGuid().ToString("N"),fixture=Path.Combine(Path.GetTempPath(),fixtureName);
        string originalHome=Environment.GetEnvironmentVariable("CODEX_HOME"),originalDb=Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
        try {
            string target=Path.Combine(fixture,"専用 home");
            var start=new ProcessStartInfo { Arguments="--no-daemon app-server" };
            foreach(string key in new[]{"CODEX_HOME","CODEX_SQLITE_HOME","CODEX_THREAD_ID","CODEX_ROLLOUT_TRACE_ROOT","CODEX_TUI_RECORD_SESSION","CODEX_TUI_SESSION_LOG_PATH"}) start.EnvironmentVariables[key]="unrelated-parent";
            start.EnvironmentVariables[CodexPlan.TokenVariable]="fixture-oauth";
            CodexStorage.Apply(start,target);
            Check(start.EnvironmentVariables["CODEX_HOME"]==target && start.EnvironmentVariables["CODEX_SQLITE_HOME"]==target,"home and database use this isolated request");
            foreach(string key in new[]{"CODEX_THREAD_ID","CODEX_ROLLOUT_TRACE_ROOT","CODEX_TUI_RECORD_SESSION","CODEX_TUI_SESSION_LOG_PATH"}) Check(!start.EnvironmentVariables.ContainsKey(key),"no inherited transcript destination: "+key);
            Check(start.EnvironmentVariables[CodexPlan.TokenVariable]=="fixture-oauth","explicit plan bearer retained");
            Check(start.EnvironmentVariables["RUST_LOG"]=="off","native text diagnostics disabled");
            Check(Environment.GetEnvironmentVariable("CODEX_HOME")==originalHome && Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME")==originalDb,"parent environment unchanged");
            CodexStorage.ValidateConfig(JsonObject(new { sqlite_home=target,log_dir=Path.Combine(target,"log"),history=new { persistence="none" } }),target);
            Reject(()=>CodexStorage.ValidateConfig(JsonObject(new { sqlite_home=target,log_dir=Path.Combine(target,"log"),history=new { persistence="save-all" } }),target),"history persistence cannot be enabled");
            CodexStorage.ValidateThread(JsonObject(new { ephemeral=true,path=(string)null }));
            Reject(()=>CodexStorage.ValidateThread(JsonObject(new { ephemeral=false,path="saved.jsonl" })),"persistent response rejected before question input");
            Reject(()=>CodexStorage.ValidateThread(JsonObject(new { ephemeral=true,path="saved.jsonl" })),"materialized ephemeral response rejected");
            Reject(()=>CodexStorage.ValidateThread(JsonObject(new { id="unsupported" })),"unsupported protocol fails closed");
            Reject(()=>CodexLogGuard.Install(target),"missing logging database fails closed");
            string codex=AssistantService.FindCodex(settings.CodexPath);
            NativeStorage(codex,fixture,report);
            string temporary;
            using(var client=new CodexClient(codex,fixture,CancellationToken.None)) {
                temporary=client.StorageHome; Check(Directory.Exists(temporary),"production client creates a temporary home");
                Reject(()=>client.Call("thread/start",new { ephemeral=false },CancellationToken.None),"production transport rejects persistent requests");
                foreach(string method in new[]{"thread/read","thread/resume","thread/fork","thread/turns/list"}) {
                    bool forbidden=false; try { client.Call(method,new { },CancellationToken.None); } catch(InvalidOperationException) { forbidden=true; }
                    Check(forbidden,"production transport cannot access saved history: "+method);
                }
            }
            Check(!Directory.Exists(temporary),"production client removes its home after normal disposal");
            using(var canceled=new CancellationTokenSource()) {
                canceled.Cancel(); string failed=CodexStorage.CreateSessionHome(); bool canceledBeforeInput=false;
                try { using(var client=new CodexClient(codex,fixture,canceled.Token,storageHome:failed)) { } } catch(OperationCanceledException) { canceledBeforeInput=true; }
                Check(canceledBeforeInput,"canceled client never accepts question input");
                CodexStorage.RemoveSessionHome(failed); Check(!Directory.Exists(failed),"canceled fixture cleanup");
            }
            report["checks"]=checks; report["inferenceRequests"]=0; report["localSyntheticRequests"]=3; report["personalHistoryRead"]=false;
        } finally {
            string resolved=Path.GetFullPath(fixture),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\','/');
            Check(string.Equals(Path.GetDirectoryName(resolved),temp,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved)==fixtureName,"fixture cleanup stays inside its temporary directory");
            if(Directory.Exists(resolved)) Directory.Delete(resolved,true);
        }
    }
    static void NativeStorage(string codex,string fixture,Dictionary<string,object> report) {
        string home=Path.Combine(fixture,"native"),work=Path.Combine(fixture,"workspace"); Directory.CreateDirectory(work);
        const string question="M7_PRIVATE_QUESTION_908fa0354d9e4ec78dc32545b8a3a5fd",answer="M7_PRIVATE_ANSWER_a83f8be95ae64c78badcd801ee9cba86";
        string screenshot=Path.Combine(work,"synthetic-screen.png");
        using(var bitmap=new Bitmap(64,64)) { using(Graphics graphics=Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue); bitmap.Save(screenshot,ImageFormat.Png); }
        string thread;
        using(var server=new ResponsesFixture(answer)) using(var client=new SyntheticClient(codex,work,home,server.Url)) {
            thread=client.StartThread(work);
            client.Call("turn/start",new { threadId=thread,input=new object[]{new { type="localImage",path=screenshot },new { type="text",text=question }} });
            bool completed=false,sawAnswer=false;
            for(int i=0;i<1000 && !completed;i++) {
                object message=client.Receive(); string method=JsonData.Text(message,"method"); object data=JsonData.Get(message,"params");
                if(JsonData.Text(data,"threadId")!=thread) continue;
                if(method=="item/completed" && JsonData.Text(JsonData.Get(data,"item"),"text")==answer) sawAnswer=true;
                if(method=="turn/completed") { Check(JsonData.Text(JsonData.Get(data,"turn"),"status")=="completed","local synthetic answer completes"); completed=true; }
            }
            Check(completed && sawAnswer && server.Request.Contains(question),"question and answer traverse real native transport in memory");
            Check(server.Request.Contains("input_image"),"synthetic screenshot reaches the local provider");
            // The native log batch flush interval is ten seconds. Inspect while
            // alive so deletion cannot conceal a privacy leak.
            Thread.Sleep(11000);
            Check(!JsonData.Items(JsonData.Get(client.Call("thread/list",new { limit=100 }),"data")).Any(t=>JsonData.Text(t,"id")==thread),"ephemeral thread is absent from stored inventory");
            AssertNoConversation(home,question,answer);
        }
        AssertNoConversation(home,question,answer);
        Check(Directory.GetFiles(home,"*",SearchOption.AllDirectories).All(p=>!new[]{".png",".jpg",".jpeg",".webp"}.Contains(Path.GetExtension(p).ToLowerInvariant())),"native home retains no screenshot attachment");
        Check(!Directory.Exists(Path.Combine(home,"sessions")) || Directory.GetFiles(Path.Combine(home,"sessions"),"*",SearchOption.AllDirectories).Length==0,"no native rollout created");
        using(var reader=new SyntheticClient(codex,work,Path.Combine(fixture,"other"),"http://127.0.0.1:1/v1")) Check(!JsonData.Items(JsonData.Get(reader.Call("thread/list",new { limit=100 }),"data")).Any(),"next request begins with no stored conversation");
        foreach(string outcome in new[]{"failure","canceled"}) {
            string interruptedHome=Path.Combine(fixture,outcome);
            using(var server=new ResponsesFixture(answer,outcome)) {
                using(var client=new SyntheticClient(codex,work,interruptedHome,server.Url)) {
                    string interrupted=client.StartThread(work);
                    client.Call("turn/start",new { threadId=interrupted,input=new[]{new { type="text",text=question }} });
                    Check(SpinWait.SpinUntil(()=>server.Request.Contains(question),20000),outcome+" fixture receives the synthetic question");
                    if(outcome=="failure") {
                        bool failed=false;
                        for(int i=0;i<1000 && !failed;i++) {
                            object notice=client.Receive(),data=JsonData.Get(notice,"params");
                            if(JsonData.Text(notice,"method")=="turn/completed" && JsonData.Text(data,"threadId")==interrupted) { Check(JsonData.Text(JsonData.Get(data,"turn"),"status")=="failed","synthetic provider error propagates"); failed=true; }
                        }
                        Check(failed,"failed native turn completes");
                    }
                    // Disposal during a pending response matches request cancellation.
                    AssertNoConversation(interruptedHome,question,answer);
                }
                AssertNoConversation(interruptedHome,question,answer);
            }
        }
        report["ephemeralNotPersisted"]=true; report["diagnosticConversationWritesBlocked"]=true;
    }
    static void AssertNoConversation(string home,params string[] sentinels) {
        foreach(string path in Directory.GetFiles(home,"*",SearchOption.AllDirectories)) {
            byte[] bytes;
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) using(var buffer=new MemoryStream()) { file.CopyTo(buffer); bytes=buffer.ToArray(); }
            string content=Encoding.UTF8.GetString(bytes);
            Check(sentinels.All(s=>!content.Contains(s)),"conversation absent from "+Path.GetFileName(path));
        }
    }
    // This loopback Responses endpoint returns fixed text without external AI.
    sealed class ResponsesFixture : IDisposable {
        readonly TcpListener listener; readonly Task worker; readonly ManualResetEvent release=new ManualResetEvent(false); internal string Request=""; internal readonly string Url;
        internal ResponsesFixture(string answer,string outcome="completed") {
            listener=new TcpListener(IPAddress.Loopback,0); listener.Start(); Url="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/v1";
            worker=Task.Run(delegate {
                using(TcpClient client=listener.AcceptTcpClient()) using(NetworkStream stream=client.GetStream()) {
                    stream.ReadTimeout=20000; stream.WriteTimeout=20000;
                    var bytes=new List<byte>(); int value;
                    while((value=stream.ReadByte())>=0) { bytes.Add((byte)value); int n=bytes.Count; if(n>=4 && bytes[n-4]==13 && bytes[n-3]==10 && bytes[n-2]==13 && bytes[n-1]==10) break; }
                    string headers=Encoding.ASCII.GetString(bytes.ToArray()); int length=0;
                    foreach(string line in headers.Split('\n')) if(line.StartsWith("Content-Length:",StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(),out length);
                    byte[] body=new byte[length]; int offset=0;
                    while(offset<length) { int read=stream.Read(body,offset,length-offset); if(read==0) break; offset+=read; }
                    Request=Encoding.UTF8.GetString(body);
                    if(outcome=="canceled") { release.WaitOne(30000); return; }
                    if(outcome=="failure") {
                        byte[] failure=Encoding.UTF8.GetBytes(JsonData.Serializer().Serialize(new { error=new { message=answer,type="invalid_request_error" } }));
                        byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: "+failure.Length+"\r\nConnection: close\r\n\r\n");
                        stream.Write(header,0,header.Length); stream.Write(failure,0,failure.Length); stream.Flush(); return;
                    }
                    var message=new { type="message",id="msg_fixture",role="assistant",phase="final_answer",status="completed",content=new[]{new { type="output_text",text=answer,annotations=new object[0] }} };
                    string events=Event(new { type="response.created",response=new { id="resp_fixture",status="in_progress",output=new object[0] } })+Event(new { type="response.output_item.done",output_index=0,item=message })+Event(new { type="response.completed",response=new { id="resp_fixture",status="completed",output=new[]{message},usage=new { input_tokens=1,output_tokens=1,total_tokens=2 } } });
                    byte[] response=Encoding.UTF8.GetBytes(events),head=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: "+response.Length+"\r\nConnection: close\r\n\r\n");
                    stream.Write(head,0,head.Length); stream.Write(response,0,response.Length); stream.Flush();
                }
            });
        }
        static string Event(object value) { return "data: "+JsonData.Serializer().Serialize(value)+"\n\n"; }
        public void Dispose() { release.Set(); listener.Stop(); try { worker.Wait(1000); } catch(AggregateException) { } release.Dispose(); }
    }
    sealed class SyntheticClient : IDisposable {
        Process process; ProcessJob job; StreamWriter input; CodexLogGuard guard; int nextId;
        internal SyntheticClient(string codex,string work,string home,string url) {
            try {
                var args=new List<string>(CodexPlan.Options("disabled"));
                foreach(string entry in new[] { "model_provider=\"fixture\"","model=\"gpt-6.1-sol\"","model_providers.fixture.name=\"Offline fixture\"","model_providers.fixture.base_url="+JsonData.Serializer().Serialize(url),"model_providers.fixture.wire_api=\"responses\"","model_providers.fixture.requires_openai_auth=false","model_providers.fixture.supports_websockets=false","model_providers.fixture.request_max_retries=0","model_providers.fixture.stream_max_retries=0" }) { args.Add("-c"); args.Add(entry); }
                var start=new ProcessStartInfo(codex,string.Join(" ",args.Select(HiddenProcess.Quote).ToArray())) { WorkingDirectory=work,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
                CodexPlan.Environment(start,null); CodexStorage.Apply(start,home);
                job=new ProcessJob(); process=new Process { StartInfo=start }; process.ErrorDataReceived+=delegate { };
                process.Start(); job.Add(process); process.BeginErrorReadLine(); input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)) { AutoFlush=true };
                Call("initialize",new { clientInfo=new { name="minus700k_storage_fixture",version="2.0" } }); Send(new { method="initialized" }); guard=CodexLogGuard.Install(home);
            } catch { Dispose(); throw; }
        }
        internal string StartThread(string work) {
            object response=Call("thread/start",new { model="gpt-6.1-sol",modelProvider="fixture",cwd=work,approvalPolicy="never",sandbox="read-only",ephemeral=true,baseInstructions="Local synthetic storage diagnostic. Do not execute tools." });
            object thread=JsonData.Get(response,"thread"); CodexStorage.ValidateThread(thread);
            string id=JsonData.Text(thread,"id"); Guid parsed; Check(Guid.TryParse(id,out parsed),"valid ephemeral thread id"); return id;
        }
        void Send(object message) { input.WriteLine(JsonData.Serializer().Serialize(message)); }
        internal object Receive() {
            Task<string> pending=process.StandardOutput.ReadLineAsync(); if(!pending.Wait(20000)) throw new TimeoutException("Synthetic storage RPC timed out.");
            string line=pending.GetAwaiter().GetResult(); if(line==null) throw new IOException("Synthetic storage process closed.");
            object response=JsonData.Serializer().DeserializeObject(line);
            if(JsonData.Get(response,"method")!=null && JsonData.Get(response,"id")!=null) throw new IOException("Synthetic storage unexpectedly requested a client operation."); return response;
        }
        internal object Call(string method,object parameters) {
            if(method!="initialize" && method!="thread/start" && method!="turn/start" && method!="thread/list") throw new InvalidOperationException("Synthetic fixture RPC is not allowed.");
            int id=++nextId; Send(new { id=id,method=method,@params=parameters });
            for(int i=0;i<1000;i++) { object response=Receive(); if(JsonData.Text(response,"id")!=id.ToString()) continue;
                object error=JsonData.Get(response,"error"); if(error!=null) throw new IOException("Synthetic storage RPC failed: "+method+" ("+JsonData.Text(error,"code")+")"); return JsonData.Get(response,"result"); }
            throw new TimeoutException("Synthetic storage RPC timed out: "+method);
        }
        public void Dispose() {
            try { if(input!=null) input.Dispose(); } catch { }
            if(process!=null) { try { if(!process.HasExited) process.WaitForExit(2000); } catch { } }
            if(job!=null) { job.Dispose(); job=null; }
            if(process!=null) { try { if(!process.HasExited) process.WaitForExit(2000); } catch { } process.Dispose(); process=null; }
            if(guard!=null) { guard.Dispose(); guard=null; }
        }
    }
}
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Minus700K {
internal static class JsonData {
    internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=32*1024*1024,RecursionLimit=150 }; }
    internal static Dictionary<string,object> Object(object value) { return value as Dictionary<string,object>??new Dictionary<string,object>(); }
    internal static object Get(object value,string key) { object result; return Object(value).TryGetValue(key,out result)?result:null; }
    internal static string Text(object value,string key) { return Convert.ToString(Get(value,key))??""; }
    internal static IEnumerable<object> Items(object value) { return value is IEnumerable && !(value is string) && !(value is IDictionary)?((IEnumerable)value).Cast<object>():Enumerable.Empty<object>(); }
}
// Local stdio transport for fresh, non-persistent question threads only.
internal sealed class CodexClient : IDisposable {
    readonly Process process; readonly ProcessJob job; readonly StreamWriter input;
    readonly Queue<Dictionary<string,object>> notifications=new Queue<Dictionary<string,object>>();
    readonly PlanAccount account;
    readonly bool temporaryHome; CodexLogGuard logs;
    internal readonly string StorageHome;
    int nextId; bool disposed;
    internal CodexClient(string codex,string root,CancellationToken token,PlanAccount account=null,string search="disabled",string catalog=null,string storageHome=null) {
        this.account=account; temporaryHome=storageHome==null;
        StorageHome=Path.GetFullPath(storageHome??CodexStorage.CreateSessionHome());
        job=new ProcessJob();
        try {
            string[] args=CodexPlan.Options(search,catalog);
            ProcessStartInfo start=new ProcessStartInfo(codex,string.Join(" ",args.Select(HiddenProcess.Quote).ToArray())) {
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            CodexPlan.Environment(start,account);
            CodexStorage.Apply(start,StorageHome);
            process=new Process { StartInfo=start };
            process.ErrorDataReceived+=delegate { }; // Drain stderr; never copy unrelated Codex diagnostics into app logs.
            process.Start(); job.Add(process); process.BeginErrorReadLine();
            try { process.PriorityClass=ProcessPriorityClass.BelowNormal; } catch { }
            input=new StreamWriter(process.StandardInput.BaseStream,new UTF8Encoding(false)) { AutoFlush=true };
            Call("initialize",new { clientInfo=new { name=ChatGptPlan.AppName,title=ChatGptPlan.AppName,version="2.0" },capabilities=new { explicitGatewayOauth=true } },token);
            Send(new { method="initialized" });
            logs=CodexLogGuard.Install(StorageHome);
        } catch { Dispose(); throw; }
    }
    void Send(object message) { input.WriteLine(JsonData.Serializer().Serialize(message)); }
    Dictionary<string,object> Receive(CancellationToken token,int seconds) {
        Stopwatch elapsed=Stopwatch.StartNew(); Task<string> line=process.StandardOutput.ReadLineAsync();
        while(!line.IsCompleted) { token.ThrowIfCancellationRequested(); if(elapsed.Elapsed.TotalSeconds>seconds) throw new TimeoutException(L10n.T("messages.CodexHistory.001")); Thread.Sleep(20); }
        token.ThrowIfCancellationRequested(); string text=line.GetAwaiter().GetResult();
        if(text==null) throw new IOException(L10n.T("messages.CodexHistory.002"));
        if(text.Length>32*1024*1024) throw new IOException(L10n.T("messages.CodexHistory.003"));
        if(account!=null) text=CodexPlan.Redact(text,account);
        var message=JsonData.Object(JsonData.Serializer().DeserializeObject(text));
        if(JsonData.Get(message,"id")!=null && JsonData.Get(message,"method")!=null) {
            Send(new { id=JsonData.Get(message,"id"),error=new { code=-32601,message="Minus700K does not permit local tool execution or interactive approvals." } });
            throw new PlanException("unexpected_tool",L10n.T("messages.CodexHistory.004"));
        }
        return message;
    }
    internal Dictionary<string,object> Notification(CancellationToken token,int seconds) {
        if(notifications.Count>0) return notifications.Dequeue();
        return Receive(token,seconds);
    }
    internal Dictionary<string,object> Call(string method,object parameters,CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if(disposed) throw new ObjectDisposedException("CodexClient");
        if(method!="initialize" && method!="config/read" && method!="thread/start" && method!="turn/start" && method!="mcpServerStatus/list") throw new InvalidOperationException("Unsupported question RPC.");
        if(method=="thread/start") {
            object requested=JsonData.Serializer().DeserializeObject(JsonData.Serializer().Serialize(parameters));
            if(!true.Equals(JsonData.Get(requested,"ephemeral"))) throw CodexStorage.PrivacyError();
        }
        int id=++nextId;
        Send(new { id=id,method=method,@params=parameters });
        Stopwatch elapsed=Stopwatch.StartNew();
        while(true) {
            if(elapsed.Elapsed.TotalSeconds>15) throw new TimeoutException(L10n.T("messages.CodexHistory.001"));
            var message=Receive(token,15);
            object responseId=JsonData.Get(message,"id");
            if(responseId==null) { if(notifications.Count<512) notifications.Enqueue(message); continue; }
            if(Convert.ToString(responseId)!=id.ToString()) continue;
            object error=JsonData.Get(message,"error");
            if(error!=null) throw CodexPlan.Error(error);
            if(method=="thread/start") CodexStorage.ValidateThread(JsonData.Get(JsonData.Get(message,"result"),"thread"));
            return JsonData.Object(JsonData.Get(message,"result"));
        }
    }
    internal Dictionary<string,object> QuestionConfig(string root,CancellationToken token) {
        // app-server merges user TOML maps: an empty mcp_servers table alone does not
        // clear inherited servers. Explicitly disable every entry before starting a turn.
        object config=JsonData.Get(Call("config/read",new { includeLayers=false,cwd=root },token),"config");
        CodexStorage.ValidateConfig(config,StorageHome);
        object provider=JsonData.Get(JsonData.Get(config,"model_providers"),"minus700k_chatgpt_plan");
        if(JsonData.Text(config,"model_provider")!="minus700k_chatgpt_plan" ||
           JsonData.Text(provider,"base_url")!=PlanHttp.Resource || JsonData.Text(provider,"env_key")!=CodexPlan.TokenVariable ||
           !false.Equals(JsonData.Get(provider,"requires_openai_auth")) || !false.Equals(JsonData.Get(provider,"supports_websockets")) ||
           JsonData.Get(provider,"auth")!=null || JsonData.Get(provider,"experimental_bearer_token")!=null || JsonData.Get(provider,"gateway_oauth")!=null)
            throw new PlanException("provider_mismatch",L10n.T("messages.CodexHistory.011"));
        var disabled=new Dictionary<string,object>();
        foreach(string name in JsonData.Object(JsonData.Get(config,"mcp_servers")).Keys) disabled[name]=new { enabled=false };
        return new Dictionary<string,object> { {"mcp_servers",disabled} };
    }
    public void Dispose() {
        if(disposed) return; disposed=true;
        try { if(input!=null) input.Dispose(); } catch { }
        if(process!=null) { try { if(!process.HasExited) process.WaitForExit(1000); } catch { } }
        if(job!=null) job.Dispose();
        if(process!=null) { try { if(!process.HasExited) process.WaitForExit(1000); } catch { } process.Dispose(); }
        if(logs!=null) { logs.Dispose(); logs=null; }
        if(temporaryHome) CodexStorage.RemoveSessionHome(StorageHome);
    }
}
}

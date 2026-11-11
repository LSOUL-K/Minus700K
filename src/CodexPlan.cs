using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Minus700K {
internal static class CodexPlan {
    internal const string TokenVariable="MINUS700K_CHATGPT_ACCESS_TOKEN";
    internal static string[] Options(string search,string catalog=null) {
        var args=new List<string> { "--no-daemon","--ask-for-approval","never","app-server","--listen","stdio://" };
        foreach(string entry in new[] {
            "model_provider=\"minus700k_chatgpt_plan\"",
            "model_providers.minus700k_chatgpt_plan.name=\"ChatGPT plan\"",
            "model_providers.minus700k_chatgpt_plan.base_url=\"https://api.openai.com/v1\"",
            "model_providers.minus700k_chatgpt_plan.env_key=\""+TokenVariable+"\"",
            "model_providers.minus700k_chatgpt_plan.wire_api=\"responses\"",
            "model_providers.minus700k_chatgpt_plan.requires_openai_auth=false",
            "model_providers.minus700k_chatgpt_plan.supports_websockets=false",
            "model_reasoning_effort=\"low\"","project_doc_max_bytes=0","web_search=\""+search+"\"",
            "sandbox_mode=\"read-only\"","approval_policy=\"never\"","mcp_servers={}","skills.include_instructions=false","skills.bundled.enabled=false","analytics.enabled=false",
            "history.persistence=\"none\"","feedback.enabled=false","otel.exporter=\"none\"","otel.trace_exporter=\"none\"","otel.metrics_exporter=\"none\"","otel.log_user_prompt=false"
        }) { args.Add("-c"); args.Add(entry); }
        foreach(string feature in new[] { "shell_tool","computer_use","browser_use","browser_use_external","in_app_browser","apps","plugins","remote_plugin","hooks","multi_agent","image_generation","goals","memories","shell_snapshot","skill_search","skill_mcp_dependency_install","sleep_tool","tool_suggest","code_mode","code_mode_host" }) { args.Add("--disable"); args.Add(feature); }
        args.Add("--enable"); args.Add("skip_host_skill_discovery");
        if(catalog!=null) { args.Add("-c"); args.Add("model_catalog_json="+JsonData.Serializer().Serialize(catalog)); }
        return args.ToArray();
    }
    internal static void Environment(ProcessStartInfo start,PlanAccount account) {
        foreach(string key in new[]{"OPENAI_API_KEY","CODEX_API_KEY","CODEX_ACCESS_TOKEN","OPENAI_BASE_URL","OPENAI_ORGANIZATION","OPENAI_PROJECT",TokenVariable}) start.EnvironmentVariables.Remove(key);
        if(account!=null) start.EnvironmentVariables[TokenVariable]=account.AccessToken;
    }
    internal static string Redact(string value,PlanAccount account) {
        foreach(string secret in new[]{account.AccessToken,account.RefreshToken,account.IdToken}) if(!string.IsNullOrEmpty(secret)) value=value.Replace(secret,"[credential redacted]");
        return value;
    }
    internal static Exception Error(object error,Localizer locale=null) {
        locale=locale??L10n.Current;
        string text=JsonData.Serializer().Serialize(error);
        Match code=Regex.Match(text,@"subscription_sharing_[a-z_]+|chatpass_v2_[a-z_]+|invalid_grant|invalid_refresh_token");
        if(code.Success) return PlanException.FromCode(code.Value,code.Value.Contains("limit")?429:code.Value.Contains("eligible")?403:0);
        if(text.Contains("401 Unauthorized")) return PlanException.FromCode("http_401",401);
        if(text.Contains("429 Too Many")) return PlanException.FromCode("http_429",429);
        // Only a concise code/type is logged; upstream error bodies may contain request data.
        Log.Write("Codex request failed");
        return new PlanException("codex_request_failed",locale.Text("errors.codex.answer"));
    }
    internal static AnswerDraft Run(string codex,string root,Settings settings,string prompt,string image,SearchDecision search,bool assess,PlanAccount account,PlanModel model,CancellationToken token) {
        Localizer locale=L10n.For(settings.AppLanguage);
        using(CancellationTokenSource deadline=CancellationTokenSource.CreateLinkedTokenSource(token)) {
            deadline.CancelAfter(settings.RequestTimeoutSeconds*1000);
            try {
                using(PlanCatalog catalog=new PlanCatalog(model))
                using(CodexClient client=new CodexClient(codex,root,deadline.Token,account,search.effectiveMode,catalog.Path)) {
                    object response=client.Call("thread/start",new { model=model.Slug,modelProvider="minus700k_chatgpt_plan",cwd=root,approvalPolicy="never",sandbox="read-only",ephemeral=true,config=client.QuestionConfig(root,deadline.Token),
                        baseInstructions=locale.Text("prompts.codexBase")+"\n"+locale.Text("prompts.answerLanguage"),developerInstructions=locale.Text("prompts.codexDeveloper") },deadline.Token);
                    string thread=JsonData.Text(JsonData.Get(response,"thread"),"id"); Guid id; if(!Guid.TryParse(thread,out id)) throw new IOException(locale.Text("errors.codex.threadId"));
                    if(JsonData.Text(response,"modelProvider")!="minus700k_chatgpt_plan") throw new PlanException("provider_mismatch",locale.Text("errors.codex.provider"));
                    var input=new List<object>();
                    if(!string.IsNullOrEmpty(image)) input.Add(new { type="localImage",path=Path.GetFullPath(image) });
                    input.Add(new { type="text",text=prompt });
                    var turnParameters=new Dictionary<string,object> { {"threadId",thread},{"input",input},{"effort","low"} };
                    if(assess) turnParameters["outputSchema"]=JsonData.Serializer().DeserializeObject(SearchFallback.Schema);
                    object started=client.Call("turn/start",turnParameters,deadline.Token); string turnId=JsonData.Text(JsonData.Get(started,"turn"),"id");
                    var answers=new Dictionary<string,string>(); string final=""; bool web=false;
                    while(true) {
                        object notice=client.Notification(deadline.Token,settings.RequestTimeoutSeconds); string method=JsonData.Text(notice,"method"); object data=JsonData.Get(notice,"params");
                        if(JsonData.Text(data,"threadId")!=thread) continue;
                        string eventTurn=JsonData.Text(data,"turnId"); if(eventTurn.Length>0 && turnId.Length>0 && eventTurn!=turnId) continue;
                        if(method=="item/completed") {
                            object item=JsonData.Get(data,"item"); string type=JsonData.Text(item,"type");
                            if(type=="webSearch") Log.Write("ChatGPT plan hosted web search completed");
                            if(type=="webSearch") web=true;
                            if(type=="agentMessage") { string text=JsonData.Text(item,"text"); answers[JsonData.Text(item,"id")]=text; if(JsonData.Text(item,"phase")=="final_answer") final=text; }
                        }
                        if(method!="turn/completed") continue;
                        object turn=JsonData.Get(data,"turn"); if(turnId.Length>0 && JsonData.Text(turn,"id")!=turnId) continue;
                        if(JsonData.Text(turn,"status")!="completed") throw Error(JsonData.Get(turn,"error"),locale);
                        foreach(object item in JsonData.Items(JsonData.Get(turn,"items"))) {
                            if(JsonData.Text(item,"type")=="webSearch") web=true;
                            if(JsonData.Text(item,"type")=="agentMessage") { string text=JsonData.Text(item,"text"); answers[JsonData.Text(item,"id")]=text; if(JsonData.Text(item,"phase")=="final_answer") final=text; }
                        }
                        if(final.Length==0) final=answers.Values.LastOrDefault()??"";
                        if(string.IsNullOrWhiteSpace(final)) throw new IOException(locale.Text("errors.codex.emptyAnswer"));
                        AnswerDraft draft=assess?SearchFallback.Parse(final,locale):new AnswerDraft { answer=final.Trim(),status="not_assessed",reason="" };
                        draft.ThreadId=thread; draft.Assessment=assess; draft.WebSearchCalled=web; return draft;
                    }
                }
            } catch(OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new TimeoutException(locale.Text("errors.codex.timeout")); }
        }
    }
}
internal sealed class PlanCatalog : IDisposable {
    internal readonly string Path;
    internal PlanCatalog(PlanModel model) {
        var entry=new Dictionary<string,object>(model.CatalogEntry);
        // This route uses full Responses with hosted web_search. A Codex desktop
        // catalog may prefer Responses Lite and consequently omit hosted tools.
        entry["use_responses_lite"]=false;
        entry["supports_search_tool"]=false; // Responses tool_search is unsupported by plan sharing.
        Path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Minus700K-model-"+Guid.NewGuid().ToString("N")+".json");
        File.WriteAllText(Path,JsonData.Serializer().Serialize(new { models=new[]{entry} }),new System.Text.UTF8Encoding(false));
    }
    public void Dispose() { try { if(File.Exists(Path)) File.Delete(Path); } catch { } }
}
}

using System;
using System.Threading;
using System.Web.Script.Serialization;

namespace Minus700K {
internal sealed class AnswerDraft {
    public string answer,status,reason;
    internal bool WebSearchCalled;
    internal string ThreadId;
    internal bool Assessment;
}
internal sealed class SearchFallbackReport {
    public bool eligible,attempted,webSearchCalled,initialWebSearchCalled;
    public string initialStatus,reason,outcome,error;
}
internal sealed class AnswerResult {
    internal string Answer;
    internal SearchDecision Search;
    internal SearchFallbackReport Fallback;
    internal int RecentMemoryCount;
    internal string ThreadId;
    internal bool Assessment;
    internal bool WebSearchCalled;
}
internal static class SearchFallback {
    internal const string Schema="{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"},\"status\":{\"type\":\"string\",\"enum\":[\"answered\",\"needs_web\",\"needs_clarification\"]},\"reason\":{\"type\":\"string\"}},\"required\":[\"answer\",\"status\",\"reason\"],\"additionalProperties\":false}";

    internal static bool Eligible(Settings settings,SearchDecision search) {
        return settings.AutoWebSearchFallback && settings.WebSearch=="on-demand" && !search.Enabled && search.reason=="default_image_only";
    }
    internal static AnswerDraft Parse(string json,Localizer locale=null) {
        locale=locale??L10n.Current;
        AnswerDraft draft=new JavaScriptSerializer().Deserialize<AnswerDraft>(json);
        if(draft==null || string.IsNullOrWhiteSpace(draft.answer) || draft.reason==null || (draft.status!="answered" && draft.status!="needs_web" && draft.status!="needs_clarification")) throw new Exception(locale.Text("errors.assessmentData"));
        draft.answer=draft.answer.Trim(); return draft;
    }
    internal static AnswerResult Run(Settings settings,SearchDecision initial,Func<SearchDecision,bool,AnswerDraft> attempt,CancellationToken token,bool allowFallback=true) {
        Localizer locale=L10n.For(settings.AppLanguage);
        token.ThrowIfCancellationRequested();
        bool eligible=allowFallback && Eligible(settings,initial);
        AnswerDraft first=attempt(initial,eligible);
        token.ThrowIfCancellationRequested();
        SearchFallbackReport report=new SearchFallbackReport { eligible=eligible,initialStatus=first.status,reason=first.reason,outcome=eligible?"not_needed":"disabled",error="",initialWebSearchCalled=first.WebSearchCalled };
        AnswerResult result=new AnswerResult { Answer=first.answer,Search=initial,Fallback=report,ThreadId=first.ThreadId,Assessment=first.Assessment,WebSearchCalled=first.WebSearchCalled };
        if(!eligible || first.status!="needs_web") return result;
        report.attempted=true;
        SearchDecision retry=new SearchDecision { configuredMode=initial.configuredMode,effectiveMode="live",reason="auto_fallback",matchedCommand="",triggerSettings=initial.triggerSettings };
        result.Search=retry;
        try {
            token.ThrowIfCancellationRequested();
            AnswerDraft second=attempt(retry,false); // One retry; never recursively assess the retry.
            token.ThrowIfCancellationRequested();
            result.Answer=second.answer; result.ThreadId=second.ThreadId; result.Assessment=second.Assessment; result.WebSearchCalled|=second.WebSearchCalled; report.webSearchCalled=second.WebSearchCalled; report.outcome="completed";
        } catch(OperationCanceledException) { throw; }
        catch(PlanException) { throw; }
        catch(Exception e) {
            token.ThrowIfCancellationRequested();
            report.outcome="failed"; report.error=e.ToString();
            result.Answer=first.answer+"\n"+locale.Text("messages.searchRetryFailed");
            Log.Write("automatic web retry failed: "+e.GetType().Name);
        }
        return result;
    }
}
}

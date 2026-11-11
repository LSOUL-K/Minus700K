using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Minus700K {
internal sealed class SearchDecision {
    public string configuredMode,effectiveMode,reason,matchedCommand;
    public SearchTriggerSnapshot triggerSettings;
    internal bool Enabled { get { return effectiveMode!="disabled"; } }
}
internal static class SearchPolicy {
    internal static string Normalize(string text,LanguageBehavior behavior=null) { return (behavior??LanguageBehavior.For("ja")).SearchNormalize(text); }
    internal static SearchDecision Decide(string configured,string question,SearchTriggerSnapshot triggers=null,LanguageBehavior behavior=null) {
        behavior=behavior??LanguageBehavior.For("ja");
        // Initial search commands come only from this utterance. Automatic retry is
        // separately gated by SearchFallback, which must preserve an explicit opt-out.
        string normalized=behavior.SearchNormalize(behavior.WithoutQuotes((question??"").Normalize(NormalizationForm.FormKC)));
        SearchDecision decision=new SearchDecision { configuredMode=configured,effectiveMode="disabled",reason="default_image_only",matchedCommand="",triggerSettings=triggers };
        Match negative=behavior.Expression("search.negative").Match(normalized);
        if(negative.Success) { decision.reason="voice_opt_out"; decision.matchedCommand=negative.Value; return decision; }
        if(configured!="on-demand") { decision.effectiveMode=configured; decision.reason="configured"; return decision; }
        Match positive=behavior.Expression("search.positive").Match(normalized);
        if(positive.Success) { decision.effectiveMode="live"; decision.reason="voice_opt_in"; decision.matchedCommand=positive.Value; return decision; }
        if(triggers!=null) foreach(string keyword in triggers.keywords) {
            string match=behavior.SearchNormalize(keyword);
            if(match.Length>0 && behavior.KeywordMatches(normalized,match)) {
                decision.effectiveMode="live"; decision.reason="keyword_opt_in"; decision.matchedCommand=keyword; return decision;
            }
        }
        return decision;
    }
}
}

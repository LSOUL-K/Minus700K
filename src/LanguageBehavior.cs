using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Minus700K {
// Text translation and language recognition are separate resources. Search
// patterns operate on the input language (Whisper Language), not the UI language.
// Keep each behavior group complete when adding a language; missing keys retain
// Japanese behavior until that language's recognition rules have been supplied.
internal sealed class LanguageBehavior {
    static readonly object Gate=new object();
    static readonly Dictionary<string,LanguageBehavior> Profiles=new Dictionary<string,LanguageBehavior>(StringComparer.Ordinal);
    readonly Localizer text;
    readonly Dictionary<string,Regex> expressions=new Dictionary<string,Regex>(StringComparer.Ordinal);
    internal readonly string Code;
    LanguageBehavior(string code) { Code=code; text=L10n.For(code); }
    internal static LanguageBehavior For(string code) {
        string language=(code??"ja").ToLowerInvariant();
        language=language=="en" || language.StartsWith("en-",StringComparison.Ordinal)?"en":"ja";
        lock(Gate) { LanguageBehavior value; if(!Profiles.TryGetValue(language,out value)) Profiles.Add(language,value=new LanguageBehavior(language)); return value; }
    }
    internal static LanguageBehavior ForInput(Settings settings) {
        return For(settings==null?"ja":string.Equals(settings.Language,"auto",StringComparison.OrdinalIgnoreCase)?settings.AppLanguage:settings.Language);
    }
    internal static string VoiceLanguage(string appLanguage) { return For(appLanguage).Code; }
    internal string Text(string key) { return text.Text("behavior."+key); }
    string Pattern(string key) {
        string pattern=Text(key);
        // Named tokens keep shared regex pieces consistent without using string.Format
        // (regex quantifiers also contain braces).
        if(key=="search.positive" || key=="search.negative") pattern=pattern.Replace("{network}",Text("search.network")).Replace("{ending}",Text("search.ending"));
        return pattern;
    }
    internal Regex Expression(string key) {
        lock(expressions) { Regex value; if(!expressions.TryGetValue(key,out value)) expressions.Add(key,value=new Regex(Pattern(key),RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1))); return value; }
    }
    IEnumerable<string[]> Pairs(string key) {
        foreach(string line in Text(key).Split(new[]{'\n'},StringSplitOptions.RemoveEmptyEntries)) {
            string[] pair=line.TrimEnd('\r').Split(new[]{'\t'},2); if(pair.Length==2 && pair[0].Length>0) yield return pair;
        }
    }
    string ReplaceWords(string value,string key) { foreach(var pair in Pairs(key)) value=value.Replace(pair[0],pair[1]); return value; }
    internal string SearchNormalize(string value) {
        string normalized=Expression("search.whitespacePattern").Replace((value??"").Normalize(NormalizationForm.FormKC),Text("search.whitespaceReplacement")).ToLowerInvariant();
        return ReplaceWords(normalized,"search.replacements").Trim();
    }
    internal bool KeywordMatches(string question,string keyword) {
        if(Code!="en") return question.IndexOf(keyword,StringComparison.Ordinal)>=0;
        string pattern=(char.IsLetterOrDigit(keyword[0])?@"(?<![\p{L}\p{Nd}_])":"")+Regex.Escape(keyword)+(char.IsLetterOrDigit(keyword[keyword.Length-1])?@"(?![\p{L}\p{Nd}_])":"");
        return Regex.IsMatch(question,pattern,RegexOptions.CultureInvariant);
    }
    internal string WithoutQuotes(string value) { return Expression("quoted").Replace(value??"",""); }
    internal string CleanTranscription(string value) { return Expression("transcription.annotations").Replace(value??"","").Trim(); }
    internal static string StripSpeechMetadata(string value) {
        // In-memory answers can be replayed after changing languages. Apply every
        // installed language's metadata grammar, leaving the answer itself intact.
        foreach(var language in L10n.Languages) {
            var profile=For(language.Code);
            string verified=profile.Text("speech.verified").Replace("{date}",profile.Text("speech.date"));
            value=Regex.Replace(value,@"[（(]\s*"+verified+@"\s*[。.]?\s*[）)]","");
            value=Regex.Replace(value,@"(?:(?<=[。！？!?.])|^)[ \t\r\n]*"+verified+@"[ \t]*(?:[。.]|(?=\r?$))","",RegexOptions.Multiline);
        }
        return value;
    }
    internal static string StripSpeechHeadings(string value) {
        foreach(var language in L10n.Languages) value=Regex.Replace(value,For(language.Code).Text("speech.sourceHeading"),"",RegexOptions.Multiline);
        return value;
    }
}
}

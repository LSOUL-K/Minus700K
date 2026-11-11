using System;

namespace Minus700K {
// User-written instructions and keywords remain data. Recognize only shipped
// defaults, allowing line-ending differences and known historical app defaults.
internal static class LocalizedDefaults {
    static string Comparable(string text) { return (text??"").Replace("\r\n","\n").Replace("\r","\n").Trim(); }
    internal static bool IsDefault(string key,string configured) {
        if(configured==null) return false;
        string comparison=Comparable(configured);
        foreach(var locale in L10n.Languages)
            if(string.Equals(comparison,Comparable(L10n.For(locale.Code).Text(key)),StringComparison.Ordinal)) return true;
        if(key=="defaults.applicationInstructions") {
            // Two older Japanese defaults joined sentences across line breaks.
            // Do not generalize this to keywords: changing their line breaks
            // changes the individual triggers and is an intentional edit.
            string baseline=Comparable(L10n.For("ja").Text(key));
            if(comparison==baseline.Replace("\n","")) return true;
            int firstBreak=baseline.IndexOf('\n');
            if(firstBreak>=0 && comparison==baseline.Remove(firstBreak,1)) return true;
        }
        return false;
    }
    internal static string Resolve(string key,string configured,string language) {
        return IsDefault(key,configured)?L10n.For(language).Text(key):configured??"";
    }
    // Explicit language selection edits a draft. Calling this on load or every
    // save would overwrite later manual speech choices, so the UI calls it only
    // when the user actually changes the language selection.
    internal static void ApplyLanguage(Settings value,string language) {
        if(value==null) throw new ArgumentNullException("value");
        var target=L10n.For(language);
        if(IsDefault("defaults.customInstructions",value.CustomInstructionsText)) value.CustomInstructionsText=target.Text("defaults.customInstructions");
        if(IsDefault("defaults.searchTriggers",value.WebSearchTriggersText)) value.WebSearchTriggersText=target.Text("defaults.searchTriggers");
        if(IsDefault("defaults.applicationInstructions",value.ApplicationInstructionsText)) value.ApplicationInstructionsText=target.Text("defaults.applicationInstructions");
        value.AppLanguage=target.Language;
        value.Language=target.Language;
        value.SpeechVoice=""; // Select an installed voice in the new app language.
    }
}
}

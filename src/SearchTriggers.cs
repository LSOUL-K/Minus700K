using System;
using System.Collections.Generic;
using System.IO;

namespace Minus700K {
internal sealed class SearchTriggerSnapshot {
    public string source,loadedAt;
    public string[] keywords=new string[0];
}
internal sealed class SearchTriggers {
    internal static string DefaultText { get { return L10n.For("ja").Text("defaults.searchTriggers"); } }
    readonly bool enabled;
    readonly string[] keywords;
    internal SearchTriggers(bool enabled,string text,string language="ja",LanguageBehavior behavior=null) {
        text=LocalizedDefaults.Resolve("defaults.searchTriggers",text,language);
        behavior=behavior??LanguageBehavior.For(language);
        this.enabled=enabled;
        List<string> entries=new List<string>(); HashSet<string> seen=new HashSet<string>(StringComparer.Ordinal);
        if(enabled) using(StringReader reader=new StringReader(text??"")) {
            string line; while((line=reader.ReadLine())!=null) {
                line=line.Trim(); if(line.Length==0 || line.StartsWith("#",StringComparison.Ordinal)) continue;
                string normalized=SearchPolicy.Normalize(line,behavior);
                if(normalized.Length>0 && seen.Add(normalized)) entries.Add(line);
            }
        }
        keywords=entries.ToArray();
    }
    internal SearchTriggerSnapshot Read() {
        return new SearchTriggerSnapshot { source=enabled?"config.json":"disabled",loadedAt=DateTimeOffset.Now.ToString("o"),keywords=(string[])keywords.Clone() };
    }
}
}

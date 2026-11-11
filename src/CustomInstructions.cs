using System;

namespace Minus700K {
internal sealed class InstructionSnapshot {
    internal string Text;
    public string source,loadedAt;
    public int characterCount;
}
internal sealed class CustomInstructions {
    internal static string DefaultText { get { return L10n.For("ja").Text("defaults.customInstructions"); } }
    readonly bool enabled;
    readonly string text;
    internal CustomInstructions(bool enabled,string text,string language="ja") { this.enabled=enabled; this.text=LocalizedDefaults.Resolve("defaults.customInstructions",text,language).Trim(); }
    // The active settings are replaced after Save and Apply. Requests never read a text file.
    internal InstructionSnapshot Read() {
        string value=enabled?text:"";
        return new InstructionSnapshot { Text=value,source=enabled?"config.json":"disabled",loadedAt=DateTimeOffset.Now.ToString("o"),characterCount=value.Length };
    }
}
}

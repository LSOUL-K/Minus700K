using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Minus700K {
internal sealed class LocaleInfo {
    internal readonly string Code,NativeName;
    internal LocaleInfo(string code,string nativeName) { Code=code; NativeName=nativeName; }
}

// Catalogs are immutable and embedded in the executable. A request captures its
// Localizer once, so changing the UI language cannot change a request mid-flight.
internal sealed class Localizer {
    internal readonly string Language;
    readonly IDictionary<string,string> values,fallback;
    internal Localizer(string language,IDictionary<string,string> values,IDictionary<string,string> fallback) {
        Language=language; this.values=values; this.fallback=fallback;
    }
    internal string Text(string key) {
        string value;
        if(values.TryGetValue(key,out value) || fallback.TryGetValue(key,out value)) return value;
        // An unknown source key is a programming error, not a missing translation.
        throw new KeyNotFoundException("Unknown localization key: "+key);
    }
    internal string Format(string key,params object[] args) { return string.Format(CultureInfo.GetCultureInfo(Language),Text(key),args); }
    internal bool HasTranslation(string key) { return values.ContainsKey(key); }
}

internal static class L10n {
    internal static readonly LocaleInfo[] Languages={new LocaleInfo("ja","日本語"),new LocaleInfo("en","English")};
    static readonly Dictionary<string,Dictionary<string,string>> catalogs=LoadCatalogs();
    static Localizer current=For("ja");
    internal static Localizer Current { get { return Volatile.Read(ref current); } }
    internal static string NormalizeLanguage(string code) {
        // Missing fields in old configs use ja. Explicit invalid values are not
        // silently persisted as an unavailable language.
        // Retired preview selections still open safely, without rewriting the
        // user's saved instructions or speech-recognition override.
        if(string.Equals(code,"zh",StringComparison.OrdinalIgnoreCase) || string.Equals(code,"zh-CN",StringComparison.OrdinalIgnoreCase) || string.Equals(code,"zh-Hans",StringComparison.OrdinalIgnoreCase)) return "ja";
        foreach(var locale in Languages) if(string.Equals(code,locale.Code,StringComparison.OrdinalIgnoreCase)) return locale.Code;
        throw new ArgumentException("AppLanguage must be ja or en.","AppLanguage");
    }
    internal static Localizer For(string code) {
        code=NormalizeLanguage(code);
        Dictionary<string,string> values;
        if(!catalogs.TryGetValue(code,out values)) values=new Dictionary<string,string>(StringComparer.Ordinal);
        return new Localizer(code,values,catalogs["ja"]);
    }
    internal static void SetLanguage(string code) { Volatile.Write(ref current,For(code)); }
    internal static string T(string key) { return Current.Text(key); }
    internal static string F(string key,params object[] args) { return Current.Format(key,args); }
    internal static IEnumerable<string> Keys { get { return catalogs["ja"].Keys; } }
    static Dictionary<string,Dictionary<string,string>> LoadCatalogs() {
        var result=new Dictionary<string,Dictionary<string,string>>(StringComparer.OrdinalIgnoreCase);
        var assembly=Assembly.GetExecutingAssembly(); const string prefix="Minus700K.Locales.";
        foreach(string name in assembly.GetManifestResourceNames().Where(n=>n.StartsWith(prefix,StringComparison.Ordinal) && n.EndsWith(".json",StringComparison.Ordinal)).OrderBy(n=>n,StringComparer.Ordinal)) {
            string tail=name.Substring(prefix.Length); int separator=tail.IndexOf('.');
            if(separator<1) throw new InvalidDataException("Invalid localization resource: "+name);
            string language=tail.Substring(0,separator);
            Dictionary<string,string> target;
            if(!result.TryGetValue(language,out target)) result.Add(language,target=new Dictionary<string,string>(StringComparer.Ordinal));
            using(var stream=assembly.GetManifestResourceStream(name)) using(var reader=new StreamReader(stream,new UTF8Encoding(false,true))) {
                var entries=new JavaScriptSerializer { MaxJsonLength=4*1024*1024 }.Deserialize<Dictionary<string,string>>(reader.ReadToEnd());
                if(entries==null) throw new InvalidDataException("Empty localization catalog: "+name);
                foreach(var item in entries) {
                    if(string.IsNullOrEmpty(item.Key) || item.Value==null || target.ContainsKey(item.Key)) throw new InvalidDataException("Invalid or duplicate localization key: "+name+" / "+item.Key);
                    target.Add(item.Key,item.Value);
                }
            }
        }
        if(!result.ContainsKey("ja")) throw new InvalidDataException("Japanese localization catalog is missing.");
        foreach(var language in result.Where(p=>p.Key!="ja")) foreach(string key in language.Value.Keys)
            if(!result["ja"].ContainsKey(key)) throw new InvalidDataException("Translation has no Japanese source: "+language.Key+" / "+key);
        return result;
    }
}
}

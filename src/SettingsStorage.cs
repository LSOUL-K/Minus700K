using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Minus700K {
internal static class SettingsStorage {
    internal const int MaximumConfigBytes=8*1024*1024;
    internal static SettingsEditSession Open(string root) { return new SettingsEditSession(root); }
    internal static bool IsRetiredSetting(string name) {
        return name.Equals("SaveConversationHistory",StringComparison.OrdinalIgnoreCase) || name.Equals("UseSavedHistory",StringComparison.OrdinalIgnoreCase);
    }
    internal static Settings Clone(Settings value) {
        if(value==null) throw new ArgumentNullException("value");
        var json=Serializer(); return json.Deserialize<Settings>(json.Serialize(value));
    }
    // Reflection can return the same fields in a different order as its cache is
    // populated. Compare a canonical snapshot so an unchanged draft stays clean.
    internal static string Snapshot(Settings value) {
        if(value==null) throw new ArgumentNullException("value");
        var fields=new SortedDictionary<string,object>(StringComparer.Ordinal);
        foreach(var field in typeof(Settings).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public)) fields.Add(field.Name,field.GetValue(value));
        return Serializer().Serialize(fields);
    }
    internal static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength=MaximumConfigBytes,RecursionLimit=80 }; }
    internal static string Resolve(string root,string configured) {
        if(string.IsNullOrWhiteSpace(configured)) return null;
        try { return Path.GetFullPath(Path.Combine(root,configured.Trim())); }
        catch(Exception e) { if(!(e is ArgumentException) && !(e is NotSupportedException) && !(e is PathTooLongException)) throw; throw new ArgumentException(L10n.F("messages.SettingsStorage.001",configured)); }
    }
    internal static void Validate(Settings value,string root) {
        if(value==null) throw new ArgumentException(L10n.T("messages.SettingsStorage.002"));
        foreach(string key in new[]{value.PushToTalkKey,value.CancelKey,value.ReplayKey}) {
            if(string.IsNullOrWhiteSpace(key)) throw new ArgumentException(L10n.T("messages.SettingsStorage.003"));
            Hotkey shortcut; try { shortcut=Hotkey.Parse(key); } catch(Exception e) { throw new ArgumentException(e.Message,e); }
            if(new[]{16,17,18,91,92,160,161,162,163,164,165}.Contains(shortcut.Key)) throw new ArgumentException(L10n.T("messages.SettingsStorage.004"));
        }
        Range(value.MicrophoneDevice,-1,65535,L10n.T("messages.SettingsStorage.005"));
        Range(value.WhisperThreads,1,8,L10n.T("messages.SettingsStorage.006"));
        Range(value.MaximumRecordingSeconds,1,180,L10n.T("messages.SettingsStorage.007"));
        Range(value.MinimumRecordingMilliseconds,100,180000,L10n.T("messages.SettingsStorage.008"));
        if(value.MinimumRecordingMilliseconds>value.MaximumRecordingSeconds*1000) throw new ArgumentException(L10n.T("messages.SettingsStorage.009"));
        Range(value.CaptureMaximumWidth,320,7680,L10n.T("messages.SettingsStorage.010"));
        Range(value.RequestTimeoutSeconds,10,600,L10n.T("messages.SettingsStorage.011"));
        Range(value.WhisperTimeoutSeconds,10,600,L10n.T("messages.SettingsStorage.012"));
        Range(value.SpeechRate,-10,10,L10n.T("messages.SettingsStorage.013"));
        Range(value.SpeechVolume,0,100,L10n.T("messages.SettingsStorage.014"));
        Range(value.OtherApplicationVolume,0,100,L10n.T("ui.audio.otherApplicationVolume"));
        Range(value.CueVolume,0,100,L10n.T("messages.SettingsStorage.015"));
        Range(value.ProcessingCueIntervalSeconds,1,60,L10n.T("messages.SettingsStorage.016"));
        Range(value.RememberLastTurns,0,10,L10n.T("messages.SettingsStorage.017"));
        if(string.IsNullOrWhiteSpace(value.Language) || !Regex.IsMatch(value.Language,@"\A(?:auto|[a-z]{2,3})\z")) throw new ArgumentException(L10n.T("messages.SettingsStorage.018"));
        if(value.CodexModel==null || value.CodexModel.Length>256 || value.CodexModel.Any(char.IsWhiteSpace)) throw new ArgumentException(L10n.T("messages.SettingsStorage.019"));
        string model=Resolve(root,value.WhisperModel);
        // The default model is acquired on first resident startup. Installer
        // language changes and settings must remain usable before that download.
        if(model==null || (!File.Exists(model) && !SpeechModel.IsDefault(root,value.WhisperModel))) throw new ArgumentException(L10n.T("messages.SettingsStorage.020"));
        string codex=Resolve(root,value.CodexPath);
        if(codex!=null && (!File.Exists(codex) || !Path.GetExtension(codex).Equals(".exe",StringComparison.OrdinalIgnoreCase))) throw new ArgumentException(L10n.T("messages.SettingsStorage.021"));
        ValidateTextSettings(value);
        try { value.Validate(); } catch(Exception e) { throw new ArgumentException(e.Message,e); }
    }
    internal static void EnsureCurrentSchema(Dictionary<string,object> values) {
        if(values==null) throw new InvalidDataException(L10n.T("messages.SettingsStorage.022"));
        if(values.Keys.Any(name=>name.Equals("CustomInstructionsFile",StringComparison.OrdinalIgnoreCase) || name.Equals("WebSearchTriggersFile",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(L10n.T("messages.SettingsStorage.023"));
    }
    internal static void ValidateTextSettings(Settings value) {
        if(value==null) throw new ArgumentException(L10n.T("messages.SettingsStorage.002"));
        string[] values={value.CustomInstructionsText,value.WebSearchTriggersText,value.ApplicationInstructionsText};
        string[] names={L10n.T("messages.SettingsStorage.024"),L10n.T("messages.SettingsStorage.025"),L10n.T("messages.SettingsStorage.026")};
        for(int i=0;i<values.Length;i++) {
            if(values[i]==null) throw new ArgumentException(L10n.F("messages.SettingsStorage.027",names[i]));
            if(values[i].Length>250000) throw new ArgumentException(L10n.F("messages.SettingsStorage.028",names[i]));
        }
    }
    static void Range(int value,int minimum,int maximum,string label) { if(value<minimum || value>maximum) throw new ArgumentException(L10n.F("messages.SettingsStorage.029",label,minimum,maximum)); }
    internal static bool Same(byte[] first,byte[] second) { return first==null?second==null:second!=null && first.SequenceEqual(second); }
    internal static byte[] Bytes(string text) { return new UTF8Encoding(false,true).GetBytes(text??""); }
    internal static string Decode(byte[] bytes) {
        if(bytes==null) return null;
        int skip=bytes.Length>=3 && bytes[0]==239 && bytes[1]==187 && bytes[2]==191?3:0;
        return new UTF8Encoding(false,true).GetString(bytes,skip,bytes.Length-skip);
    }
    internal static byte[] ReadBytes(string path) {
        try {
            using(FileStream stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                if(stream.Length>MaximumConfigBytes) throw new IOException(L10n.F("messages.SettingsStorage.031",Path.GetFileName(path)));
                using(MemoryStream copy=new MemoryStream()) { stream.CopyTo(copy); if(copy.Length>MaximumConfigBytes) throw new IOException(L10n.T("messages.SettingsStorage.032")); return copy.ToArray(); }
            }
        } catch(FileNotFoundException) { return null; } catch(DirectoryNotFoundException) { return null; }
    }
    internal static string Pretty(string json) {
        var text=new StringBuilder(); int depth=0; bool quoted=false,escaped=false;
        foreach(char c in json) {
            if(quoted) { text.Append(c); if(escaped) escaped=false; else if(c=='\\') escaped=true; else if(c=='"') quoted=false; continue; }
            if(c=='"') { quoted=true; text.Append(c); }
            else if(c=='{' || c=='[') { text.Append(c); depth++; text.AppendLine(); text.Append(' ',depth*2); }
            else if(c=='}' || c==']') { depth--; text.AppendLine(); text.Append(' ',depth*2); text.Append(c); }
            else if(c==',') { text.Append(c); text.AppendLine(); text.Append(' ',depth*2); }
            else if(c==':') text.Append(": ");
            else if(!char.IsWhiteSpace(c)) text.Append(c);
        }
        return text.ToString()+Environment.NewLine;
    }
}

internal sealed class SettingsEditSession {
    internal readonly string Root;
    internal Settings Value { get; private set; }
    readonly string path,mutexName;
    Dictionary<string,object> raw;
    byte[] originalBytes;
    string originalSettings;
    internal SettingsEditSession(string root) {
        Root=Path.GetFullPath(root); path=Path.Combine(Root,"config.json"); originalBytes=SettingsStorage.ReadBytes(path);
        if(originalBytes==null) throw new FileNotFoundException(L10n.T("messages.SettingsStorage.033"),path);
        string text=SettingsStorage.Decode(originalBytes); var serializer=SettingsStorage.Serializer();
        raw=serializer.DeserializeObject(text) as Dictionary<string,object>; SettingsStorage.EnsureCurrentSchema(raw);
        Value=serializer.Deserialize<Settings>(text);
        if(Value==null) throw new InvalidDataException(L10n.T("messages.SettingsStorage.034"));
        Value.AppLanguage=L10n.NormalizeLanguage(Value.AppLanguage);
        SettingsStorage.ValidateTextSettings(Value); originalSettings=SettingsStorage.Snapshot(Value);
        using(SHA256 sha=SHA256.Create()) mutexName="Local\\Minus700K_Settings_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-","").Substring(0,24);
    }
    internal bool HasChanges(Settings value) { return SettingsStorage.Snapshot(value)!=originalSettings; }
    void Verify() {
        if(!SettingsStorage.Same(originalBytes,SettingsStorage.ReadBytes(path))) throw new IOException(L10n.T("messages.SettingsStorage.035"));
    }
    internal bool Save(Settings value,Action apply=null) {
        Settings candidate=SettingsStorage.Clone(value); SettingsStorage.Validate(candidate,Root);
        string candidateSettings=SettingsStorage.Snapshot(candidate);
        using(Mutex gate=new Mutex(false,mutexName)) {
            bool acquired=false;
            try {
                try { acquired=gate.WaitOne(10000); } catch(AbandonedMutexException) { acquired=true; }
                if(!acquired) throw new IOException(L10n.T("messages.SettingsStorage.036"));
                Verify();
                if(candidateSettings==originalSettings && !raw.Keys.Any(SettingsStorage.IsRetiredSetting)) { if(apply!=null) apply(); Value=SettingsStorage.Clone(candidate); return false; }
                var serializer=SettingsStorage.Serializer(); var merged=new Dictionary<string,object>(raw);
                var known=(Dictionary<string,object>)serializer.DeserializeObject(serializer.Serialize(candidate));
                var names=new HashSet<string>(known.Keys,StringComparer.OrdinalIgnoreCase);
                // Retired conversation-saving options cannot be re-enabled and
                // are removed only when the user explicitly saves settings.
                foreach(string name in merged.Keys.Where(name=>names.Contains(name) || SettingsStorage.IsRetiredSetting(name)).ToArray()) merged.Remove(name);
                foreach(var item in known) merged[item.Key]=item.Value;
                byte[] next=SettingsStorage.Bytes(SettingsStorage.Pretty(serializer.Serialize(merged)));
                if(next.Length>SettingsStorage.MaximumConfigBytes) throw new IOException(L10n.T("messages.SettingsStorage.032"));
                var change=new SettingsConfigChange(path,originalBytes,next); bool applying=false;
                try {
                    change.Prepare(); change.Commit();
                    // Keep the previous config until the resident accepts the change.
                    // A failed apply leaves this open editor's baseline untouched.
                    applying=true; if(apply!=null) apply();
                } catch(Exception failure) {
                    try { change.Rollback(); }
                    catch(Exception recovery) { throw new IOException(L10n.F("messages.SettingsStorage.037",change.Backup),new AggregateException(failure,recovery)); }
                    throw new IOException(applying?L10n.F("messages.SettingsStorage.038",failure.Message):L10n.T("messages.SettingsStorage.039"),failure);
                } finally { change.Cleanup(); }
                originalBytes=next; originalSettings=candidateSettings; raw=merged; Value=SettingsStorage.Clone(candidate);
                return true;
            } finally { if(acquired) gate.ReleaseMutex(); }
        }
    }
}

internal sealed class SettingsConfigChange {
    readonly string path; readonly byte[] previous,next;
    string temporary; internal string Backup { get; private set; }
    bool committed,retainBackup;
    internal SettingsConfigChange(string path,byte[] previous,byte[] next) { this.path=path; this.previous=previous; this.next=next; }
    internal void Prepare() {
        string suffix=".minus700k-"+Guid.NewGuid().ToString("N"); temporary=path+suffix+".tmp"; Backup=path+suffix+".bak";
        using(FileStream output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { output.Write(next,0,next.Length); output.Flush(true); }
    }
    internal void Commit() {
        if(!SettingsStorage.Same(previous,SettingsStorage.ReadBytes(path))) throw new IOException(L10n.T("messages.SettingsStorage.040"));
        File.Replace(temporary,path,Backup); committed=true;
    }
    internal void Rollback() {
        if(!committed) return;
        // Never overwrite an unrelated editor's newer data during recovery.
        try {
            if(!SettingsStorage.Same(next,SettingsStorage.ReadBytes(path))) throw new IOException(L10n.T("messages.SettingsStorage.041"));
            File.Replace(Backup,path,null); committed=false;
        } catch { retainBackup=true; throw; }
    }
    internal void Cleanup() {
        try { if(temporary!=null && File.Exists(temporary)) File.Delete(temporary); } catch { }
        try { if(!retainBackup && Backup!=null && File.Exists(Backup)) File.Delete(Backup); } catch { }
    }
}
}

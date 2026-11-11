using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Minus700K {
internal static class SettingsStorageDiagnostics {
    static void Check(bool condition,string message) { if(!condition) throw new Exception("settings storage test: "+message); }
    static void Reject(Action action,string message) { try { action(); } catch(ArgumentException) { return; } throw new Exception("settings storage test: expected validation rejection: "+message); }
    static void RejectLegacy(Action action) {
        try { action(); } catch(InvalidDataException e) { Check(e.Message.Contains("migrate-config.ps1"),"old schema error explains migration command"); return; }
        throw new Exception("settings storage test: old file keys must be rejected");
    }
    static void NoTemporaryFiles(string fixture) { Check(!Directory.GetFiles(fixture).Any(p=>p.Contains(".minus700k-")),"completed operation removes temporary files and backups"); }
    internal static void Run(string root,Dictionary<string,object> report) {
        string parent=Path.GetFullPath(Path.Combine(root,"runtime","settings-storage-test-work"));
        string fixture=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
        Check(fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture containment"); Directory.CreateDirectory(fixture);
        try {
            var original=new Settings { WhisperModel="model.bin",CaptureMaximumWidth=2560,AnnounceCaptureFailure=true,GameName="旧設定の補足名（保持）",CustomInstructionsText="元の指示\r\n",WebSearchTriggersText="最新情報\n現在は\n" };
            File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1,2,3});
            var serializer=SettingsStorage.Serializer(); var raw=(Dictionary<string,object>)serializer.DeserializeObject(serializer.Serialize(original));
            raw["FutureOption"]=new Dictionary<string,object> { {"enabled",true},{"value","keep \"quotes\" and 日本語"} };
            string config=Path.Combine(fixture,"config.json");
            Check(!typeof(Settings).GetFields().Any(field=>SettingsStorage.IsRetiredSetting(field.Name)),"conversation saving and saved recall no longer exist as settings");
            foreach(bool debug in new[]{false,true}) foreach(string preference in new[]{"missing","null","false","true"}) foreach(bool mixedCase in new[]{false,true}) {
                var historyRaw=new Dictionary<string,object>(raw); historyRaw["KeepRequestFiles"]=debug; historyRaw["RememberLastTurns"]=7;
                if(preference!="missing") {
                    historyRaw[mixedCase?"saveconversationhistory":"SaveConversationHistory"]=preference=="null"?null:(object)(preference=="true");
                    historyRaw[mixedCase?"USESAVEDHISTORY":"UseSavedHistory"]=preference=="null"?null:(object)(preference=="true");
                }
                File.WriteAllText(config,serializer.Serialize(historyRaw),new UTF8Encoding(false));
                byte[] beforeHistoryRead=File.ReadAllBytes(config);
                var historyLoaded=Settings.Load(fixture); var historyEditor=SettingsStorage.Open(fixture);
                Check(historyLoaded.KeepRequestFiles==debug && historyEditor.Value.KeepRequestFiles==debug && historyLoaded.RememberLastTurns==7 && historyEditor.Value.RememberLastTurns==7,"retired history preferences do not affect memory or debug settings: "+preference);
                Check(!((Dictionary<string,object>)serializer.DeserializeObject(serializer.Serialize(historyLoaded))).Keys.Any(SettingsStorage.IsRetiredSetting),"legacy history options are absent from loaded settings");
                Check(File.ReadAllBytes(config).SequenceEqual(beforeHistoryRead),"loading retired options never rewrites stored settings");
                bool hasRetired=preference!="missing";
                Check(!historyEditor.HasChanges(historyEditor.Value),"opening legacy options alone does not mark settings as edited");
                Check(historyEditor.Save(historyEditor.Value)==hasRetired,"explicit unchanged save removes retired options only when present");
                var cleaned=(Dictionary<string,object>)serializer.DeserializeObject(File.ReadAllText(config));
                Check(!cleaned.Keys.Any(SettingsStorage.IsRetiredSetting) && cleaned.ContainsKey("FutureOption"),"save removes both retired options including case variants and preserves unknown fields");
                var changedDebug=SettingsStorage.Clone(historyEditor.Value); changedDebug.KeepRequestFiles=!debug;
                Check(historyEditor.Save(changedDebug),"debug preference can be saved independently");
                var afterDebugSave=Settings.Load(fixture);
                Check(afterDebugSave.KeepRequestFiles==!debug && afterDebugSave.RememberLastTurns==7,"changing debug logging preserves the memory limit");
            }
            File.WriteAllText(config,serializer.Serialize(raw),new UTF8Encoding(true));
            byte[] originalConfig=File.ReadAllBytes(config);
            var canceled=SettingsStorage.Open(fixture); canceled.Value.CueVolume=23; canceled.Value.CustomInstructionsText="未保存";
            Check(File.ReadAllBytes(config).SequenceEqual(originalConfig),"editing and abandoning session does not write");
            var session=SettingsStorage.Open(fixture); var clone=SettingsStorage.Clone(session.Value); clone.CueVolume=27;
            Check(session.Value.CueVolume==original.CueVolume,"clone is independent");
            var reversedRaw=new Dictionary<string,object>(); foreach(var pair in raw.Reverse()) reversedRaw[pair.Key]=pair.Value;
            var reordered=serializer.Deserialize<Settings>(serializer.Serialize(reversedRaw));
            Check(SettingsStorage.Snapshot(session.Value)==SettingsStorage.Snapshot(reordered) && !session.HasChanges(reordered),"equivalent settings ignore JSON property order");
            Check(SettingsStorage.Snapshot(session.Value)!=SettingsStorage.Snapshot(clone),"snapshot detects an actual changed value");
            var snapshotFields=((Dictionary<string,object>)serializer.DeserializeObject(SettingsStorage.Snapshot(session.Value))).Keys.ToArray();
            Check(snapshotFields.SequenceEqual(snapshotFields.OrderBy(name=>name,StringComparer.Ordinal)),"snapshot has stable ordinal property order");
            clone.CustomInstructionsText="新しい指示\r\n引用: \"短く\"、場所 C:\\example\\file\n"; clone.WebSearchTriggersText="いまの価格\n速報を調べて\n";
            clone.ApplicationInstructionsText="アプリ全体の指定\r\n識別情報を扱う方針\n";
            Check(session.HasChanges(clone) && session.Save(clone),"inline settings and both texts save together");
            Settings loaded=Settings.Load(fixture);
            Check(loaded.CueVolume==27 && loaded.CustomInstructionsText==clone.CustomInstructionsText && loaded.WebSearchTriggersText==clone.WebSearchTriggersText,"inline Japanese text preserves quotes slashes and exact line endings");
            Check(loaded.ApplicationInstructionsText==clone.ApplicationInstructionsText,"application instructions survive save and reload without newline changes");
            Check(Directory.GetFiles(fixture).Select(Path.GetFileName).OrderBy(p=>p).SequenceEqual(new[]{"config.json","model.bin"}),"text save creates no separate text files");
            var stored=(Dictionary<string,object>)serializer.DeserializeObject(File.ReadAllText(config)); var future=(Dictionary<string,object>)stored["FutureOption"];
            Check((bool)future["enabled"] && (string)future["value"]=="keep \"quotes\" and 日本語","unknown nested fields preserved");
            Check(!session.HasChanges(session.Value),"successful save updates editor baseline");
            byte[] unchanged=File.ReadAllBytes(config); int applies=0;
            Check(!session.Save(session.Value,()=>applies++) && applies==1 && unchanged.SequenceEqual(File.ReadAllBytes(config)),"unchanged save applies once without rewriting config");
            foreach(Action<Settings> invalidate in new Action<Settings>[] {
                s=>s.CancelKey=s.PushToTalkKey,s=>s.PushToTalkKey="ShiftKey",s=>s.WhisperTimeoutSeconds=601,
                s=>{s.MinimumRecordingMilliseconds=2000;s.MaximumRecordingSeconds=1;},s=>s.CueVolume=101,
                s=>s.WhisperModel="missing.bin",s=>s.CodexPath="missing.exe",s=>s.Language="日本語",
                s=>s.CustomInstructionsText=null,s=>s.WebSearchTriggersText=null,s=>s.ApplicationInstructionsText=null,
                s=>s.CustomInstructionsText=new string('x',250001),s=>s.WebSearchTriggersText=new string('x',250001),s=>s.ApplicationInstructionsText=new string('x',250001)
            }) {
                var invalid=SettingsStorage.Clone(session.Value); invalidate(invalid);
                Reject(()=>session.Save(invalid,()=>applies++),"invalid setting or inline text");
                Check(unchanged.SequenceEqual(File.ReadAllBytes(config)) && applies==1,"validation failure preserves config and does not apply");
            }
            var stale=SettingsStorage.Open(fixture); File.AppendAllText(config," \n"); byte[] external=File.ReadAllBytes(config);
            bool conflict=false; try { var next=SettingsStorage.Clone(stale.Value); next.CueVolume=28; stale.Save(next,()=>applies++); } catch(IOException e) { conflict=e.Message.Contains("変更"); }
            Check(conflict && external.SequenceEqual(File.ReadAllBytes(config)) && applies==1,"concurrent config edit is preserved before apply");
            var rollback=SettingsStorage.Open(fixture); byte[] beforeFailure=File.ReadAllBytes(config); var candidate=SettingsStorage.Clone(rollback.Value); candidate.CueVolume=39; candidate.CustomInstructionsText="適用前の仮テキスト\n";
            bool failed=false;
            using(var locked=new FileStream(config,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                try { rollback.Save(candidate,()=>applies++); } catch(IOException) { failed=true; }
            }
            Check(failed && beforeFailure.SequenceEqual(File.ReadAllBytes(config)) && applies==1 && rollback.HasChanges(candidate),"replacement failure preserves file baseline and skips apply"); NoTemporaryFiles(fixture);
            failed=false; bool sawCandidate=false;
            try { rollback.Save(candidate,delegate { Settings applying=Settings.Load(fixture); sawCandidate=applying.CueVolume==39 && applying.CustomInstructionsText==candidate.CustomInstructionsText; throw new InvalidOperationException("fixture apply failure"); }); }
            catch(IOException e) { failed=e.Message.Contains("変更前"); }
            Check(failed && sawCandidate && beforeFailure.SequenceEqual(File.ReadAllBytes(config)),"apply sees entire new config and failure restores exact previous bytes");
            Check(rollback.Value.CueVolume!=39 && rollback.HasChanges(candidate) && !rollback.HasChanges(rollback.Value),"apply failure leaves open editor baseline unchanged"); NoTemporaryFiles(fixture);
            Check(rollback.Save(candidate,()=>applies++) && rollback.Value.CueVolume==39 && !rollback.HasChanges(rollback.Value),"same editor can retry successfully after failed apply");
            var repeated=SettingsStorage.Clone(rollback.Value); repeated.WebSearchTriggersText="再保存の検索語\r\n";
            Check(rollback.Save(repeated,()=>applies++) && Settings.Load(fixture).WebSearchTriggersText==repeated.WebSearchTriggersText && !rollback.HasChanges(repeated),"open editor supports another save after applying without reopening");
            var recovery=SettingsStorage.Open(fixture); byte[] beforeRace=File.ReadAllBytes(config); var raceCandidate=SettingsStorage.Clone(recovery.Value); raceCandidate.CueVolume=40; string externalDuringApply=null;
            bool recoveryRejected=false;
            try { recovery.Save(raceCandidate,delegate { File.AppendAllText(config," \n"); externalDuringApply=File.ReadAllText(config); throw new InvalidOperationException("fixture concurrent apply failure"); }); }
            catch(IOException e) { recoveryRejected=e.Message.Contains("バックアップ"); }
            string[] backups=Directory.GetFiles(fixture,"*.minus700k-*.bak");
            Check(recoveryRejected && File.ReadAllText(config)==externalDuringApply && backups.Length==1 && File.ReadAllBytes(backups[0]).SequenceEqual(beforeRace),"failed rollback preserves external edit and retains original backup");
            Check(recovery.HasChanges(raceCandidate),"failed rollback does not advance baseline");
            File.WriteAllBytes(config,beforeRace); File.Delete(backups[0]); NoTemporaryFiles(fixture);
            const string instructions="独自の回答ルールです。\r\n数字を先に伝えてください。\r\n",triggers="いまの価格\n速報を調べて\n";
            var custom=SettingsStorage.Open(fixture); var customValue=SettingsStorage.Clone(custom.Value); customValue.CustomInstructionsText=instructions; customValue.WebSearchTriggersText=triggers; custom.Save(customValue);
            foreach(bool[] flags in new[]{new[]{false,false},new[]{true,false},new[]{false,true},new[]{true,true}}) {
                var toggle=SettingsStorage.Open(fixture); var next=SettingsStorage.Clone(toggle.Value); next.CustomInstructionsEnabled=flags[0]; next.WebSearchTriggersEnabled=flags[1]; Check(toggle.Save(next),"enable flags save independently");
                var reopened=SettingsStorage.Open(fixture);
                Check(reopened.Value.CustomInstructionsEnabled==flags[0] && reopened.Value.WebSearchTriggersEnabled==flags[1],"enable flags survive save and reopen");
                Check(reopened.Value.CustomInstructionsText==instructions && reopened.Value.WebSearchTriggersText==triggers && !reopened.HasChanges(reopened.Value),"OFF ON and reopen preserve inline text and line endings");
                Check(reopened.Value.ApplicationInstructionsText==customValue.ApplicationInstructionsText,"custom toggles do not modify independent application instructions");
            }
            var empty=SettingsStorage.Open(fixture); var emptyValue=SettingsStorage.Clone(empty.Value); emptyValue.CustomInstructionsText=""; emptyValue.WebSearchTriggersText=""; emptyValue.ApplicationInstructionsText="";
            Check(empty.Save(emptyValue) && Settings.Load(fixture).CustomInstructionsText=="" && Settings.Load(fixture).WebSearchTriggersText=="" && Settings.Load(fixture).ApplicationInstructionsText=="","empty strings are preserved without reverting to placeholder defaults");
            var maximum=SettingsStorage.Open(fixture); var maximumValue=SettingsStorage.Clone(maximum.Value); maximumValue.CustomInstructionsText=new string('\u0001',250000); maximumValue.WebSearchTriggersText=new string('\u0002',250000);
            Check(maximum.Save(maximumValue) && SettingsStorage.Open(fixture).Value.CustomInstructionsText==maximumValue.CustomInstructionsText && Settings.Load(fixture).WebSearchTriggersText==maximumValue.WebSearchTriggersText,"250000 character boundaries work even when JSON escapes expand both strings");
            maximum.Save(customValue);
            foreach(bool captureEnabled in new[]{false,true}) {
                var capture=SettingsStorage.Open(fixture); var next=SettingsStorage.Clone(capture.Value); next.CaptureEnabled=captureEnabled; Check(capture.Save(next),"capture toggle saves");
                loaded=Settings.Load(fixture); Check(loaded.CaptureEnabled==captureEnabled && loaded.CaptureMaximumWidth==2560 && loaded.AnnounceCaptureFailure && loaded.GameName==original.GameName,"capture flag retains dependent values and hidden hint");
            }
            foreach(string device in new[]{"fixture-speaker-id",""}) {
                var speaker=SettingsStorage.Open(fixture); var next=SettingsStorage.Clone(speaker.Value); next.SpeakerDevice=device; Check(speaker.Save(next),"speaker choice saves");
                Check(Settings.Load(fixture).SpeakerDevice==device && SettingsStorage.Open(fixture).Value.SpeakerDevice==device,"speaker choice survives save reopen including Windows routing");
            }
            byte[] current=File.ReadAllBytes(config);
            var legacyDefaults=(Dictionary<string,object>)serializer.DeserializeObject(File.ReadAllText(config)); foreach(string name in new[]{"CustomInstructionsEnabled","WebSearchTriggersEnabled","CaptureEnabled","SpeakerDevice","ApplicationInstructionsText"}) legacyDefaults.Remove(name);
            File.WriteAllText(config,serializer.Serialize(legacyDefaults),new UTF8Encoding(false)); var defaults=SettingsStorage.Open(fixture).Value;
            Check(defaults.CustomInstructionsEnabled && defaults.WebSearchTriggersEnabled && defaults.CaptureEnabled && defaults.SpeakerDevice=="" && defaults.GameName==original.GameName && defaults.ApplicationInstructionsText==ApplicationInstructions.DefaultText,"older configs receive application defaults while preserving other values"); File.WriteAllBytes(config,current);
            foreach(string oldKey in new[]{"CustomInstructionsFile","WebSearchTriggersFile","custominstructionsfile","WEBSEARCHTRIGGERSFILE"}) {
                var old=(Dictionary<string,object>)serializer.DeserializeObject(SettingsStorage.Decode(current)); old[oldKey]="legacy-file.txt";
                File.WriteAllText(config,serializer.Serialize(old),new UTF8Encoding(false)); byte[] oldBytes=File.ReadAllBytes(config);
                RejectLegacy(()=>SettingsStorage.Open(fixture)); RejectLegacy(()=>Settings.Load(fixture));
                Check(oldBytes.SequenceEqual(File.ReadAllBytes(config)),"old schema is rejected without implicit migration or writes");
            }
            File.WriteAllBytes(config,current);
            foreach(string name in new[]{"CustomInstructionsText","WebSearchTriggersText","ApplicationInstructionsText"}) {
                var invalid=(Dictionary<string,object>)serializer.DeserializeObject(SettingsStorage.Decode(current)); invalid[name]=null; File.WriteAllText(config,serializer.Serialize(invalid),new UTF8Encoding(false));
                Reject(()=>SettingsStorage.Open(fixture),"null inline text rejected on editor load"); Reject(()=>Settings.Load(fixture),"null inline text rejected on runtime load");
            }
            File.WriteAllBytes(config,current); NoTemporaryFiles(fixture);
            report["checks"]="conversation saving and saved recall settings removed / retired missing + null + false + true + case variants ignored / explicit save removes retired options / debug and memory settings preserved / read-only legacy loading / single config + inline UTF-8 text / clone + order-independent snapshot / canceled edits / unknown JSON preservation / validation + text limits / concurrent edit rejection / atomic replace / apply rollback + retry / repeated save baseline / backup retained on external edit / enable flags retain text / empty strings / legacy schema migration error / capture + speaker defaults";
        } finally { if(Directory.Exists(fixture) && fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)) Directory.Delete(fixture,true); }
    }
}
}

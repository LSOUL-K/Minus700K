using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Minus700K {
// These diagnostics never instantiate Core Audio or change the user's mixer.
internal static class OtherApplicationAudioDiagnostics {
    const uint OwnProcess=42;
    static void Check(bool condition,string message) { if(!condition) throw new Exception("other application audio: "+message); }
    static void Volume(SessionData session,float expected,string message) { Check(Math.Abs(session.Value-expected)<0.00001f,message); }
    static void Rejected(Action action,string message) { bool rejected=false; try { action(); } catch(Exception) { rejected=true; } Check(rejected,message); }

    sealed class SessionData {
        internal readonly string Id; internal readonly uint ProcessId;
        internal volatile float Value; internal bool Muted; internal int Reads,Writes,Opened,Closed,ReadFailures,WriteFailures,WriteThenFailures;
        internal SessionData(string id,uint processId,float value) { Id=id; ProcessId=processId; Value=value; }
    }
    sealed class Session : IOtherAudioSession {
        readonly SessionData data; bool disposed;
        internal Session(SessionData data) { this.data=data; data.Opened++; }
        void Alive() { Check(!disposed,"released session handle is never reused"); }
        public string Id { get { Alive(); return data.Id; } }
        public uint ProcessId { get { Alive(); return data.ProcessId; } }
        public float Volume {
            get { Alive(); data.Reads++; if(data.ReadFailures>0) { data.ReadFailures--; throw new IOException("fixture read failure"); } return data.Value; }
            set {
                Alive(); data.Writes++;
                if(data.WriteFailures>0) { data.WriteFailures--; throw new IOException("fixture write failure"); }
                data.Value=value;
                if(data.WriteThenFailures>0) { data.WriteThenFailures--; throw new IOException("fixture committed write failure"); }
            }
        }
        public void Dispose() { if(disposed) return; disposed=true; data.Closed++; }
    }
    sealed class Backend : IOtherAudioBackend {
        internal readonly List<SessionData> Items=new List<SessionData>();
        internal int Enumerations,FailAfter=-1; internal bool Disposed;
        internal SessionData Add(string id,uint pid,float value) { var data=new SessionData(id,pid,value); Items.Add(data); return data; }
        public IEnumerable<IOtherAudioSession> Sessions() {
            Check(!Disposed,"released backend is never enumerated"); Enumerations++;
            for(int i=0;i<Items.Count;i++) {
                if(FailAfter==i) { FailAfter=-1; throw new IOException("fixture enumeration failure"); }
                yield return new Session(Items[i]);
            }
        }
        public void Dispose() { Disposed=true; }
        internal void Released() { Check(Disposed && Items.All(item=>item.Opened==item.Closed),"backend and all enumerated session handles released"); }
    }

    static void LevelsAndRestoration() {
        var backend=new Backend(); var own=backend.Add("own",OwnProcess,0.9f); var music=backend.Add("music",7,0.8f); var game=backend.Add("game",8,0.3f);
        var muted=backend.Add("muted",9,0.6f); muted.Muted=true;
        using(var state=new OtherApplicationAudioState(backend,OwnProcess)) {
            state.Start(100); state.Refresh(); state.Stop();
            Check(backend.Enumerations==0 && backend.Items.All(item=>item.Writes==0),"100 leaves mixer completely untouched");
            state.Start(50);
            Volume(music,0.4f,"50 scales original music level"); Volume(game,0.15f,"50 scales original game level independently");
            Volume(own,0.9f,"current application remains unchanged"); Check(own.Reads==0 && own.Writes==0,"own process volume is never read or written");
            Check(muted.Muted,"existing mute is preserved"); Volume(muted,0.3f,"muted session retains a scaled level for later unmute");
            state.Start(50); state.Refresh(); Volume(music,0.4f,"repeated start and refresh do not compound attenuation");
            int stableWrites=music.Writes; state.Refresh(); Check(music.Writes==stableWrites,"stable session levels do not trigger redundant writes");
            music.Value=0.6f; state.Refresh(); Volume(music,0.4f,"external volume change is reduced again while the answer is active");
            var late=backend.Add("late-on-second-output",12,0.7f); state.Refresh(); Volume(late,0.35f,"new session is reduced during active playback");
            state.Start(25); Volume(music,0.2f,"changed percentage uses original volume"); Volume(late,0.175f,"changed percentage includes later sessions");
            foreach(int invalid in new[]{-1,101}) Rejected(()=>state.Start(invalid),"out-of-range percentages are rejected");
            Volume(music,0.2f,"invalid update cannot disturb active levels");
            state.Stop();
            Volume(music,0.8f,"stop restores music"); Volume(game,0.3f,"stop restores game"); Volume(muted,0.6f,"stop restores muted session level"); Volume(late,0.7f,"stop restores sessions created later");
            Check(state.PendingRestores==0 && muted.Muted,"restoration finishes without changing mute state");
            int writes=music.Writes; state.Stop(); state.Refresh(); Check(music.Writes==writes,"repeated stop and inactive refresh have no mixer writes");
            music.Value=0.55f; state.Start(0); Volume(music,0,"zero silences other applications"); Volume(own,0.9f,"zero leaves this application audible");
            music.Value=0.7f; state.Refresh(); Volume(music,0,"zero stays silent if an application changes its volume during speech");
            state.Start(100); Volume(music,0.55f,"switching to 100 restores the newest original level");
            int enumerations=backend.Enumerations; state.Refresh(); Check(backend.Enumerations==enumerations,"100 stops session monitoring");
        }
        backend.Released();
    }

    static void FailuresAndDisposal() {
        var backend=new Backend(); var first=backend.Add("first",7,0.8f); var second=backend.Add("second",8,0.4f);
        using(var state=new OtherApplicationAudioState(backend,OwnProcess)) {
            state.Start(25); first.WriteFailures=1; state.Stop();
            Volume(second,0.4f,"one failed restoration does not prevent another session restoring");
            state.Refresh(); Volume(first,0.8f,"failed restoration retries while inactive"); Check(state.PendingRestores==0,"successful retry clears pending restoration");
            first.ReadFailures=1; state.Start(50); Volume(first,0.8f,"inaccessible original volume is not overwritten"); Volume(second,0.2f,"one inaccessible session does not prevent other attenuation");
            state.Refresh(); Volume(first,0.4f,"session becomes eligible after transient read failure"); state.Stop();
            first.WriteThenFailures=1; state.Start(50); state.Stop();
            Volume(first,0.8f,"a write that throws after applying still retains restoration data"); Volume(second,0.4f,"other sessions restore after partial write failure");
            state.Start(50); Volume(first,0.4f,"active playback is attenuated before enumeration failure");
            backend.FailAfter=1; state.Refresh();
            Volume(first,0.8f,"enumeration failure restores previously modified sessions"); Volume(second,0.4f,"enumeration failure also restores sessions absent from the partial list");
            int enumerations=backend.Enumerations; state.Refresh(); Check(backend.Enumerations==enumerations,"enumeration failure deactivates ducking safely");
            state.Start(0); Volume(first,0,"new playback can recover after enumeration failure");
        }
        Volume(first,0.8f,"dispose restores active session"); Volume(second,0.4f,"dispose restores every active session"); backend.Released();
    }

    static void Configuration(string root,Dictionary<string,object> report) {
        string parent=Path.GetFullPath(Path.Combine(root,"runtime","other-audio-test-work"));
        string fixture=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
        Check(fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture containment");
        Directory.CreateDirectory(fixture); File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1});
        var serializer=SettingsStorage.Serializer(); var original=new Settings { WhisperModel="model.bin" };
        Check(original.OtherApplicationVolume==100,"new installation defaults to 100");
        var raw=serializer.Deserialize<Dictionary<string,object>>(serializer.Serialize(original)); raw.Remove("OtherApplicationVolume");
        string config=Path.Combine(fixture,"config.json"); File.WriteAllText(config,serializer.Serialize(raw),new UTF8Encoding(false)); byte[] before=File.ReadAllBytes(config);
        Check(Settings.Load(fixture).OtherApplicationVolume==100,"older configurations default to 100");
        var editor=SettingsStorage.Open(fixture); Check(editor.Value.OtherApplicationVolume==100 && !editor.HasChanges(editor.Value),"older configuration opens without unsaved changes");
        Check(before.SequenceEqual(File.ReadAllBytes(config)),"loading older configuration never rewrites it");
        foreach(int percentage in new[]{0,37,100}) {
            var changed=SettingsStorage.Clone(editor.Value); changed.OtherApplicationVolume=percentage;
            changed.Validate(); SettingsStorage.Validate(changed,fixture); editor.Save(changed);
            Check(Settings.Load(fixture).OtherApplicationVolume==percentage,"percentage survives save and reload: "+percentage);
        }
        before=File.ReadAllBytes(config);
        foreach(int percentage in new[]{-1,101}) {
            var invalid=SettingsStorage.Clone(editor.Value); invalid.OtherApplicationVolume=percentage;
            Rejected(()=>invalid.Validate(),"resident rejects invalid percentage: "+percentage);
            Rejected(()=>editor.Save(invalid),"settings save rejects invalid percentage: "+percentage);
            Check(before.SequenceEqual(File.ReadAllBytes(config)),"invalid percentage leaves saved configuration untouched");
        }
        report["settingsFixture"]=fixture;
    }

    static void Wait(Func<bool> complete,int milliseconds,string message) {
        var timer=Stopwatch.StartNew();
        while(!complete() && timer.ElapsedMilliseconds<milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        Check(complete(),message);
    }
    static void PlaybackLifecycle(Dictionary<string,object> report) {
        var devices=SpeakerDevices.List(); Check(devices.Count>0,"native speech output is available for silent lifecycle test");
        var routes=new List<object>();
        foreach(var device in new SpeakerDevice[]{null,devices[0]}) {
            var backend=new Backend(); var music=backend.Add("lifecycle-music",7,0.8f); var own=backend.Add("lifecycle-own",OwnProcess,0.9f);
            var quiet=new Settings { SpeechVolume=0,OtherApplicationVolume=0 };
            using(var audio=new OtherApplicationAudio(backend,OwnProcess))
            using(var voice=new SpeechPlayback(quiet,device,audio)) {
                int completed=0; voice.SpeakCompleted+=delegate { Interlocked.Increment(ref completed); };
                voice.SpeakAsync("音声確認です。");
                Volume(music,0,"other volume is reduced before speech starts"); Volume(own,0.9f,"speech leaves own process unchanged");
                Wait(()=>Volatile.Read(ref completed)>0 && voice.IsIdle,7000,"silent speech completes normally");
                Volume(music,0.8f,"natural speech completion restores original level"); Check(voice.Error=="","silent speech completes without error");
                string longSpeech=string.Concat(Enumerable.Repeat("キャンセルと再生を確認しています。",40));
                voice.SpeakAsync(longSpeech); Wait(()=>voice.State==SynthesizerState.Speaking,2500,"long silent speech starts");
                voice.SpeakAsyncCancelAll(); Volume(music,0.8f,"cancellation restores original level immediately");
                voice.SpeakAsync(longSpeech); Volume(music,0,"immediate replay reduces original level again");
                var replay=Stopwatch.StartNew();
                while(replay.ElapsedMilliseconds<180) { Application.DoEvents(); Thread.Sleep(10); }
                Check(!voice.IsIdle,"replay is still active while canceled completion events arrive");
                Volume(music,0,"stale canceled completion cannot restore volume during replay");
                voice.SpeakAsyncCancelAll();
                Wait(()=>voice.IsIdle,2500,"replay cancellation drains playback"); Volume(music,0.8f,"replay cancellation restores level");
                voice.SpeakAsync(longSpeech); Wait(()=>voice.State==SynthesizerState.Speaking,2500,"speech starts before disposal");
                voice.Dispose(); Volume(music,0.8f,"disposing active speech restores original level");
                Check(own.Reads==0 && own.Writes==0,"lifecycle never touches own process level");
            }
            backend.Released(); routes.Add(device==null?"Windows routing":device.Id);
        }
        report["silentPlaybackRoutes"]=routes;
    }

    internal static void Run(string root,Dictionary<string,object> report) {
        LevelsAndRestoration(); FailuresAndDisposal(); Configuration(root,report); PlaybackLifecycle(report);
        report["realMixerModified"]=false;
        report["audiblePlayback"]=false;
        report["checks"]="relative volume / own process excluded / 100 no-op / 0 silent / repeated start / new sessions / percentage changes / stop and disposal restore / transient failures / partial writes / old config default / save and reload / invalid values / managed and native silent speech completion, cancel, immediate replay, disposal";
    }
}
}

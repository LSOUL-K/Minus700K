using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Windows.Forms;

namespace Minus700K {
internal static class SpeakerDiagnostics {
    static void Check(bool condition,string message) { if(!condition) throw new Exception("speaker: "+message); }
    static void Wait(Func<bool> done,int milliseconds,string message) {
        var timer=Stopwatch.StartNew();
        while(!done() && timer.ElapsedMilliseconds<milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
        Check(done(),message);
    }
    internal static void Run(Settings settings,Dictionary<string,object> report) {
        var devices=SpeakerDevices.List();
        Check(devices.Count>0,"at least one shared speech/cue endpoint available");
        Check(devices.Select(d=>d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()==devices.Count,"endpoint IDs are unique");
        Check(SpeakerDevices.Resolve("")==null,"empty speaker preserves Windows routing");
        Check(SpeakerDevices.Resolve("fixture-missing-device")==null,"disconnected speaker safely uses Windows routing");
        var rows=new List<object>();
        foreach(var device in new SpeakerDevice[]{null}.Concat(devices)) {
            var quiet=SettingsStorage.Clone(settings); quiet.SpeechVolume=0; quiet.CueVolume=0; quiet.SpeakerDevice=device==null?"":device.Id;
            if(device!=null) Check(SpeakerDevices.EndpointForWaveDevice(SpeakerDevices.WaveDevice(device.Id))==device.Id,"stable endpoint maps back to current WinMM device");
            using(var voice=new SpeechPlayback(quiet,device)) using(var cues=new CuePlayer(0,quiet.SpeakerDevice)) {
                Check(cues.WaitReady(5000),"silent cue stream opens: "+cues.Error);
                if(device!=null) Check(cues.OutputDeviceId==device.Id && voice.OutputDeviceId==device.Id,"speech and cues target the same selected endpoint");
                int started=0,completed=0;
                voice.SpeakStarted+=delegate { Interlocked.Increment(ref started); };
                voice.SpeakCompleted+=delegate { Interlocked.Increment(ref completed); };
                voice.SpeakAsync("音声確認です。");
                Wait(()=>Volatile.Read(ref completed)>=1 && voice.State==SynthesizerState.Ready,7000,"silent speech completes");
                Check(Volatile.Read(ref started)>=1 && voice.Error=="","speech start/completion without error");
                voice.SpeakAsync(string.Concat(Enumerable.Repeat("途中で中止する音声です。",40)));
                Wait(()=>voice.State==SynthesizerState.Speaking,2000,"long speech starts");
                var cancel=Stopwatch.StartNew(); voice.SpeakAsyncCancelAll();
                Wait(()=>voice.State==SynthesizerState.Ready && Volatile.Read(ref completed)>=2,2500,"cancel stops pending speech promptly");
                long cancelMilliseconds=cancel.ElapsedMilliseconds;
                voice.SpeakAsync("再生確認です。");
                Wait(()=>voice.State==SynthesizerState.Ready && Volatile.Read(ref completed)>=3,7000,"speech works after cancellation");
                Check(voice.Error=="","replay has no speech error");
                int cue=cues.Play(660); Wait(()=>cues.CompletedId>=cue,2500,"selected endpoint finishes silent cue");
                rows.Add(new { name=device==null?"Windows routing":device.Name,endpoint=device==null?"":device.Id,cueEndpoint=cues.OutputDeviceId,starts=started,completions=completed,cancelMilliseconds=cancelMilliseconds });
            }
        }
        report["outputs"]=rows; report["audiblePlayback"]=false;
        report["checks"]="stable endpoint ID mapping / shared speech and cue output / unchanged Windows routing / disconnected output fallback / silent speech / cancel / replay after cancel / silent cue completion";
    }
}
}

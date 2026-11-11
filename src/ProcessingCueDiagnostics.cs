using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Web.Script.Serialization;

namespace Minus700K {
internal static class ProcessingCueDiagnostics {
    static void Require(bool condition,string description) { if(!condition) throw new Exception(description); }
    static void WaitFor(Func<bool> condition,int timeout,string description) {
        var elapsed=Stopwatch.StartNew();
        while(!condition() && elapsed.ElapsedMilliseconds<timeout) Thread.Sleep(10);
        Require(condition(),description);
    }
    internal static void Run(Settings settings,Dictionary<string,object> report,bool audio) {
        var serializer=new JavaScriptSerializer();
        var defaults=serializer.Deserialize<Settings>("{}");
        Require(defaults.PlayProcessingCues && defaults.ProcessingCueIntervalSeconds==3,"existing settings default to enabled at three seconds");
        var disabled=serializer.Deserialize<Settings>("{\"PlayProcessingCues\":false,\"PlayCues\":true}");
        Require(!disabled.PlayProcessingCues && disabled.PlayCues,"progress sound can be disabled independently");
        foreach(int invalid in new[]{0,61}) {
            defaults.ProcessingCueIntervalSeconds=invalid; bool rejected=false;
            try { defaults.Validate(); } catch(Exception) { rejected=true; }
            Require(rejected,"invalid progress interval rejected");
        }
        var schedule=new ProcessingCueSchedule();
        Require(!schedule.TryStart(0),"idle/recording starts silent");
        schedule.SetEnabled(true,3000,0);
        Require(!schedule.TryStart(2999) && schedule.TryStart(3000) && !schedule.TryStart(3000),"first cue delayed and emitted only once");
        schedule.SetEnabled(true,3000,4500); // Recognition -> Codex -> retry must not reset the cadence.
        Require(!schedule.TryStart(5999) && schedule.TryStart(6000),"phase transitions preserve cadence");
        Require(schedule.TryStart(20000) && !schedule.TryStart(20000),"delayed audio worker never emits a backlog");
        int version=schedule.Version;
        schedule.SetSpeaking(true,20500);
        Require(!schedule.CanPlay && schedule.Version!=version && !schedule.TryStart(25000),"announcement/speech suppresses current and future progress cues");
        schedule.SetSpeaking(false,26000);
        Require(!schedule.TryStart(28999) && schedule.TryStart(29000),"progress resumes after speech with a fresh interval");
        version=schedule.Version; schedule.SetEnabled(false,3000,29010);
        Require(!schedule.CanPlay && schedule.Version!=version && !schedule.TryStart(60000),"completion/cancel/error invalidates active tone and future cues");
        schedule.SetEnabled(true,3000,61000);
        Require(!schedule.TryStart(63999) && schedule.TryStart(64000),"next request cannot inherit old cue deadline");
        report["checks"]="defaults / independent opt-out / interval bounds / delayed start / periodic cadence / no backlog / speech suppression / completion and cancellation / next request";
        if(!audio) return;
        using(var cues=new CuePlayer(settings.CueVolume)) {
            Require(cues.WaitReady(5000),"audio device initialized: "+cues.Error);
            report["outputDevice"]=cues.DeviceName;
            cues.SetProcessing(false,1000); Thread.Sleep(1200);
            Require(cues.CompletedProcessingCues==0,"OFF produces no progress audio");
            var elapsed=Stopwatch.StartNew(); cues.SetProcessing(true,1000);
            Require(elapsed.ElapsedMilliseconds<100,"enabling progress never blocks the caller");
            WaitFor(()=>cues.CompletedProcessingCues>=2,4000,"periodic progress audio completes on the device");
            cues.SetSpeaking(true); Thread.Sleep(150); // Drain the three existing 20 ms audio buffers.
            int count=cues.CompletedProcessingCues; Thread.Sleep(1200);
            Require(cues.CompletedProcessingCues==count,"no progress audio while speech is active");
            cues.SetSpeaking(false);
            WaitFor(()=>cues.CompletedProcessingCues>count,2500,"progress audio resumes after an announcement");
            cues.SetProcessing(false,1000); Thread.Sleep(150); count=cues.CompletedProcessingCues;
            Thread.Sleep(1200);
            Require(cues.CompletedProcessingCues==count,"no progress audio after stop");
            int normal=cues.Play(660);
            WaitFor(()=>cues.CompletedId>=normal,2000,"normal end cue still works with progress disabled");
            Require(cues.Error=="","audio driver remained healthy: "+cues.Error);
            report["completedProcessingCues"]=cues.CompletedProcessingCues;
        }
    }
}
}

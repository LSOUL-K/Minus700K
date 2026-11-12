using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Minus700K {
// Uses synthetic PCM and fake recorders only. It never opens a microphone.
internal static class ExclusiveMicrophoneDiagnostics {
    static void Check(bool condition,string message) { if(!condition) throw new Exception("exclusive microphone: "+message); }
    static void Reject(Action action,string message) { bool rejected=false; try { action(); } catch(Exception) { rejected=true; } Check(rejected,message); }
    static short Sample(byte[] pcm,int frame) { return (short)(pcm[frame*2]|pcm[frame*2+1]<<8); }
    static byte[] Convert(byte[] bytes,int bits,int validBits,bool floating) { return MicrophonePcm.ToMono16k(bytes,16000,1,bits,validBits,floating); }
    static byte[] Floats(params float[] values) { return values.SelectMany(BitConverter.GetBytes).ToArray(); }
    static byte[] Doubles(params double[] values) { return values.SelectMany(BitConverter.GetBytes).ToArray(); }
    static double Rms(byte[] pcm,int skip) { double total=0; int frames=pcm.Length/2; for(int i=skip;i<frames-skip;i++) { double value=Sample(pcm,i)/32768.0; total+=value*value; } return Math.Sqrt(total/(frames-2*skip)); }

    static void Conversion(Dictionary<string,object> report) {
        var pcm16=new byte[65536*2]; for(int i=0;i<65536;i++) { pcm16[2*i]=(byte)i; pcm16[2*i+1]=(byte)(i>>8); }
        Check(Convert(pcm16,16,16,false).SequenceEqual(pcm16),"16k PCM16 preserves every representable value exactly");
        byte[] converted=Convert(new byte[]{0,128,255},8,8,false);
        Check(Sample(converted,0)==-32768 && Sample(converted,1)==0 && Sample(converted,2)==32512,"unsigned PCM8 conversion");
        converted=Convert(new byte[]{0,0,64,0,0,192,255,255,127},24,24,false);
        Check(Sample(converted,0)==16384 && Sample(converted,1)==-16384 && Sample(converted,2)==32767,"signed PCM24 conversion and clipping");
        byte[] pcm32=new int[]{0x40000000,unchecked((int)0xC0000000),int.MinValue,int.MaxValue}.SelectMany(BitConverter.GetBytes).ToArray();
        converted=Convert(pcm32,32,32,false);
        Check(Sample(converted,0)==16384 && Sample(converted,1)==-16384 && Sample(converted,2)==-32768 && Sample(converted,3)==32767,"signed PCM32 conversion");
        Check(Convert(pcm32,32,24,false).SequenceEqual(converted),"extensible PCM valid bits are left aligned in the container");
        converted=Convert(Floats(0.5f,-0.5f,2,-2,float.NaN,float.PositiveInfinity),32,32,true);
        Check(new short[]{16384,-16384,32767,-32768,0,0}.Select((value,index)=>Sample(converted,index)==value).All(value=>value),"float32 clipping and non-finite samples");
        converted=Convert(Doubles(0.25,-0.25,double.NaN,double.NegativeInfinity),64,64,true);
        Check(new short[]{8192,-8192,0,0}.Select((value,index)=>Sample(converted,index)==value).All(value=>value),"float64 conversion");
        converted=MicrophonePcm.ToMono16k(Floats(0.5f,0.25f,0.75f,-0.75f),16000,2,32,32,true);
        Check(Sample(converted,0)==12288 && Sample(converted,1)==0,"interleaved stereo averages channels without doubling volume");
        Check(MicrophonePcm.ToMono16k(new byte[0],48000,2,16,16,false).Length==0,"empty capture remains empty");
        foreach(Action invalid in new Action[]{
            ()=>MicrophonePcm.ToMono16k(null,16000,1,16,16,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],0,1,16,16,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],16000,0,16,16,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],16000,33,16,16,false),
            ()=>MicrophonePcm.ToMono16k(new byte[3],48000,2,16,16,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],16000,1,12,12,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],16000,1,16,17,false),
            ()=>MicrophonePcm.ToMono16k(new byte[4],16000,1,32,24,true)
        }) Reject(invalid,"malformed sample format/data is rejected before conversion");
        var frequencies=new List<object>();
        foreach(int rate in new[]{44100,48000,96000}) {
            double low=0,high=0;
            foreach(int frequency in new[]{1000,6000,10000}) {
                byte[] tone=new byte[rate*4];
                for(int i=0;i<rate;i++) Buffer.BlockCopy(BitConverter.GetBytes((float)(0.65*Math.Sin(2*Math.PI*frequency*i/rate))),0,tone,i*4,4);
                converted=MicrophonePcm.ToMono16k(tone,rate,1,32,32,true);
                Check(converted.Length==32000,"resampling preserves one-second duration at "+rate);
                double rms=Rms(converted,400);
                if(frequency==1000) low=rms;
                if(frequency==10000) high=rms;
                if(frequency<8000) Check(Math.Abs(rms/(0.65/Math.Sqrt(2))-1)<0.015,"speech passband amplitude preserved: "+rate+" / "+frequency);
                else Check(rms<0.0005,"above-Nyquist tone is suppressed before downsampling: "+rate);
            }
            frequencies.Add(new { inputRate=rate,passbandRms=low,stopbandRms=high,aliasSuppressionDb=20*Math.Log10(Math.Max(1e-12,high)/low) });
            byte[] dc=Enumerable.Repeat((short)8192,rate/100).SelectMany(BitConverter.GetBytes).ToArray();
            converted=MicrophonePcm.ToMono16k(dc,rate,1,16,16,false);
            Check(converted.Length==320 && Enumerable.Range(0,160).All(index=>Sample(converted,index)==8192),"resampler preserves DC at both recording boundaries");
        }
        converted=MicrophonePcm.ToMono16k(Enumerable.Repeat((short)8192,8000).SelectMany(BitConverter.GetBytes).ToArray(),8000,1,16,16,false);
        Check(converted.Length==32000 && Sample(converted,8000)==8192,"low-rate microphone is upsampled to 16k");
        report["resampling"]=frequencies;
    }

    sealed class Recorder : IAudioRecorder {
        internal int Starts,Stops,Disposals; internal bool FailStart,FailStop;
        internal readonly byte[] Data=new byte[]{1,2,3,4};
        public void Start() { Starts++; if(FailStart) throw new IOException("fixture start failure"); }
        public byte[] Stop() { Stops++; if(FailStop) throw new IOException("fixture stop failure"); return Data; }
        public void Dispose() { Disposals++; }
    }
    sealed class CaptureSession : IExclusiveCaptureSession {
        internal int ThreadId,Starts,Stops,Disposals; internal bool FailStart,FailStop,Malformed; internal volatile bool FailRead;
        internal readonly ManualResetEvent Read=new ManualResetEvent(false); bool first,tail,stopped;
        internal void Touch() {
            int current=Thread.CurrentThread.ManagedThreadId;
            if(ThreadId==0) ThreadId=current;
            Check(ThreadId==current && Thread.CurrentThread.GetApartmentState()==ApartmentState.STA,"all native capture work stays on one STA thread");
        }
        public int SampleRate { get { Touch(); return 16000; } }
        public int Channels { get { Touch(); return 1; } }
        public int BitsPerSample { get { Touch(); return Malformed?24:16; } }
        public int ValidBits { get { Touch(); return Malformed?24:16; } }
        public bool FloatingPoint { get { Touch(); return false; } }
        public void Start() { Touch(); Starts++; if(FailStart) throw new IOException("fixture native start failure"); }
        public bool TryRead(out byte[] packet) {
            Touch(); packet=null;
            if(FailRead) { Read.Set(); throw new IOException("fixture capture failure"); }
            if(!first) { first=true; packet=new byte[]{1,2}; Read.Set(); return true; }
            if(stopped && !tail) { tail=true; packet=new byte[]{3,4}; return true; }
            return false;
        }
        public void Stop() { Touch(); Stops++; stopped=true; if(FailStop) throw new IOException("fixture native stop failure"); }
        public void Dispose() { Touch(); Disposals++; }
    }
    static void NativeLifecycle() {
        int caller=Thread.CurrentThread.ManagedThreadId,factories=0; var capture=new CaptureSession();
        using(var recorder=new ExclusiveMicrophone(3,id=>{Check(id==3,"selected device reaches native factory"); Interlocked.Increment(ref factories); capture.Touch(); return capture;})) {
            Check(factories==0,"exclusive constructor never opens a microphone"); recorder.Start();
            Check(capture.Read.WaitOne(1500),"capture worker reads synthetic packet");
            byte[] data=recorder.Stop();
            Check(data.SequenceEqual(new byte[]{1,2,3,4}),"stop drains final buffered samples before conversion");
            Check(capture.Stops==1 && capture.Disposals==1 && capture.ThreadId!=caller,"native endpoint released on its worker before returning PCM");
            Check(recorder.Stop().Length==0 && capture.Disposals==1,"repeated native stop does not access released capture");
        }
        capture.Read.Dispose();
        foreach(string failure in new[]{"start","read","stop","conversion"}) {
            var failed=new CaptureSession { FailStart=failure=="start",FailStop=failure=="stop",Malformed=failure=="conversion" };
            using(var recorder=new ExclusiveMicrophone(0,id=>failed)) {
                if(failed.FailStart) Reject(recorder.Start,"native start error surfaces");
                else {
                    recorder.Start();
                    if(failure=="read") { failed.FailRead=true; Check(SpinWait.SpinUntil(()=>recorder.CaptureError!=null,1500),"capture error becomes observable before stop"); }
                    else Check(failed.Read.WaitOne(1500),"native test reaches read: "+failure);
                    Reject(()=>recorder.Stop(),"native capture/stop/conversion error surfaces: "+failure);
                }
                Check(failed.Disposals==1,"native endpoint released after error: "+failure);
            }
            Check(failed.Disposals==1,"failed native session disposed once: "+failure); failed.Read.Dispose();
        }
        var recovered=new CaptureSession(); int attempts=0;
        using(var recorder=new ExclusiveMicrophone(0,id=>{if(Interlocked.Increment(ref attempts)==1) throw new IOException("fixture factory failure"); return recovered;})) {
            Reject(recorder.Start,"native factory failure surfaces"); recorder.Start(); recorder.Stop();
            Check(attempts==2 && recovered.Disposals==1,"failed acquisition releases state so a new recording can retry");
        }
        recovered.Read.Dispose();
        var cancelled=new CaptureSession { Malformed=true };
        using(var recorder=new ExclusiveMicrophone(0,id=>cancelled)) {
            recorder.Start(); recorder.Dispose(); Check(cancelled.Disposals==1,"active Dispose releases native session without conversion");
        }
        cancelled.Read.Dispose();
        var delayed=new CaptureSession();
        using(var recorder=new ExclusiveMicrophone(0,id=>{Thread.Sleep(100); return delayed;},20)) {
            Reject(recorder.Start,"slow device activation times out");
            Check(delayed.Starts==0 && delayed.Disposals==1,"timed-out activation releases device without starting capture");
        }
        delayed.Read.Dispose();
        var interrupted=new CaptureSession(); int cancellationPolls=0,readyPolls=0;
        using(var entered=new ManualResetEvent(false))
        using(var recorder=new ExclusiveMicrophone(0,id=>{interrupted.Touch(); entered.Set(); Thread.Sleep(150); return interrupted;})) {
            bool acquisitionCancelled=false;
            try {
                recorder.Start(delegate {
                    Check(Thread.CurrentThread.ManagedThreadId==caller,"acquisition cancellation is polled on the caller thread");
                    cancellationPolls++;
                    return entered.WaitOne(0) && ++readyPolls>=2;
                });
            } catch(OperationCanceledException) { acquisitionCancelled=true; }
            Check(acquisitionCancelled && cancellationPolls>=2,"cancellation interrupts acquisition before its normal timeout");
            Check(interrupted.Starts==0 && interrupted.Disposals==1,"cancelled acquisition releases the endpoint without starting capture");
        }
        Check(interrupted.Disposals==1,"cancelled acquisition is disposed once"); interrupted.Read.Dispose();
    }
    static void Lifecycle() {
        int sharedCalls=0,exclusiveCalls=0; var shared=new Recorder(); var exclusive=new Recorder();
        using(var recorder=new RecordingMicrophone(false,()=>{sharedCalls++;return shared;},()=>{exclusiveCalls++;return exclusive;})) {
            Check(sharedCalls==1 && exclusiveCalls==0,"OFF retains shared capture construction");
            recorder.Start(); Check(recorder.Stop()==shared.Data,"OFF returns shared audio"); recorder.Start(); recorder.Stop();
            Check(shared.Starts==2 && shared.Stops==2 && shared.Disposals==0 && exclusiveCalls==0,"shared recorder is reusable until app disposal");
        }
        Check(shared.Disposals==1,"OFF releases shared recorder once");
        sharedCalls=exclusiveCalls=0; var captures=new List<Recorder>();
        using(var recorder=new RecordingMicrophone(true,()=>{sharedCalls++;return new Recorder();},()=>{exclusiveCalls++; var capture=new Recorder(); captures.Add(capture); return capture;})) {
            Check(sharedCalls==0 && exclusiveCalls==0,"ON leaves microphone free while idle");
            recorder.Start(); Reject(recorder.Start,"duplicate start is rejected"); Check(exclusiveCalls==1,"duplicate start never reserves a second endpoint");
            byte[] data=recorder.Stop(); Check(data==captures[0].Data && captures[0].Disposals==1,"normal stop releases exclusive access before returning audio");
            Check(recorder.Stop()==data && captures[0].Stops==1,"repeated stop returns completed audio without touching released recorder");
            recorder.Start(); Check(exclusiveCalls==2 && sharedCalls==0,"each recording reacquires exclusively with no shared fallback");
        }
        Check(captures[1].Disposals==1 && sharedCalls==0,"cancel/app disposal releases active exclusive recorder");
        foreach(bool startFailure in new[]{true,false}) {
            var failed=new Recorder { FailStart=startFailure,FailStop=!startFailure }; sharedCalls=0;
            using(var recorder=new RecordingMicrophone(true,()=>{sharedCalls++;return new Recorder();},()=>failed)) {
                if(startFailure) Reject(recorder.Start,"exclusive start error surfaces");
                else { recorder.Start(); Reject(()=>recorder.Stop(),"exclusive stop error surfaces"); }
                Check(failed.Disposals==1 && sharedCalls==0,"exclusive errors release capture and never fall back to shared mode");
            }
            Check(failed.Disposals==1,"error cleanup is not repeated on wrapper disposal");
        }
        sharedCalls=0;
        using(var recorder=new RecordingMicrophone(true,()=>{sharedCalls++;return new Recorder();},()=>{throw new IOException("endpoint busy");})) {
            Reject(recorder.Start,"busy endpoint creation surfaces as an error"); Check(sharedCalls==0,"busy endpoint never falls back to shared capture");
        }
    }

    static void Configuration(string root,Dictionary<string,object> report) {
        string parent=Path.GetFullPath(Path.Combine(root,"runtime","exclusive-microphone-test-work"));
        string fixture=Path.GetFullPath(Path.Combine(parent,Guid.NewGuid().ToString("N")));
        Check(fixture.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase),"fixture stays in test directory"); Directory.CreateDirectory(fixture);
        File.WriteAllBytes(Path.Combine(fixture,"model.bin"),new byte[]{1});
        var original=new Settings { WhisperModel="model.bin" }; Check(!original.ExclusiveMicrophone,"new installations default OFF");
        var json=SettingsStorage.Serializer(); var raw=json.Deserialize<Dictionary<string,object>>(json.Serialize(original)); raw.Remove("ExclusiveMicrophone");
        string config=Path.Combine(fixture,"config.json"); File.WriteAllText(config,json.Serialize(raw),new UTF8Encoding(false)); byte[] before=File.ReadAllBytes(config);
        Check(!Settings.Load(fixture).ExclusiveMicrophone,"older configurations default OFF"); var edit=SettingsStorage.Open(fixture);
        Check(!edit.Value.ExclusiveMicrophone && !edit.HasChanges(edit.Value) && before.SequenceEqual(File.ReadAllBytes(config)),"loading older config never writes or marks it edited");
        foreach(bool enabled in new[]{true,false}) {
            var changed=SettingsStorage.Clone(edit.Value); changed.ExclusiveMicrophone=enabled; edit.Save(changed);
            Check(Settings.Load(fixture).ExclusiveMicrophone==enabled,"exclusive preference survives save/reload: "+enabled);
        }
        report["settingsFixture"]=fixture;
    }
    internal static void Run(string root,Dictionary<string,object> report) {
        Conversion(report); Lifecycle(); NativeLifecycle(); Configuration(root,report);
        report["realMicrophoneOpened"]=false;
        report["checks"]="PCM formats / valid bits / clipping / stereo mix / duration / passband / alias rejection / shared mode compatibility / exclusive reservation lifecycle / native STA ownership and final-buffer drain / capture errors and timeout cleanup / fail-closed errors / old config default OFF / save and reload";
    }
}
}

using System;

namespace Minus700K {
internal interface IAudioRecorder : IDisposable {
    void Start();
    byte[] Stop();
}

// Shared recording preserves the existing WinMM lifetime. Exclusive recording
// owns a device only between Start and Stop, including failed starts and stops.
internal sealed class RecordingMicrophone : IAudioRecorder {
    readonly object gate=new object();
    readonly bool exclusive;
    readonly Func<IAudioRecorder> exclusiveFactory;
    IAudioRecorder recorder;
    byte[] last=new byte[0];
    bool disposed,recording;
    internal Exception CaptureError { get { lock(gate) { var source=recorder as ExclusiveMicrophone; return source==null?null:source.CaptureError; } } }

    internal RecordingMicrophone(int device,bool exclusive)
        : this(exclusive,()=>new WaveRecorder(device),()=>new ExclusiveMicrophone(device)) { }
    internal RecordingMicrophone(bool exclusive,Func<IAudioRecorder> sharedFactory,Func<IAudioRecorder> exclusiveFactory) {
        this.exclusive=exclusive; this.exclusiveFactory=exclusiveFactory;
        // Never open a shared microphone, even briefly, when isolation is enabled.
        if(!exclusive) recorder=sharedFactory();
    }
    public void Start() { Start(null); }
    internal void Start(Func<bool> cancelled) {
        lock(gate) {
            if(disposed) throw new ObjectDisposedException("RecordingMicrophone");
            if(recording) throw new InvalidOperationException("Recording is already active.");
            last=new byte[0];
            try {
                if(exclusive) recorder=exclusiveFactory();
                var native=exclusive?recorder as ExclusiveMicrophone:null;
                if(native!=null) native.Start(cancelled); else recorder.Start();
                recording=true;
            } catch {
                if(exclusive) Release();
                throw;
            }
        }
    }
    public byte[] Stop() {
        lock(gate) {
            if(disposed || recorder==null) return last;
            try { last=recorder.Stop(); return last; }
            finally { recording=false; if(exclusive) Release(); }
        }
    }
    void Release() {
        var previous=recorder; recorder=null; recording=false;
        if(previous!=null) previous.Dispose();
    }
    public void Dispose() {
        lock(gate) {
            if(disposed) return;
            disposed=true;
            try { Release(); } finally { last=new byte[0]; }
        }
    }
}
}

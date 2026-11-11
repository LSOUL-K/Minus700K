using System;
using System.Speech.Synthesis;
using System.Windows.Forms;

namespace Minus700K {
// The default route retains the existing System.Speech behavior. An explicit
// speaker uses SAPI's documented AudioOutput property and asynchronous Speak.
// All methods and timer callbacks run on the resident/settings UI thread.
internal sealed class SpeechPlayback : IDisposable {
    SpeechSynthesizer managed; object native; Timer completion; Prompt pending;
    bool disposed,speaking;
    internal event EventHandler SpeakStarted,SpeakCompleted;
    internal SynthesizerState State { get { return managed!=null?managed.State:speaking?SynthesizerState.Speaking:SynthesizerState.Ready; } }
    internal bool IsIdle { get { return State==SynthesizerState.Ready && (pending==null || pending.IsCompleted); } }
    internal string OutputDeviceId { get; private set; }
    internal string Error { get; private set; }
    internal SpeechPlayback(Settings settings,SpeakerDevice device) {
        OutputDeviceId=""; Error="";
        try {
            if(device==null) {
                managed=Speech.Create(settings);
                managed.SpeakStarted+=delegate { if(!disposed) Raise(SpeakStarted); };
                managed.SpeakCompleted+=delegate(object sender,SpeakCompletedEventArgs e) { if(e.Error!=null) Error=e.Error.Message; if(!disposed) Raise(SpeakCompleted); };
                return;
            }
            string voiceName; using(var available=Speech.Create(settings)) voiceName=available.Voice.Name;
            native=ComSpeech.Create();
            SetToken("GetAudioOutputs","AudioOutput",t=>string.Equals((string)ComSpeech.Get(t,"Id"),device.TokenId,StringComparison.OrdinalIgnoreCase));
            SetToken("GetVoices","Voice",t=>string.Equals((string)ComSpeech.Call(t,"GetAttribute","Name"),voiceName,StringComparison.OrdinalIgnoreCase));
            ComSpeech.Set(native,"Rate",settings.SpeechRate); ComSpeech.Set(native,"Volume",settings.SpeechVolume);
            object actualOutput=ComSpeech.Get(native,"AudioOutput");
            try { string id=(string)ComSpeech.Get(actualOutput,"Id"); OutputDeviceId=id.Substring(id.LastIndexOf('\\')+1); }
            finally { ComSpeech.Release(actualOutput); }
            if(!string.Equals(OutputDeviceId,device.Id,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(L10n.T("messages.SpeechPlayback.001"));
            completion=new Timer { Interval=30 }; completion.Tick+=delegate { Poll(); };
        } catch { Dispose(); throw; }
    }
    void SetToken(string listMethod,string property,Func<object,bool> matches) {
        object tokens=ComSpeech.Call(native,listMethod,"","");
        try {
            int count=Convert.ToInt32(ComSpeech.Get(tokens,"Count"));
            for(int i=0;i<count;i++) {
                object token=ComSpeech.Call(tokens,"Item",i);
                try { if(matches(token)) { ComSpeech.Set(native,property,token); return; } }
                finally { ComSpeech.Release(token); }
            }
            throw new InvalidOperationException(L10n.T("messages.SpeechPlayback.002"));
        } finally { ComSpeech.Release(tokens); }
    }
    void Raise(EventHandler handler) { if(handler!=null) handler(this,EventArgs.Empty); }
    internal void SpeakAsync(string text) {
        if(disposed) return; Error="";
        if(managed!=null) { pending=managed.SpeakAsync(text); return; }
        // SVSFlagsAsync | SVSFIsNotXML: user/model text is always literal text.
        try { ComSpeech.Call(native,"Speak",text,17); speaking=true; completion.Start(); Raise(SpeakStarted); }
        catch(Exception e) { Error=e.Message; speaking=false; completion.Stop(); Log.Write("speaker playback start failed: "+e.GetType().Name); Raise(SpeakCompleted); }
    }
    void Poll() {
        if(disposed || !speaking) return;
        try { if(!Convert.ToBoolean(ComSpeech.Call(native,"WaitUntilDone",0))) return; }
        catch(Exception e) { Error=e.Message; Log.Write("speaker playback failed: "+e.GetType().Name); }
        speaking=false; completion.Stop(); Raise(SpeakCompleted);
    }
    internal void SpeakAsyncCancelAll() {
        if(disposed) return;
        if(managed!=null) { managed.SpeakAsyncCancelAll(); return; }
        bool wasSpeaking=speaking; speaking=false; completion.Stop();
        try { ComSpeech.Call(native,"Speak","",3); } // asynchronous purge
        catch(Exception e) { Error=e.Message; Log.Write("speaker cancel failed: "+e.GetType().Name); }
        if(wasSpeaking) Raise(SpeakCompleted);
    }
    public void Dispose() {
        if(disposed) return; disposed=true;
        if(completion!=null) { completion.Stop(); completion.Dispose(); completion=null; }
        if(managed!=null) { managed.SpeakAsyncCancelAll(); managed.Dispose(); managed=null; }
        if(native!=null) { try { ComSpeech.Call(native,"Speak","",3); } catch { } ComSpeech.Release(native); native=null; }
    }
}
}

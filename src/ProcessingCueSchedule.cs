using System;

namespace Minus700K {
// Called under CuePlayer's gate, using a monotonic clock. No timer callbacks or
// queued progress tones can outlive the request that enabled them.
internal sealed class ProcessingCueSchedule {
    bool enabled,speaking;
    int interval=3000;
    long next;
    internal int Version { get; private set; }
    internal bool CanPlay { get { return enabled && !speaking; } }
    internal void SetEnabled(bool value,int milliseconds,long now) {
        if(milliseconds<1000 || milliseconds>60000) throw new ArgumentOutOfRangeException("milliseconds");
        if(enabled==value && interval==milliseconds) return;
        enabled=value; interval=milliseconds; next=now+interval; Version++;
    }
    internal void SetSpeaking(bool value,long now) {
        if(speaking==value) return;
        speaking=value; next=now+interval; Version++;
    }
    internal bool TryStart(long now) {
        if(!CanPlay || now<next) return false;
        next=now+interval; return true;
    }
}
}

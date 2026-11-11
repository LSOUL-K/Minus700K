using System;
using System.Linq;

namespace Minus700K {
// A detached request snapshot, never a reference to a saved conversation.
internal sealed class RecentTurn {
    public string startedAt,windowTitle,application,question,answer;
    public string source="memory";
    public bool usedWeb;
    public int questionsAgo;
}
// Every request gets the complete bounded memory, independently of wording,
// foreground application and age. The model decides which exchanges are relevant.
internal static class RecentHistory {
    internal static RecentTurn[] Capture(Settings settings,Turn[] history) {
        var retained=history.Skip(Math.Max(0,history.Length-Math.Max(0,settings.RememberLastTurns))).ToArray();
        return retained.Select((turn,index)=>new RecentTurn { startedAt=turn.RecordedUtc.ToString("o"),
            windowTitle=turn.WindowTitle??"",application=turn.Application??"",question=turn.Question,answer=turn.Answer,
            usedWeb=turn.UsedWeb,questionsAgo=retained.Length-index }).ToArray();
    }
}
}

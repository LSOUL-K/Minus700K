using System;

namespace Minus700K {
// Resource preparation is reversible. Commit is called only after all settings
// files have been written, while the keyboard worker is paused at an idle point.
internal sealed class SettingsRuntimeUpdate : IDisposable {
    Action commit,rollback;
    internal SettingsRuntimeUpdate(Action commit,Action rollback) { this.commit=commit; this.rollback=rollback; }
    internal void Commit() { if(commit==null) return; commit(); commit=null; rollback=null; }
    public void Dispose() { var action=rollback; commit=null; rollback=null; if(action!=null) action(); }
}
}

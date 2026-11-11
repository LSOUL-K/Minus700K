using System;
using System.Diagnostics;
using System.Threading;

namespace Minus700K {
internal static class Installation {
    // This entry point runs before audio, the microphone, or the resident are
    // initialized. The installer uses its exit code even when launch is unchecked.
    internal static int ConfigureLanguageCommand(string root,string[] args) {
        try {
            if(args.Length!=2) throw new ArgumentException("Use --install-language ja or --install-language en.");
            ConfigureLanguage(root,args[1]);
            return 0;
        } catch(Exception e) {
            Log.Write("Installer language configuration failed: "+e);
            return 1;
        }
    }
    internal static void ConfigureLanguage(string root,string language) {
        if(!string.Equals(language,"ja",StringComparison.OrdinalIgnoreCase) && !string.Equals(language,"en",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Installer language must be ja or en.","language");
        string target=L10n.NormalizeLanguage(language);
        var session=SettingsStorage.Open(root);
        // Reinstalling in the same language must not reset later user choices
        // for recognition language or voice, or rewrite the settings file.
        if(session.Value.AppLanguage==target) return;
        var updated=SettingsStorage.Clone(session.Value);
        LocalizedDefaults.ApplyLanguage(updated,target);
        session.Save(updated);
    }
    internal static bool ShowExistingConnectionSettings() {
        // Another startup may own the mutex before it creates its IPC events.
        // Wait for that resident instead of opening an unrelated settings window.
        var deadline=Stopwatch.StartNew();
        while(deadline.ElapsedMilliseconds<5000) {
            try {
                using(var connection=EventWaitHandle.OpenExisting(ResidentIpc.ConnectEvent)) connection.Set();
                return true;
            } catch(WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
        }
        Log.Write("Post-install launch could not reach the existing resident.");
        return false;
    }
}
}

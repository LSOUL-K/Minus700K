using System;
using System.Threading;
using System.Windows.Forms;

namespace Minus700K {
internal static class StartupRecovery {
    internal static int RunResident(string root,bool postInstall) {
        while(true) {
            Settings settings=Settings.Load(root);
            L10n.SetLanguage(settings.AppLanguage);
            VoiceContext context;
            try {
                if(!SpeechModelForm.Prepare(root,settings,ref postInstall)) return 1;
                context=new VoiceContext(root,settings);
            }
            catch(Exception error) {
                // A failed microphone or dependency must not hide the
                // settings needed to repair it. The failed context has released
                // its devices and shortcuts before this standalone form opens.
                Log.Write("Startup initialization failed: "+error);
                if(!ShowSettings(root,settings,postInstall?5:0,error.Message)) return 1;
                continue;
            }
            using(context) {
                if(postInstall) context.OpenConnectionAfterStartup();
                Application.Run(context);
            }
            return 0;
        }
    }
    static bool ShowSettings(string root,Settings settings,int initialTab,string message) {
        using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,ResidentIpc.StopEvent))
        using(var connect=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.ConnectEvent))
        using(var general=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.SettingsEvent))
        using(var form=new SettingsForm(root,settings,null,initialTab))
        using(var update=new UpdateShutdown(()=>form.ShutdownReady,form.BeginUpdateWait,form.EndUpdateWait,form.CloseForUpdate))
        using(var commands=new System.Windows.Forms.Timer { Interval=100 }) {
            form.ShowStartupFailure(message);
            // Keep control commands usable while the resident's main mutex is
            // held by this recovery window, without opening duplicate windows.
            commands.Tick+=delegate {
                if(stop.WaitOne(0)) { stop.Reset(); form.CloseForUpdate(); return; }
                if(connect.WaitOne(0)) { form.SelectTab(5); form.Reveal(); }
                if(general.WaitOne(0)) { form.SelectTab(0); form.Reveal(); }
            };
            commands.Start(); Application.Run(form); commands.Stop();
            return form.StartupRetryRequested;
        }
    }
}
}

using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Minus700K {
internal sealed class SpeechModelForm : Form {
    readonly Func<Action<long,long>,CancellationToken,Task> download;
    readonly Label status; readonly ProgressBar progress; readonly Button retry,close;
    CancellationTokenSource cancellation; bool running,closing;
    internal bool Completed { get; private set; }
    internal bool OpenConnection { get; private set; }
    internal SpeechModelForm(Func<Action<long,long>,CancellationToken,Task> download) {
        this.download=download;
        Text=L10n.T("modelSetup.title"); Font=SettingsTheme.Font(); BackColor=SettingsTheme.Background; ForeColor=SettingsTheme.Text;
        AutoScaleMode=AutoScaleMode.Dpi; ClientSize=new Size(560,280); FormBorderStyle=FormBorderStyle.FixedDialog;
        StartPosition=FormStartPosition.CenterScreen; MaximizeBox=false; MinimizeBox=true;
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        var description=SettingsTheme.Label(L10n.T("modelSetup.description")); description.Dock=DockStyle.Fill; description.AutoSize=false;
        status=SettingsTheme.Label(""); status.Dock=DockStyle.Fill; status.AutoSize=false; status.Name="ModelDownloadStatus";
        progress=new ProgressBar { Dock=DockStyle.Fill,Margin=new Padding(0,0,0,12),Name="ModelDownloadProgress" };
        var buttons=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,WrapContents=false };
        close=SettingsTheme.Button(L10n.T("modelSetup.close"),false,130); close.Click+=delegate { Close(); };
        retry=SettingsTheme.Button(L10n.T("modelSetup.retry"),true,130); retry.Name="ModelDownloadRetry"; retry.Click+=delegate { StartDownload(); };
        buttons.Controls.Add(close); buttons.Controls.Add(retry);
        layout.Controls.Add(description); layout.Controls.Add(status); layout.Controls.Add(progress); layout.Controls.Add(buttons); Controls.Add(layout);
        Shown+=delegate { StartDownload(); };
        FormClosing+=delegate(object sender,FormClosingEventArgs e) {
            if(running) { e.Cancel=true; closing=true; close.Enabled=false; cancellation.Cancel(); status.Text=L10n.T("modelSetup.cancelling"); }
        };
    }
    async void StartDownload() {
        if(running || closing) return;
        running=true; retry.Enabled=false; retry.Visible=false; status.ForeColor=SettingsTheme.Text;
        status.Text=L10n.T("modelSetup.connecting"); progress.Value=0; progress.Style=ProgressBarStyle.Marquee;
        cancellation=new CancellationTokenSource();
        var updates=new Progress<Tuple<long,long>>(value=>{
            if(closing || !running) return;
            progress.Style=ProgressBarStyle.Continuous; progress.Value=(int)Math.Min(100,value.Item1*100/value.Item2);
            status.Text=L10n.F("modelSetup.progress",value.Item1/1000000.0,value.Item2/1000000.0);
        });
        try {
            await download((received,total)=>((IProgress<Tuple<long,long>>)updates).Report(Tuple.Create(received,total)),cancellation.Token);
            if(!closing) Completed=true;
        } catch(OperationCanceledException) { }
        catch(Exception error) {
            Log.Write("Speech model download failed: "+error.GetType().Name);
            status.ForeColor=SettingsTheme.Error;
            status.Text=L10n.T(error is UnauthorizedAccessException?"modelSetup.writeFailed":error is InvalidDataException?"modelSetup.invalid":"modelSetup.failed");
        } finally {
            running=false; cancellation.Dispose(); cancellation=null; progress.Style=ProgressBarStyle.Continuous;
            if(Completed || closing) Close(); else { retry.Visible=true; retry.Enabled=true; }
        }
    }
    internal static bool Prepare(string root,Settings settings,ref bool postInstall) {
        if(!SpeechModel.NeedsDownload(root,settings)) return true;
        var file=SpeechModel.FileFor(root);
        using(var form=new SpeechModelForm((progress,token)=>Task.Run(()=>file.Ensure(progress,token),token)))
        using(var stop=new EventWaitHandle(false,EventResetMode.ManualReset,ResidentIpc.StopEvent))
        using(var connect=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.ConnectEvent))
        using(var general=new EventWaitHandle(false,EventResetMode.AutoReset,ResidentIpc.SettingsEvent))
        using(var update=new UpdateShutdown(()=>!form.running,form.Close,delegate {},form.Close))
        using(var commands=new System.Windows.Forms.Timer { Interval=100 }) {
            commands.Tick+=delegate {
                if(stop.WaitOne(0)) { stop.Reset(); form.Close(); return; }
                if(connect.WaitOne(0)) { form.OpenConnection=true; form.Activate(); }
                if(general.WaitOne(0)) form.Activate();
            };
            commands.Start(); Application.Run(form); commands.Stop();
            postInstall=postInstall || form.OpenConnection;
            return form.Completed;
        }
    }
}
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Minus700K {
// Opened only by an explicit settings action. Recording shortcuts and request errors never open windows.
internal sealed class PlanConnectionPanel : UserControl {
    readonly ChatGptPlan plan; readonly PlanStore store; readonly Action<Uri> openBrowser;
    readonly ComboBox models=SettingsTheme.Combo();
    readonly Label status=new Label { AutoSize=true,MaximumSize=new Size(560,0),Padding=new Padding(0,8,0,8) };
    readonly Label active=new Label { AutoSize=true,MaximumSize=new Size(560,0),Padding=new Padding(0,8,0,8) };
    Func<string> statusText,activeText;
    readonly List<Button> conflicting=new List<Button>();
    readonly Button cancelLogin,retryLogin,signOut;
    CancellationTokenSource operationCancellation; Task operationTask;
    bool working,loggingIn,restarting; Settings settings; readonly bool preview;
    bool updatingModels,canChooseModel; string modelAccountKey,originalModel="",draftModel="",appliedModelKey,appliedModel;
    readonly List<PlanModel> modelCatalog=new List<PlanModel>();
    internal bool IsBusy { get { return working || restarting; } }
    internal bool HasModelChanges { get { return canChooseModel && modelAccountKey!=null && draftModel!=originalModel; } }
    internal event EventHandler ModelSelectionChanged;
    internal Func<CancellationToken,Task> BeforeConnectionChange { get; set; }
    internal event EventHandler ConnectionChanging;
    internal event EventHandler ConnectionUpdated;
    internal event EventHandler BusyStateChanged;
    internal PlanConnectionPanel(Settings settings,bool preview=false,ChatGptPlan plan=null,PlanStore store=null,Action<Uri> openBrowser=null) {
        this.settings=settings; this.preview=preview; this.store=store??PlanStore.Default; this.plan=plan??new ChatGptPlan(this.store);
        this.openBrowser=openBrowser??(uri=>Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute=true }));
        Font=SettingsTheme.Font(); ForeColor=SettingsTheme.Text; BackColor=SettingsTheme.Background; AutoSize=false;
        var layout=new TableLayoutPanel { Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,ColumnCount=1,Margin=new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); Controls.Add(layout);
        layout.SizeChanged+=delegate { Height=layout.Height; };
        var accountCard=new SettingsCard("ui.connection.account","ui.connection.accountHelp");
        active.ForeColor=SettingsTheme.Text; active.Font=SettingsTheme.Font(10,FontStyle.Bold); active.BackColor=Color.Transparent; accountCard.Add(active);
        signOut=Button("ui.connection.signOut",SignOut,210);
        accountCard.Buttons(Button("Continue with ChatGPT",BeginLogin,230),signOut);
        cancelLogin=Button("ui.connection.cancelLogin",CancelLogin,230,true); retryLogin=Button("ui.connection.retryLogin",RetryLogin,210,true);
        accountCard.Buttons(cancelLogin,retryLogin);
        var modelCard=new SettingsCard("ui.connection.model","ui.connection.modelHelp");
        models.BackColor=SettingsTheme.Input; models.ForeColor=SettingsTheme.Text; models.FlatStyle=FlatStyle.Flat;
        models.SelectedIndexChanged+=delegate { if(updatingModels) return; PlanModel selected=models.SelectedItem as PlanModel; if(selected==null || !canChooseModel) return; draftModel=selected.Slug??""; ModelChanged(); SetStatus(()=>ModelPreferenceText()); };
        modelCard.Field("ui.connection.selectedModel",null,models);
        modelCard.Buttons(Button("ui.connection.loadModels",LoadModels,210));
        status.ForeColor=SettingsTheme.Muted; status.Font=SettingsTheme.Font(9); status.BackColor=Color.Transparent; modelCard.Add(status);
        var usageCard=new SettingsCard("ui.connection.usage","ui.connection.usageHelp");
        usageCard.Buttons(Button("ui.connection.manageUsage",OpenUsage,280,true));
        foreach(var card in new[]{accountCard,modelCard,usageCard}) { card.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top; layout.Controls.Add(card,0,layout.RowCount++); }
        RefreshState(); UpdateControls();
    }
    Button Button(string text,Action action,int width,bool allowWhileBusy=false) {
        var button=SettingsTheme.Button(text,text=="Continue with ChatGPT",width); if(!allowWhileBusy) conflicting.Add(button);
        button.Click+=delegate { if(!preview && (allowWhileBusy || !IsBusy)) { try { action(); } catch(Exception e) { ShowError(e); } } }; return button;
    }
    void SetStatus(Func<string> value) { statusText=value; status.Text=value(); }
    void SetActive(Func<string> value) { activeText=value; active.Text=value(); }
    internal void RefreshLanguage() {
        SettingsTheme.RefreshLanguage(this);
        if(preview) foreach(var model in modelCatalog) {
            if(model.Slug=="preview-model-a") model.Name=L10n.T("ui.connection.previewModelA");
            else if(model.Slug=="preview-model-b") model.Name=L10n.T("ui.connection.previewModelB");
        }
        RenderModels();
        if(activeText!=null) active.Text=activeText();
        if(statusText!=null) status.Text=statusText();
    }
    void Busy(bool value) { working=value; if(!IsDisposed) UpdateControls(); var handler=BusyStateChanged; if(handler!=null) handler(this,EventArgs.Empty); }
    void UpdateControls() {
        foreach(Button button in conflicting) button.Enabled=!IsBusy; models.Enabled=!IsBusy && canChooseModel;
        cancelLogin.Visible=loggingIn; retryLogin.Visible=loggingIn;
        cancelLogin.Enabled=loggingIn && !restarting && operationCancellation!=null && !operationCancellation.IsCancellationRequested;
        retryLogin.Enabled=loggingIn && !restarting;
    }
    void Changing() { var handler=ConnectionChanging; if(handler!=null) handler(this,EventArgs.Empty); }
    void Updated() { var handler=ConnectionUpdated; if(handler!=null) handler(this,EventArgs.Empty); }
    internal void UpdatePreferences(Settings value) {
        settings=value; if(!IsBusy) SetStatus(()=>ModelPreferenceText());
    }
    void ModelChanged() { var handler=ModelSelectionChanged; if(handler!=null) handler(this,EventArgs.Empty); }
    string ModelPreferenceText() {
        string text=HasModelChanges?L10n.T("ui.connection.pendingModel"):L10n.T("ui.connection.autoModelHelp");
        if(!string.IsNullOrWhiteSpace(settings.CodexModel)) text+=L10n.F("ui.connection.overrideModel",settings.CodexModel);
        return text;
    }
    void SynchronizeModelAccount(PlanAccount current) {
        bool editable=current!=null && current.Subject.Length>0 && current.Connected;
        string key=current==null?null:current.Key,saved=current==null?"":current.Model??"";
        bool same=editable && canChooseModel && key==modelAccountKey;
        if(!same || !HasModelChanges) {
            originalModel=saved; draftModel=saved;
            if(!same) { modelCatalog.Clear(); appliedModelKey=null; appliedModel=null; }
        }
        modelAccountKey=key; canChooseModel=editable;
    }
    void RenderModels() {
        updatingModels=true;
        try {
            models.Items.Clear(); models.Items.Add(new PlanModel { Slug="",Name=L10n.T("ui.connection.autoModel") });
            var seen=new HashSet<string>(StringComparer.Ordinal) { "" };
            foreach(PlanModel model in modelCatalog) {
                if(model==null || string.IsNullOrEmpty(model.Slug) || !seen.Add(model.Slug)) continue;
                models.Items.Add(new PlanModel { Slug=model.Slug,Name=string.IsNullOrWhiteSpace(model.Name)?model.Slug:model.Name,CatalogEntry=model.CatalogEntry });
            }
            if(seen.Add(originalModel)) models.Items.Add(new PlanModel { Slug=originalModel,Name=L10n.F("ui.connection.savedModel",originalModel) });
            if(seen.Add(draftModel)) models.Items.Add(new PlanModel { Slug=draftModel,Name=L10n.F("ui.connection.draftModel",draftModel) });
            for(int i=0;i<models.Items.Count;i++) if(((PlanModel)models.Items[i]).Slug==draftModel) { models.SelectedIndex=i; break; }
            models.Enabled=!IsBusy && canChooseModel;
        } finally { updatingModels=false; }
    }
    void PreviewModels() {
        if(modelAccountKey==null) { modelAccountKey="preview-client:preview-subject"; originalModel=""; draftModel=""; }
        canChooseModel=true; modelCatalog.Clear();
        modelCatalog.Add(new PlanModel { Slug="preview-model-a",Name=L10n.T("ui.connection.previewModelA") });
        modelCatalog.Add(new PlanModel { Slug="preview-model-b",Name=L10n.T("ui.connection.previewModelB") });
        RenderModels(); SetStatus(()=>ModelPreferenceText());
    }
    void RefreshState() {
        if(preview) { SetActive(()=>L10n.T("ui.connection.previewAccount")); PreviewModels(); return; }
        PlanAccount current=store.Read().Active;
        SetActive(()=>current==null?L10n.T("ui.connection.disconnected"):L10n.F("ui.connection.accountStatus",current.Email.Length>0?current.Email:L10n.T("ui.connection.account"),current.Connected?L10n.T("ui.connection.connected"):L10n.T("ui.connection.permissionNeeded")));
        SynchronizeModelAccount(current); RenderModels(); ModelChanged(); SetStatus(()=>ModelPreferenceText());
    }
    internal void ApplyModelSelection(Action apply) {
        if(!HasModelChanges) { if(apply!=null) apply(); return; }
        if(IsBusy) throw new InvalidOperationException(L10n.T("ui.connection.modelBusy"));
        string key=modelAccountKey,previous=originalModel,next=draftModel;
        appliedModelKey=null; appliedModel=null;
        if(preview) {
            if(apply!=null) apply();
        } else {
            store.Locked(delegate {
                PlanState state=store.ReadUnlocked(); PlanAccount current=state.Active;
                if(current==null || !current.Connected || current.Key!=key) throw new InvalidOperationException(L10n.T("ui.connection.accountChanged"));
                if((current.Model??"")!=previous) throw new InvalidOperationException(L10n.T("ui.connection.modelChanged"));
                current.Model=next; store.SaveUnlocked(state);
                try { if(apply!=null) apply(); }
                catch(Exception failure) {
                    try {
                        // Refresh may have rotated tokens during apply. Restore only our
                        // model field in the latest state, never an old credential snapshot.
                        PlanState latest=store.ReadUnlocked(); PlanAccount target=latest.Accounts.FirstOrDefault(account=>account.Key==key);
                        if(target==null || (target.Model??"")!=next) throw new InvalidOperationException(L10n.T("ui.connection.restoreChanged"));
                        target.Model=previous; store.SaveUnlocked(latest);
                    } catch(Exception recovery) { throw new IOException(L10n.T("ui.connection.restoreFailed"),new AggregateException(failure,recovery)); }
                    throw;
                }
                return true;
            });
        }
        appliedModelKey=key; appliedModel=next;
    }
    internal void AcceptModelSelection() {
        if(appliedModelKey!=null && appliedModelKey==modelAccountKey) originalModel=appliedModel;
        appliedModelKey=null; appliedModel=null; ModelChanged(); if(!IsDisposed) SetStatus(()=>ModelPreferenceText());
    }
    void ShowError(Exception e) {
        SetStatus(()=>e is OperationCanceledException?L10n.T("ui.connection.loginCancelled"):e is PlanException?e.Message:L10n.T("ui.connection.failed"));
        Log.Write("connection settings: "+(e is PlanException?((PlanException)e).Code:e.GetType().Name));
    }
    void BeginLogin() { if(preview || IsBusy || IsDisposed) return; operationTask=Login(); }
    async Task Login() {
        var cancellation=new CancellationTokenSource(); operationCancellation=cancellation; loggingIn=true; Busy(true);
        try {
            Changing(); SetStatus(()=>L10n.T("ui.connection.waitToConnect"));
            var prepare=BeforeConnectionChange; if(prepare!=null) await prepare(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            SetStatus(()=>L10n.T("ui.connection.loginPrompt"));
            PlanAccount result=await Task.Run(()=>plan.SignInCurrent(openBrowser,cancellation.Token));
            // A committed login wins a simultaneous cancellation. Always publish it;
            // subscribers invalidate requests/replay before the new account is used.
            Updated(); if(IsDisposed) return; RefreshState();
            if(result.Permitted && !result.Welcomed) {
                store.Locked(delegate { PlanState state=store.ReadUnlocked(); var account=state.Accounts.FirstOrDefault(a=>a.ClientId==result.ClientId); if(account!=null) { account.Welcomed=true; store.SaveUnlocked(state); } return true; });
            }
            SetStatus(()=>result.Permitted?L10n.T("ui.connection.loginSucceeded"):L10n.T("ui.connection.loginNotPermitted"));
        } catch(Exception e) { if(!IsDisposed) { try { RefreshState(); } catch { } ShowError(e); } }
        finally { loggingIn=false; if(ReferenceEquals(operationCancellation,cancellation)) operationCancellation=null; cancellation.Dispose(); Busy(false); }
    }
    void CancelLogin() { if(!loggingIn || operationCancellation==null) return; SetStatus(()=>L10n.T("ui.connection.cancelling")); operationCancellation.Cancel(); UpdateControls(); }
    async void RetryLogin() {
        if(!loggingIn || restarting || operationTask==null) return;
        restarting=true; Busy(true); SetStatus(()=>L10n.T("ui.connection.restarting"));
        Task previous=operationTask; operationCancellation.Cancel();
        try { await previous; } catch { }
        restarting=false; if(IsDisposed) { Busy(false); return; } operationTask=Login();
    }
    async void SignOut() {
        if(preview) return;
        PlanAccount current=store.Read().Active; if(current==null) { SetStatus(()=>L10n.T("ui.connection.noAccount")); return; }
        var cancellation=new CancellationTokenSource(); operationCancellation=cancellation; Busy(true);
        try {
            Changing(); SetStatus(()=>L10n.T("ui.connection.waitToSignOut"));
            var prepare=BeforeConnectionChange; if(prepare!=null) await prepare(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); SetStatus(()=>L10n.T("ui.connection.signingOut"));
            bool revoked=await Task.Run(()=>plan.SignOut(current.ClientId,cancellation.Token)); Updated();
            if(!IsDisposed) { RefreshState(); SetStatus(()=>revoked?L10n.T("ui.connection.signedOut"):L10n.T("ui.connection.signedOutLocally")); }
        } catch(Exception e) { if(!IsDisposed) ShowError(e); }
        finally { if(ReferenceEquals(operationCancellation,cancellation)) operationCancellation=null; cancellation.Dispose(); Busy(false); }
    }
    async void LoadModels() {
        if(preview) { PreviewModels(); return; }
        var cancellation=new CancellationTokenSource(); operationCancellation=cancellation; Busy(true); SetStatus(()=>L10n.T("ui.connection.loadingModels"));
        try {
            PlanAccount account=null; var choices=await Task.Run(delegate { account=plan.Access(cancellation.Token); return plan.Models(account,cancellation.Token); });
            if(IsDisposed) return;
            PlanAccount current=store.Read().Active;
            if(current==null || !current.Connected || current.Key!=account.Key) { RefreshState(); SetStatus(()=>L10n.T("ui.connection.catalogAccountChanged")); return; }
            SynchronizeModelAccount(current); modelCatalog.Clear(); modelCatalog.AddRange(choices); RenderModels(); ModelChanged();
            SetStatus(()=>ModelPreferenceText()+L10n.T("ui.connection.modelAvailability"));
        } catch(Exception e) { if(!IsDisposed) { try { RefreshState(); } catch { } ShowError(e); } }
        finally { if(ReferenceEquals(operationCancellation,cancellation)) operationCancellation=null; cancellation.Dispose(); Busy(false); }
    }
    protected override void Dispose(bool disposing) { if(disposing && operationCancellation!=null) operationCancellation.Cancel(); base.Dispose(disposing); }
    internal static void OpenUsage() { Process.Start(new ProcessStartInfo(ChatGptPlan.UsageUrl) { UseShellExecute=true }); }
}
}

#ifndef RepoRoot
  #define RepoRoot SourcePath + ".."
#endif
#ifndef PayloadRoot
  #error Build this installer with build-installer.ps1.
#endif
#ifndef InstallerAppId
  #define InstallerAppId "Minus700K"
#endif
#ifndef InstallerGroupName
  #define InstallerGroupName "Minus700K"
#endif
#ifndef InstallerInstallingMutex
  #define InstallerInstallingMutex "Local\Minus700K_Installing_7E940DB1"
#endif

[Setup]
AppId={#InstallerAppId}
AppName=Minus700K
AppVersion=1.0.0
AppVerName=Minus700K 1.0
VersionInfoVersion=1.0.0.0
AppPublisher=Minus700K contributors
DefaultDirName={localappdata}\Programs\Minus700K
DefaultGroupName={#InstallerGroupName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
UsePreviousLanguage=yes
UninstallDisplayIcon={app}\bin\Minus700K.exe
; Setup cooperatively closes this installation through prepare-update.ps1.
; Uninstall checks the installed executable's processes in InitializeUninstall.
; AppMutex code constants are expanded during setup, not during uninstall.
SetupMutex=Local\Minus700K_Setup_7E940DB1
CloseApplications=no
RestartApplications=no
SetupLogging=yes
WizardStyle=modern
SetupIconFile={#RepoRoot}\assets\icons\minus700k.ico
LicenseFile={#PayloadRoot}\LICENSE
OutputDir={#RepoRoot}\dist
OutputBaseFilename=Minus700K-Setup
Compression=lzma2/normal
SolidCompression=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
japanese.DesktopShortcut=デスクトップにショートカットを作成する
english.DesktopShortcut=Create a desktop shortcut
japanese.LaunchApp=Minus700Kを起動する
english.LaunchApp=Start Minus700K
japanese.CheckingDependencies=必要なソフトウェアを確認しています。未導入の場合はダウンロードしてインストールします。しばらくお待ちください。
english.CheckingDependencies=Checking required software. Missing components will be downloaded and installed. Please wait.
japanese.DependenciesFailed=必要なソフトウェアを準備できませんでした。インターネット接続と以下の内容を確認して、インストールをやり直してください。
english.DependenciesFailed=Required software could not be prepared. Check your internet connection and the details below, then run the installer again.
japanese.PowerShellFailed=Windows PowerShellを起動できませんでした。
english.PowerShellFailed=Windows PowerShell could not be started.
japanese.RestartRequired=必要なソフトウェアを導入しました。Windowsを再起動してから、もう一度Minus700Kのインストーラーを実行してください。
english.RestartRequired=Required software was installed. Restart Windows, then run the Minus700K installer again.
japanese.DotNetRequired=このアプリには.NET Framework 4.8以降が必要です。Windows Updateなどで導入してから、もう一度インストーラーを実行してください。
english.DotNetRequired=This application requires .NET Framework 4.8 or later. Install it through Windows Update or Microsoft's installer, then run this installer again.
japanese.LanguageSetupFailed=アプリの言語設定を保存できませんでした。インストール先の書き込み権限を確認して、もう一度インストールしてください。
english.LanguageSetupFailed=The application language could not be saved. Check write access to the installation folder and run the installer again.
japanese.WaitingForApplication=Minus700Kの処理が完了するのを待っています。録音中の場合は録音を終えてください。完了後に自動で終了し、更新します。
english.WaitingForApplication=Waiting for Minus700K to finish its current work. If recording, finish the recording. The application will then close automatically for the update.
japanese.UpdatePreparationFailed=Minus700Kの終了を確認できなかったため、アプリのファイルを更新していません。以下の内容を確認して再試行してください。
english.UpdatePreparationFailed=The application files have not been updated because Minus700K could not be confirmed closed. Review the details below and retry.
japanese.UpdateGateFailed=更新の準備を開始できませんでした。ほかのインストーラーが終了してから再試行してください。
english.UpdateGateFailed=Update preparation could not be started. Wait for other installers to finish, then retry.
japanese.ApplicationRestartFailed=インストールは完了しましたが、Minus700Kを再起動できませんでした。スタートメニューから起動してください。
english.ApplicationRestartFailed=Installation completed, but Minus700K could not be restarted. Start it from the Start menu.
japanese.UpdatedAndRestarted=Minus700Kの更新が完了し、アプリを再起動しました。
english.UpdatedAndRestarted=Minus700K has been updated and restarted.
japanese.UninstallApplicationRunning=Minus700Kが実行中のため、アンインストールできません。通知領域のアイコンから「終了」を選び、開いているMinus700Kの画面も閉じてから、もう一度アンインストールしてください。
english.UninstallApplicationRunning=Minus700K is running and cannot be uninstalled. Choose Exit from its notification-area icon, close any open Minus700K windows, then run the uninstaller again.
japanese.UninstallCheckFailed=Minus700Kの実行状態を確認できなかったため、アンインストールを中止しました。アプリを終了してから、もう一度お試しください。
english.UninstallCheckFailed=Uninstallation was stopped because Minus700K's running state could not be verified. Close the application and try again.
japanese.UninstallGateFailed=更新またはアンインストールが進行中のため、アンインストールを開始できませんでした。ほかの処理が終了してから再試行してください。
english.UninstallGateFailed=Uninstallation could not start because an update or another uninstall is in progress. Wait for it to finish, then try again.
japanese.PrerequisiteNotice=Codex CLIがない場合はOpenAIの公式インストーラーで追加します。必要なMicrosoftのコンポーネントの導入時に管理者権限の確認が表示されることがあります。
english.PrerequisiteNotice=Missing Codex CLI will be installed using OpenAI's official installer. Installing missing Microsoft dependencies may require administrator approval.

[Messages]
japanese.FinishedLabel=Minus700Kのインストールが完了しました。%n%n「Minus700Kを起動する」を選択すると、初回は音声認識モデルをダウンロードした後、設定画面の「接続」タブが開きます。「Continue with ChatGPT」からログインしてください。
english.FinishedLabel=Minus700K has been installed.%n%nSelect "Start Minus700K". On first launch, the speech recognition model downloads before the Connection tab opens. Choose Continue with ChatGPT to sign in.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopShortcut}"; Flags: unchecked

[Files]
; Keep prerequisites first for dependency checks before installation.
Source: "{#RepoRoot}\install-codex.ps1"; Flags: dontcopy
Source: "{#RepoRoot}\installer\prepare-dependencies.ps1"; Flags: dontcopy
Source: "{#RepoRoot}\installer\prepare-update.ps1"; Flags: dontcopy
; Positive lists keep source-tree credentials, personal settings, and logs out.
Source: "{#PayloadRoot}\bin\Minus700K.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: "{#PayloadRoot}\vendor\whisper\Release\whisper-cli.exe"; DestDir: "{app}\vendor\whisper\Release"; Flags: ignoreversion
Source: "{#PayloadRoot}\vendor\whisper\Release\*.dll"; DestDir: "{app}\vendor\whisper\Release"; Flags: ignoreversion
; The app downloads its model on first launch. Do not replace or delete the
; existing model here: upgrades from the bundled version reuse the same path.
Source: "{#PayloadRoot}\config.example.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\config.example.json"; DestDir: "{app}"; DestName: "config.json"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#PayloadRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\README.en.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
; First-launch downloads are not recorded by [Files]. Remove only the default
; model on uninstall; ordinary updates and custom/external models are untouched.
Type: files; Name: "{app}\vendor\ggml-small-q5_1.bin"

[Icons]
Name: "{group}\Minus700K"; Filename: "{app}\bin\Minus700K.exe"; WorkingDir: "{app}"
Name: "{group}\Minus700K Settings"; Filename: "{app}\bin\Minus700K.exe"; Parameters: "--settings"; WorkingDir: "{app}"
Name: "{autodesktop}\Minus700K"; Filename: "{app}\bin\Minus700K.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\bin\Minus700K.exe"; Parameters: "--post-install"; WorkingDir: "{app}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent; Check: OfferFirstLaunch

[Code]
var
  UpdateMutex, UninstallMutex: THandle;
  WaitingForUpdate, WasRunning, ApplicationStopped, InstallingFiles: Boolean;
  UpdateCancelFile, RestartArguments: String;

function NativeCreateMutex(Attributes: NativeInt; InitialOwner: Boolean; Name: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function NativeCloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetCurrentProcessId: LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function AcquireMaintenanceMutex(var Handle: THandle): Boolean;
var
  ErrorCode: LongWord;
begin
  Handle := NativeCreateMutex(0, False, '{#InstallerInstallingMutex}');
  ErrorCode := DLLGetLastError;
  Result := (Handle <> 0) and (ErrorCode <> 183); { ERROR_ALREADY_EXISTS }
  if (not Result) and (Handle <> 0) then begin
    NativeCloseHandle(Handle);
    Handle := 0;
  end;
end;

function InstalledApplicationRunning: Boolean;
var
  Locator, Services, Processes, Process, ExecutablePath: Variant;
  Index: Integer;
  TargetPath, RunningPath: String;
begin
  Result := False;
  TargetPath := ExpandFileName(ExpandConstant('{app}\bin\Minus700K.exe'));
  Locator := CreateOleObject('WbemScripting.SWbemLocator');
  Services := Locator.ConnectServer('.', 'root\CIMV2');
  Processes := Services.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE Name = ''Minus700K.exe''');
  for Index := 0 to Processes.Count - 1 do begin
    Process := Processes.ItemIndex(Index);
    ExecutablePath := Process.ExecutablePath;
    if VarIsNull(ExecutablePath) or VarIsEmpty(ExecutablePath) then
      RaiseException('Could not verify a running Minus700K executable path.');
    RunningPath := ExecutablePath;
    if RunningPath = '' then
      RaiseException('A running Minus700K executable path was empty.');
    if CompareText(ExpandFileName(RunningPath), TargetPath) = 0 then begin
      Result := True;
      exit;
    end;
  end;
end;

procedure ReleaseUninstallMutex;
begin
  if UninstallMutex <> 0 then begin
    NativeCloseHandle(UninstallMutex);
    UninstallMutex := 0;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  { Hold the same startup gate used by setup before enumerating processes.
    This also covers standalone settings/sign-in windows without a resident mutex. }
  if not AcquireMaintenanceMutex(UninstallMutex) then begin
    SuppressibleMsgBox(CustomMessage('UninstallGateFailed'), mbError, MB_OK, IDOK);
    exit;
  end;
  try
    if InstalledApplicationRunning then begin
      Log('Uninstall blocked: the installed Minus700K executable is running.');
      SuppressibleMsgBox(CustomMessage('UninstallApplicationRunning'), mbError, MB_OK, IDOK);
    end else
      Result := True;
  except
    Log('Uninstall running-state check failed: ' + GetExceptionMessage);
    SuppressibleMsgBox(CustomMessage('UninstallCheckFailed'), mbError, MB_OK, IDOK);
  end;
  if not Result then ReleaseUninstallMutex;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Running: Boolean;
begin
  if CurUninstallStep = usUninstall then begin
    { Recheck immediately before deletion, after the confirmation dialog. }
    try
      Running := InstalledApplicationRunning;
    except
      Log('Uninstall running-state recheck failed: ' + GetExceptionMessage);
      RaiseException(CustomMessage('UninstallCheckFailed'));
    end;
    if Running then
      RaiseException(CustomMessage('UninstallApplicationRunning'));
  end;
end;

procedure DeinitializeUninstall;
begin
  ReleaseUninstallMutex;
end;

procedure ReleaseUpdateMutex;
begin
  if UpdateMutex <> 0 then begin
    NativeCloseHandle(UpdateMutex);
    UpdateMutex := 0;
  end;
end;

function OfferFirstLaunch: Boolean;
begin
  Result := not WasRunning;
end;

procedure RestartPreviousApplication(InstallationComplete: Boolean);
var
  ExitCode: Integer;
begin
  if not (WasRunning and ApplicationStopped) then exit;
  { The gate must be released before the replacement process can start. }
  ReleaseUpdateMutex;
  if Exec(ExpandConstant('{app}\bin\Minus700K.exe'), RestartArguments,
      ExpandConstant('{app}'), SW_HIDE, ewNoWait, ExitCode) then
    ApplicationStopped := False
  else begin
    Log('Application restart failed: ' + IntToStr(ExitCode));
    if InstallationComplete then
      SuppressibleMsgBox(CustomMessage('ApplicationRestartFailed'), mbError, MB_OK, IDOK);
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('DotNetRequired'), mbCriticalError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  LanguageCode: String;
  ExitCode: Integer;
begin
  if CurStep = ssInstall then InstallingFiles := True;
  if CurStep = ssPostInstall then begin
    if ActiveLanguage = 'english' then LanguageCode := 'en'
    else LanguageCode := 'ja';
    { Save the chosen language even when the launch checkbox is not selected. }
    if not Exec(ExpandConstant('{app}\bin\Minus700K.exe'),
      '--install-language ' + LanguageCode, ExpandConstant('{app}'),
      SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      RaiseException(CustomMessage('LanguageSetupFailed'));
    if ExitCode <> 0 then begin
      Log('Application language setup failed with exit code ' + IntToStr(ExitCode));
      RaiseException(CustomMessage('LanguageSetupFailed'));
    end;
    ReleaseUpdateMutex;
    RestartPreviousApplication(True);
  end;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if WaitingForUpdate then begin
    { Let the helper release its heartbeat; never terminate the application. }
    SaveStringToFile(UpdateCancelFile, 'cancel', False);
    Cancel := False;
    Confirm := False;
  end;
end;

procedure DeinitializeSetup;
begin
  ReleaseUpdateMutex;
  { Restore the old executable only if copying has not begun. A failed partial
    installation must not start a possibly inconsistent set of files. }
  if not InstallingFiles then RestartPreviousApplication(False);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WasRunning then begin
    if ApplicationStopped then
      WizardForm.FinishedLabel.Caption := CustomMessage('ApplicationRestartFailed')
    else
      WizardForm.FinishedLabel.Caption := CustomMessage('UpdatedAndRestarted');
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  Result := Result + NewLine + NewLine + CustomMessage('PrerequisiteNotice');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  PowerShell, Parameters, ErrorFile, ResultFile: String;
  ErrorDetails: AnsiString;
begin
  Result := '';
  WizardForm.PreparingLabel.Caption := CustomMessage('CheckingDependencies');
  ExtractTemporaryFile('install-codex.ps1');
  ExtractTemporaryFile('prepare-dependencies.ps1');
  ErrorFile := ExpandConstant('{tmp}\dependencies-error.txt');
  DeleteFile(ErrorFile);
  PowerShell := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\prepare-dependencies.ps1') + '" -ApplicationRoot "' +
    ExpandConstant('{app}') + '" -ErrorPath "' + ErrorFile + '"';
  if not Exec(PowerShell, Parameters, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    Result := CustomMessage('PowerShellFailed')
  else if (ExitCode = 3010) or (ExitCode = 1641) then begin
    NeedsRestart := True;
    Result := CustomMessage('RestartRequired');
  end
  else if ExitCode <> 0 then begin
    Result := CustomMessage('DependenciesFailed');
    if LoadStringFromFile(ErrorFile, ErrorDetails) then
      Result := Result + #13#10#13#10 + UTF8Decode(ErrorDetails);
    Log('Prerequisite failed: ' + Result);
  end;
  if Result <> '' then exit;

  { The application uses this mutex to prevent a fresh launch between orderly
    shutdown and replacement of its files. It does not terminate an instance. }
  if UpdateMutex = 0 then begin
    if not AcquireMaintenanceMutex(UpdateMutex) then begin
      Result := CustomMessage('UpdateGateFailed');
      exit;
    end;
  end;
  ExtractTemporaryFile('prepare-update.ps1');
  ErrorFile := ExpandConstant('{tmp}\update-error.txt');
  ResultFile := ExpandConstant('{tmp}\update-result.json');
  UpdateCancelFile := ExpandConstant('{tmp}\update-cancel.txt');
  DeleteFile(ErrorFile);
  DeleteFile(ResultFile);
  DeleteFile(ResultFile + '.ini');
  DeleteFile(UpdateCancelFile);
  WizardForm.PreparingLabel.Caption := CustomMessage('WaitingForApplication');
  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\prepare-update.ps1') + '" -ApplicationRoot "' +
    ExpandConstant('{app}') + '" -ResultPath "' + ResultFile + '" -ErrorPath "' +
    ErrorFile + '" -CancelPath "' + UpdateCancelFile + '" -InstallerProcessId ' + IntToStr(GetCurrentProcessId);
  WaitingForUpdate := True;
  try
    if not Exec(PowerShell, Parameters, ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := CustomMessage('PowerShellFailed')
    else if (ExitCode <> 0) or (GetIniInt('update', 'passed', 0, 0, 1, ResultFile + '.ini') <> 1) then begin
      Result := CustomMessage('UpdatePreparationFailed');
      if LoadStringFromFile(ErrorFile, ErrorDetails) then
        Result := Result + #13#10#13#10 + UTF8Decode(ErrorDetails);
      Log('Update preparation failed: ' + Result);
    end;
  finally
    WaitingForUpdate := False;
  end;
  WasRunning := WasRunning or (GetIniInt('update', 'wasRunning', 0, 0, 1, ResultFile + '.ini') = 1);
  ApplicationStopped := GetIniInt('update', 'stoppedAny', 0, 0, 1, ResultFile + '.ini') = 1;
  RestartArguments := GetIniString('update', 'restartArguments', '', ResultFile + '.ini');
  if Result <> '' then begin
    ReleaseUpdateMutex;
    RestartPreviousApplication(False);
  end;
end;

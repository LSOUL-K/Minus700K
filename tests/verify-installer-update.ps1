param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$PayloadRoot = '',
    [string]$CompilerPath = '',
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Workspace',$Workspace)
    if ($PayloadRoot) { $arguments+=@('-PayloadRoot',$PayloadRoot) }
    if ($CompilerPath) { $arguments+=@('-CompilerPath',$CompilerPath) }
    if ($SkipInstaller) { $arguments+='-SkipInstaller' }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Installer update verification failed in Windows PowerShell.' }
    return
}
$id=[Guid]::NewGuid().ToString('N').Substring(0,12)
$work=Join-Path $Workspace ('runtime\installer-update-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$id)
New-Item -ItemType Directory -Path $work -Force | Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
$checks=New-Object Collections.Generic.List[string]
$fixtures=New-Object Collections.Generic.List[object]
$helper=Join-Path $Workspace 'installer\prepare-update.ps1'
$legacySignal='Local\Minus700K_Verification_Stop_'+$id
$residentMutex='Local\Minus700K_Verification_Resident_'+$id
$powershell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$template=Join-Path $work 'Minus700K.exe'
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$template" /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'InstallerUpdateFixture.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer update fixture did not compile.' }
function Check([bool]$Value,[string]$Message) { if (-not $Value) { throw "Installer update test failed: $Message" }; $checks.Add($Message) }
function Wait-File([string]$Path,[int]$Milliseconds=10000) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) { if ($timer.ElapsedMilliseconds -gt $Milliseconds) { throw "Fixture did not write $Path" }; Start-Sleep -Milliseconds 50 }
}
function New-Fixture([string]$Name,[string]$Mode='',[int]$BusyMilliseconds=1800,[switch]$Standalone) {
    $path=Join-Path $work $Name
    New-Item -ItemType Directory -Path (Join-Path $path 'bin') -Force | Out-Null
    Copy-Item -LiteralPath $template -Destination (Join-Path $path 'bin\Minus700K.exe')
    $process=$null
    if ($Mode) {
        $arguments=@(('--fixture='+$Mode),('--busy-ms='+$BusyMilliseconds),('--legacy-signal='+$legacySignal))
        if ($Standalone) { $arguments+='--settings' }
        $process=Start-Process -FilePath (Join-Path $path 'bin\Minus700K.exe') -ArgumentList $arguments -WorkingDirectory $path -WindowStyle Hidden -PassThru
        $null=$process.Handle
        Wait-File (Join-Path $path 'ready.json')
    }
    $fixture=[pscustomobject]@{Root=$path;Process=$process}; $fixtures.Add($fixture); return $fixture
}
function Start-Helper([object]$Fixture,[int]$Seconds=15,[string]$Cancellation='') {
    $result=Join-Path $Fixture.Root 'update-result.json'; $errorPath=Join-Path $Fixture.Root 'update-error.txt'
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$helper+'" -ApplicationRoot "'+$Fixture.Root+'" -ResultPath "'+$result+'" -ErrorPath "'+$errorPath+'" -TimeoutSeconds '+$Seconds
    if ($Cancellation) { $arguments+=' -CancelPath "'+$Cancellation+'"' }
    $process=Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $Fixture.Root -WindowStyle Hidden -PassThru
    $null=$process.Handle; return $process
}
function Finish-Helper([object]$Process,[int]$ExpectedCode=0) {
    if (-not $Process.WaitForExit(22000)) { throw 'Fixture helper exceeded its bounded wait.' }
    $Process.WaitForExit(); Check ($Process.ExitCode -eq $ExpectedCode) "Helper exits with expected code $ExpectedCode"
}
function Read-ProductionSnapshot {
    $snapshot=[ordered]@{}
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {
            $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\Minus700K_is1')
            if (-not $key) { $snapshot[[string]$view]=$null; continue }
            try { $values=[ordered]@{}; foreach ($name in @($key.GetValueNames() | Sort-Object)) { $values[$name]=$key.GetValue($name) }; $snapshot[[string]$view]=$values }
            finally { $key.Dispose() }
        } finally { $base.Dispose() }
    }
    $snapshot['shortcuts']=@()
    $group=Join-Path ([Environment]::GetFolderPath('Programs')) 'Minus700K'
    if (Test-Path -LiteralPath $group) { $snapshot['shortcuts']=@(Get-ChildItem -LiteralPath $group -File -Recurse | Sort-Object FullName | ForEach-Object { $_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash }) }
    return ($snapshot | ConvertTo-Json -Depth 8 -Compress)
}
$productionSnapshot=Read-ProductionSnapshot
$qaUninstaller=''; $qaRoot=''; $appId='Minus700K.Verification.Update.'+$id
try {
    $idle=New-Fixture 'not-running'; Finish-Helper (Start-Helper $idle)
    $result=Get-Content -LiteralPath (Join-Path $idle.Root 'update-result.json') -Raw | ConvertFrom-Json
    Check ($result.passed -and -not $result.wasRunning) 'A stopped installation needs no application shutdown or restart'

    $other=New-Fixture 'same-name-different-folder' 'observer' 20000
    $busy=New-Fixture 'active-work' 'heartbeat' 2500
    Finish-Helper (Start-Helper $busy)
    $request=Get-Content -LiteralPath (Join-Path $busy.Root 'request.json') -Raw | ConvertFrom-Json
    $exit=Get-Content -LiteralPath (Join-Path $busy.Root 'normal-exit.json') -Raw | ConvertFrom-Json
    $result=Get-Content -LiteralPath (Join-Path $busy.Root 'update-result.json') -Raw | ConvertFrom-Json
    Check ($request.busy -and $exit.elapsedMs -ge 2500 -and -not $exit.busy -and $busy.Process.HasExited) 'Cooperative update waits for active work and the real process exit'
    Check ($result.wasRunning -and $result.stoppedProcesses -contains $busy.Process.Id) 'Result records the original running process for restart'
    Check (-not $other.Process.HasExited -and -not (Test-Path -LiteralPath (Join-Path $other.Root 'request.json'))) 'A same-name executable in another folder receives no shutdown request'

    $standalone=New-Fixture 'standalone-settings' 'heartbeat' 2000 -Standalone
    Finish-Helper (Start-Helper $standalone)
    $result=Get-Content -LiteralPath (Join-Path $standalone.Root 'update-result.json') -Raw | ConvertFrom-Json
    Check ($result.restartArguments -ceq '--settings' -and $standalone.Process.HasExited) 'Cooperative standalone settings finish work and retain their restart mode'

    $cancel=New-Fixture 'cancel' 'refuse' 20000
    $cancelPath=Join-Path $cancel.Root 'cancel.txt'; $pending=Start-Helper $cancel 15 $cancelPath
    Wait-File (Join-Path $cancel.Root 'request.json'); [IO.File]::WriteAllText($cancelPath,'cancel')
    Finish-Helper $pending 1
    Check (-not $cancel.Process.HasExited) 'Cancelling update preparation never force-closes an active process'

    $refuse=New-Fixture 'timeout' 'refuse' 20000
    Finish-Helper (Start-Helper $refuse 2) 1
    Check (-not $refuse.Process.HasExited) 'A bounded wait fails safely without terminating a nonresponsive process'

    $legacy=New-Fixture 'legacy-active-work' 'legacy' 2500
    Finish-Helper (Start-Helper $legacy) 1
    Check (-not $legacy.Process.HasExited -and -not (Test-Path -LiteralPath (Join-Path $legacy.Root 'request.json'))) 'A legacy process is never stopped, even when its stale status reports ready'
    [IO.File]::WriteAllText((Join-Path $legacy.Root 'fixture-exit'),'done'); Check ($legacy.Process.WaitForExit(3000)) 'Legacy fixture closes itself normally'

    $unknown=New-Fixture 'legacy-unknown' 'unknown' 20000
    Finish-Helper (Start-Helper $unknown 8) 1
    Check (-not $unknown.Process.HasExited -and -not (Test-Path -LiteralPath (Join-Path $unknown.Root 'request.json'))) 'Unknown legacy state aborts without sending a stop signal'
    [IO.File]::WriteAllText((Join-Path $unknown.Root 'fixture-exit'),'done'); Check ($unknown.Process.WaitForExit(3000)) 'Legacy fixture closes itself normally'

    if (-not $SkipInstaller) {
        if (-not $PayloadRoot -or -not (Test-Path -LiteralPath (Join-Path $PayloadRoot 'bin\Minus700K.exe'))) { throw 'Pass a previously built installer PayloadRoot for the isolated installer test.' }
        if (-not $CompilerPath) { $CompilerPath=Join-Path $Workspace 'vendor\inno-setup-7.1.1\ISCC.exe' }
        . (Join-Path $Workspace 'installer\prepare-dependencies.ps1') -ApplicationRoot $Workspace -ErrorPath (Join-Path $work 'unused-error.txt')
        if (-not (Test-VisualCppRuntime)) { throw 'The runtime must already be installed; this test will not install shared dependencies.' }
        . (Join-Path $Workspace 'install-codex.ps1') -ApplicationRoot $Workspace
        $codex=Find-CodexExecutable -Root $Workspace
        if (-not $codex) { throw 'Codex must already be installed; this test will not download it.' }
        Assert-CodexExecutable $codex
        $payload=Join-Path $work 'payload'
        Copy-Item -LiteralPath $PayloadRoot -Destination $payload -Recurse
        Copy-Item -LiteralPath $template -Destination (Join-Path $payload 'bin\Minus700K.exe') -Force
        $output=Join-Path $work 'installer'
        & $CompilerPath '/Qp' "/DRepoRoot=$Workspace" "/DPayloadRoot=$payload" "/DInstallerAppId=$appId" "/DInstallerGroupName=$appId" "/DInstallerResidentMutex=$residentMutex" "/O$output" (Join-Path $Workspace 'installer\Minus700K.iss')
        Check ($LASTEXITCODE -eq 0) 'Isolated Inno installer compiles successfully'
        $installed=New-Fixture 'installed application'
        $qaRoot=$installed.Root
        [IO.File]::WriteAllText((Join-Path $qaRoot 'config.json'),'{"AppLanguage":"ja","customSavedValue":"keep-this-value"}',$utf8)
        New-Item -ItemType Directory -Path (Join-Path $qaRoot 'codex-home') -Force | Out-Null
        $authPath=Join-Path $qaRoot 'codex-home\auth.json'; [IO.File]::WriteAllText($authPath,'fixture-auth-only',$utf8)
        $authHash=(Get-FileHash -LiteralPath $authPath).Hash
        # The old fixture remains a valid executable but has a different hash.
        $oldExe=Join-Path $qaRoot 'bin\Minus700K.exe'; $bytes=[IO.File]::ReadAllBytes($oldExe); [IO.File]::WriteAllBytes($oldExe,($bytes+[byte[]]@(1,2,3,4)))
        $installed.Process=Start-Process -FilePath $oldExe -ArgumentList ('--fixture=install --busy-ms=5000 --resident-mutex='+$residentMutex) -WindowStyle Hidden -PassThru
        $null=$installed.Process.Handle; Wait-File (Join-Path $qaRoot 'ready.json')
        $setup=Join-Path $output 'Minus700K-Setup.exe'
        $setupArgs='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /LANG=english /DIR="'+$qaRoot+'" /LOG="'+(Join-Path $work 'upgrade.log')+'"'
        $setupProcess=Start-Process -FilePath $setup -ArgumentList $setupArgs -WorkingDirectory $work -WindowStyle Hidden -PassThru
        $null=$setupProcess.Handle
        if (-not $setupProcess.WaitForExit(90000)) { throw 'Isolated installer exceeded its bounded test wait.' }
        $setupProcess.WaitForExit(); Check ($setupProcess.ExitCode -eq 0) 'Installer upgrades a running application without the startup mutex rejection'
        $qaUninstaller=Join-Path $qaRoot 'unins000.exe'
        Wait-File (Join-Path $qaRoot 'restarted.json')
        $restart=Get-Content -LiteralPath (Join-Path $qaRoot 'restarted.json') -Raw | ConvertFrom-Json
        $language=Get-Content -LiteralPath (Join-Path $qaRoot 'language-applied.json') -Raw | ConvertFrom-Json
        $config=Get-Content -LiteralPath (Join-Path $qaRoot 'config.json') -Raw | ConvertFrom-Json
        Check ($installed.Process.HasExited -and (Test-Path -LiteralPath (Join-Path $qaRoot 'normal-exit.json'))) 'Installer waits for orderly process exit before replacement'
        Check ((Get-FileHash -LiteralPath $oldExe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payload 'bin\Minus700K.exe')).Hash) 'The installed executable is replaced by the new payload'
        Check ($restart.languageAlreadyApplied -and -not $restart.gatePresent -and $restart.arguments -ceq '') 'Previously running application restarts after language setup and after releasing the install gate'
        Check ($language.gatePresent -and $config.AppLanguage -ceq 'en') 'The install gate remains held while the selected language is saved'
        Check ($config.customSavedValue -ceq 'keep-this-value' -and (Get-FileHash -LiteralPath $authPath).Hash -ceq $authHash) 'Update preserves saved settings and authentication data'
        # A stopped installation must remain stopped during a silent update.
        Remove-Item -LiteralPath (Join-Path $qaRoot 'restarted.json')
        $setupProcess=Start-Process -FilePath $setup -ArgumentList $setupArgs -WorkingDirectory $work -WindowStyle Hidden -Wait -PassThru
        Check ($setupProcess.ExitCode -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $qaRoot 'restarted.json'))) 'A previously stopped application is not auto-started by a silent update'
    }
    Check ((Read-ProductionSnapshot) -ceq $productionSnapshot) 'Production installer registration and Start menu shortcuts are unchanged'
    $report=[ordered]@{passed=$true;checks=@($checks.ToArray());evidenceDirectory=$work;installerTest=(-not $SkipInstaller);appId=$appId}
    [IO.File]::WriteAllText((Join-Path $work 'report.json'),($report | ConvertTo-Json -Depth 7),$utf8)
    [IO.File]::WriteAllText((Join-Path $Workspace 'runtime\verify-installer-update.json'),($report | ConvertTo-Json -Depth 7),$utf8)
    Write-Output "Installer update verification passed ($($checks.Count) checks). Evidence: $work"
} finally {
    foreach ($fixture in $fixtures) {
        if ($fixture.Process -and -not $fixture.Process.HasExited) {
            [IO.File]::WriteAllText((Join-Path $fixture.Root 'fixture-exit'),'cleanup')
            if (-not $fixture.Process.WaitForExit(5000)) { Write-Warning "Fixture will self-exit within 30 seconds: $($fixture.Root)" }
        }
    }
    if ($qaUninstaller -and (Test-Path -LiteralPath $qaUninstaller)) {
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry64)
        try {
            $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$appId+'_is1')
            if (-not $key) { throw 'QA installer registration is missing; refusing automatic uninstall.' }
            try { $registered=[IO.Path]::GetFullPath([string]$key.GetValue('InstallLocation')).TrimEnd('\') }
            finally { $key.Dispose() }
        } finally { $base.Dispose() }
        if ($registered -ine [IO.Path]::GetFullPath($qaRoot).TrimEnd('\') -or $appId -notlike 'Minus700K.Verification.Update.*') { throw 'QA installation identity does not match; refusing uninstall.' }
        $uninstall=Start-Process -FilePath $qaUninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WorkingDirectory $work -WindowStyle Hidden -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw 'Isolated QA uninstaller failed.' }
    }
}

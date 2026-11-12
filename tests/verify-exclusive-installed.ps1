param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$CompilerPath = '',
    [string]$ModelPath = '',
    [ValidateRange(0,65535)][int]$LiveDeviceId,
    [switch]$ShellIcons
)
$ErrorActionPreference = 'Stop'
$runLiveCapture = $PSBoundParameters.ContainsKey('LiveDeviceId')
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-PayloadRoot',$PayloadRoot,'-Workspace',$Workspace)
    if ($CompilerPath) { $arguments += @('-CompilerPath',$CompilerPath) }
    if ($ModelPath) { $arguments += @('-ModelPath',$ModelPath) }
    if ($runLiveCapture) { $arguments += @('-LiveDeviceId',[string]$LiveDeviceId) }
    if ($ShellIcons) { $arguments += '-ShellIcons' }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Installed exclusive microphone verification failed in Windows PowerShell.' }
    return
}
$Workspace = (Resolve-Path -LiteralPath $Workspace).ProviderPath
$PayloadRoot = (Resolve-Path -LiteralPath $PayloadRoot).ProviderPath
if (-not $CompilerPath) { $CompilerPath = Join-Path $Workspace 'vendor\inno-setup-7.1.1\ISCC.exe' }
if (-not $ModelPath) { $ModelPath = Join-Path $Workspace 'vendor\ggml-small-q5_1.bin' }
foreach ($path in @($CompilerPath,$ModelPath,(Join-Path $PayloadRoot 'bin\Minus700K.exe'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required existing test input is missing: $path" }
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).ProviderPath
$ModelPath = (Resolve-Path -LiteralPath $ModelPath).ProviderPath
$manifestPath = Join-Path (Split-Path -Parent $PayloadRoot) 'payload-manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Pass the retained payload directory from build-installer.ps1, with its adjacent payload-manifest.json.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not ($manifest | Where-Object { $_.File -ceq 'bin/Minus700K.exe' }) -or
    -not ($manifest | Where-Object { $_.File -ceq 'config.example.json' })) { throw 'The payload manifest is incomplete.' }
if (-not [Environment]::Is64BitOperatingSystem) { throw 'This test requires 64-bit Windows.' }
$release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release
if ($release -lt 528040) { throw 'This test requires an existing .NET Framework 4.8 installation.' }
. (Join-Path $Workspace 'installer\prepare-dependencies.ps1') -ApplicationRoot $Workspace -ErrorPath (Join-Path $Workspace 'runtime\unused-exclusive-installer-error.txt')
if (-not (Test-VisualCppRuntime)) { throw 'The Visual C++ x64 runtime 14.51 or newer must already be installed; this test does not install shared prerequisites.' }
. (Join-Path $Workspace 'install-codex.ps1') -ApplicationRoot $Workspace
$existingCodex = Find-CodexExecutable -Root $Workspace
if (-not $existingCodex) { throw 'A discoverable Codex CLI must already be installed; this test does not download it.' }
Assert-CodexExecutable $existingCodex

$id = [Guid]::NewGuid().ToString('N').Substring(0,12)
$appId = 'Minus700K.Verification.Exclusive_' + $id
$installingMutex = 'Local\Minus700K_Verification_Exclusive_Installing_' + $id
$evidence = Join-Path $Workspace ('runtime\exclusive-installed-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $id)
$installRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('M7KI-' + $id)))
$groupPath = Join-Path ([Environment]::GetFolderPath('Programs')) $appId
$output = Join-Path $evidence 'installer'
$setup = Join-Path $output 'Minus700K-Setup.exe'
$checks = New-Object Collections.Generic.List[string]
$diagnostics = New-Object Collections.Generic.List[object]
$utf8 = New-Object Text.UTF8Encoding($false)
$failure = $null; $cleanupFailure = $null; $setupProcess = $null; $diagnosticProcess = $null; $interopProcess = $null; $liveProcess = $null; $shellProcess = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw "Installed exclusive microphone test failed: $Message" }
    $checks.Add($Message)
}
function Same-Path([string]$Left, [string]$Right) {
    return -not [string]::IsNullOrWhiteSpace($Left) -and -not [string]::IsNullOrWhiteSpace($Right) -and
        [string]::Equals([IO.Path]::GetFullPath($Left).TrimEnd('\'),[IO.Path]::GetFullPath($Right).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)
}
function Get-Registration([string]$Identifier) {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {
            $key = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $Identifier + '_is1')
            if ($null -eq $key) { continue }
            try { return [string]$key.GetValue('InstallLocation') } finally { $key.Dispose() }
        } finally { $base.Dispose() }
    }
    return $null
}
function Get-ProductionSnapshot {
    $snapshot = [ordered]@{}
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {
            $key = $base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\Minus700K_is1')
            if ($null -eq $key) { $snapshot[[string]$view] = $null; continue }
            try {
                $values = [ordered]@{}
                foreach ($name in @($key.GetValueNames() | Sort-Object)) { $values[$name] = $key.GetValue($name) }
                $snapshot[[string]$view] = $values
            } finally { $key.Dispose() }
        } finally { $base.Dispose() }
    }
    $shortcuts = [ordered]@{}
    $productionGroup = Join-Path ([Environment]::GetFolderPath('Programs')) 'Minus700K'
    if (Test-Path -LiteralPath $productionGroup) {
        foreach ($file in @(Get-ChildItem -LiteralPath $productionGroup -File -Recurse | Sort-Object FullName)) { $shortcuts[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash }
    }
    $snapshot['shortcuts'] = $shortcuts
    $files = [ordered]@{}; $productionRoot = Get-Registration 'Minus700K'
    if ($productionRoot) {
        foreach ($relative in @('bin\Minus700K.exe','config.json')) {
            $path = Join-Path $productionRoot $relative
            $files[$relative] = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path).Hash } else { $null }
        }
    }
    $snapshot['files'] = $files
    return ($snapshot | ConvertTo-Json -Depth 8 -Compress)
}
function Check-Payload([string]$Directory) {
    $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    foreach ($entry in $manifest) {
        $path = [IO.Path]::GetFullPath((Join-Path $Directory ([string]$entry.File)))
        if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Payload manifest path escapes its directory.' }
        Check ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-FileHash -LiteralPath $path).Hash -ieq $entry.SHA256) "Payload matches manifest: $($entry.File)"
    }
}
function Assert-NoTestResident {
    $app = Join-Path $installRoot 'bin\Minus700K.exe'
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name = 'Minus700K.exe'")) {
        if (Same-Path ([string]$process.ExecutablePath) $app) { throw 'A test application process is still running; refusing to update or uninstall it.' }
    }
}
function Invoke-QASetup([string]$LogName) {
    Assert-NoTestResident
    if (-not (Test-VisualCppRuntime) -or -not (Test-Path -LiteralPath $existingCodex -PathType Leaf)) { throw 'A required pre-existing dependency disappeared; refusing installation.' }
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=japanese','/NOICONS',('/DIR="' + $installRoot + '"'),('/LOG="' + (Join-Path $evidence $LogName) + '"'))
    $script:setupProcess = Start-Process -FilePath $setup -ArgumentList $arguments -WorkingDirectory $evidence -WindowStyle Hidden -PassThru
    $null = $script:setupProcess.Handle
    if (-not $script:setupProcess.WaitForExit(120000)) { throw 'QA setup exceeded its bounded wait; no uninstall will run while it is active.' }
    $script:setupProcess.WaitForExit()
    Check ($script:setupProcess.ExitCode -eq 0) "QA installation succeeds: $LogName"
    Check (Same-Path (Get-Registration $appId) $installRoot) 'QA registration points only to the isolated test directory'
    Assert-NoTestResident
}

if ((Get-Registration $appId) -or (Test-Path -LiteralPath $installRoot) -or (Test-Path -LiteralPath $groupPath)) { throw 'The unique QA identity or directory already exists.' }
$productionSnapshot = Get-ProductionSnapshot
New-Item -ItemType Directory -Path $evidence,$output -Force | Out-Null
try {
    Check-Payload $PayloadRoot
    & $CompilerPath '/Qp' "/DRepoRoot=$Workspace" "/DPayloadRoot=$PayloadRoot" "/DInstallerAppId=$appId" "/DInstallerGroupName=$appId" "/DInstallerInstallingMutex=$installingMutex" "/O$output" (Join-Path $Workspace 'installer\Minus700K.iss') *> (Join-Path $evidence 'compile.log')
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $setup -PathType Leaf)) 'QA installer compiles from the unchanged production payload'
    [IO.File]::WriteAllText($setup + '.verification.json',(@{appId=$appId;groupName=$appId;installerSha256=(Get-FileHash -LiteralPath $setup).Hash} | ConvertTo-Json),$utf8)
    Invoke-QASetup 'fresh-install.log'
    Check-Payload $installRoot
    $app = Join-Path $installRoot 'bin\Minus700K.exe'; $configPath = Join-Path $installRoot 'config.json'
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($config.ExclusiveMicrophone -is [bool] -and -not $config.ExclusiveMicrophone) 'Fresh installation defaults exclusive microphone recording to OFF'
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($app))
    $settingsType = $assembly.GetType('Minus700K.Settings',$true)
    $setting = $settingsType.GetField('ExclusiveMicrophone')
    Check ($null -ne $setting -and $setting.FieldType -eq [bool] -and -not $setting.GetValue([Activator]::CreateInstance($settingsType))) 'Installed application itself declares the default-OFF exclusive microphone setting'

    $config.ExclusiveMicrophone = $true; $config.OtherApplicationVolume = 37
    $config.WhisperModel = $ModelPath; $config.CodexPath = $existingCodex
    $config | Add-Member -NotePropertyName ExclusiveVerificationSentinel -NotePropertyValue $id
    [IO.File]::WriteAllText($configPath,($config | ConvertTo-Json -Depth 20),$utf8)
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    Invoke-QASetup 'upgrade.log'
    Check-Payload $installRoot
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Second installation preserves existing config bytes, exclusive ON, other volume 37 and sentinel'
    foreach ($mode in @('exclusive-microphone-test','other-audio-test','settings-ui-test','localization-test')) {
        $reportPath = Join-Path $installRoot ("runtime\$mode.json")
        $diagnosticProcess = Start-Process -FilePath $app -ArgumentList ('--' + $mode) -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
        $null = $diagnosticProcess.Handle
        if (-not $diagnosticProcess.WaitForExit(120000)) { throw "Installed diagnostic exceeded its bounded wait: $mode" }
        $diagnosticProcess.WaitForExit()
        Check ($diagnosticProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $reportPath -PathType Leaf)) "Installed diagnostic exits successfully: $mode"
        $result = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Check ([bool]$result.passed) "Installed diagnostic passes: $mode"
        Copy-Item -LiteralPath $reportPath -Destination (Join-Path $evidence ($mode + '.json'))
        $diagnostics.Add([pscustomobject]@{mode=$mode;report=$reportPath;elapsedMs=$result.elapsedMs})
    }
    # Exercise all production Core Audio activation paths without opening a
    # microphone stream or changing session volume, mute, names or icons.
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    # Opt-in: show a real taskbar window and a separate test-only tray identity.
    # This never records audio or changes the user's existing tray preference.
    if ($ShellIcons) {
        $shellHarness = Join-Path $evidence 'ShellIconsHarness.exe'
        & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$shellHarness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $Workspace 'tests\ShellIconsHarness.cs') *> (Join-Path $evidence 'shell-icons-build.log')
        Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $shellHarness -PathType Leaf)) 'Shell icon integration harness compiles'
        $shellReport = Join-Path $evidence 'shell-icons.json'
        $shellArguments = foreach ($argument in @($app,(Join-Path $evidence 'shell-fixture'),$shellReport)) {
            if ($argument.Contains('"')) { throw 'Unexpected quote in shell icon verification argument.' }
            '"' + $argument + '"'
        }
        $shellProcess = Start-Process -FilePath $shellHarness -ArgumentList ($shellArguments -join ' ') -WorkingDirectory $installRoot -WindowStyle Hidden -PassThru
        $null = $shellProcess.Handle
        if (-not $shellProcess.WaitForExit(60000)) {
            $shellProcess.Kill(); $null = $shellProcess.WaitForExit(5000)
            throw 'Shell icon verification exceeded 60 seconds; its own harness was stopped.'
        }
        $shellProcess.WaitForExit()
        Check ($shellProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $shellReport -PathType Leaf)) 'Installed shell icon verification exits successfully'
        $shellResult = Get-Content -LiteralPath $shellReport -Raw -Encoding UTF8 | ConvertFrom-Json
        Check ([bool]$shellResult.passed) 'Installed taskbar and notification-area native integration passes'
        $diagnostics.Add([pscustomobject]@{mode='shell-icons';report=$shellReport})
    }
    $interopHarness = Join-Path $evidence 'CoreAudioInteropHarness.exe'
    & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$interopHarness" /r:System.Web.Extensions.dll (Join-Path $Workspace 'tests\CoreAudioInteropHarness.cs') *> (Join-Path $evidence 'core-audio-interop-build.log')
    Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $interopHarness -PathType Leaf)) 'Read-only Core Audio interop harness compiles'
    $interopReport = Join-Path $evidence 'core-audio-interop.json'
    $interopArguments = foreach ($argument in @($app,$interopReport)) {
        if ($argument.Contains('"')) { throw 'Unexpected quote in Core Audio interop test argument.' }
        '"' + $argument + '"'
    }
    $interopProcess = Start-Process -FilePath $interopHarness -ArgumentList ($interopArguments -join ' ') -WorkingDirectory $installRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence 'core-audio-interop.stdout.log') -RedirectStandardError (Join-Path $evidence 'core-audio-interop.stderr.log') -PassThru
    $null = $interopProcess.Handle
    if (-not $interopProcess.WaitForExit(60000)) {
        $interopProcess.Kill(); $null = $interopProcess.WaitForExit(5000)
        throw 'Installed read-only Core Audio interop verification exceeded 60 seconds; its own harness was stopped.'
    }
    $interopProcess.WaitForExit()
    Check ($interopProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $interopReport -PathType Leaf)) 'Installed read-only Core Audio interop verification exits successfully'
    $interopResult = Get-Content -LiteralPath $interopReport -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ([bool]$interopResult.passed) 'Installed production Core Audio activation paths pass read-only interop verification'
    $diagnostics.Add([pscustomobject]@{mode='core-audio-interop';report=$interopReport;elapsedMs=$interopResult.elapsedMs})
    # Live capture is opt-in only after the caller identifies an unused microphone.
    # The default verification above never acquires an exclusive microphone stream.
    if ($runLiveCapture) {
        $harnessSource = Join-Path $Workspace 'tests\ExclusiveMicrophoneLiveHarness.cs'
        $harness = Join-Path $evidence 'ExclusiveMicrophoneLiveHarness.exe'
        & $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll $harnessSource *> (Join-Path $evidence 'live-harness-build.log')
        Check ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $harness -PathType Leaf)) 'Installed live microphone harness compiles'
        foreach ($liveCase in @(
            @{Name='exclusive-microphone-live';Argument=[string]$LiveDeviceId},
            @{Name='exclusive-microphone-repeat';Argument=([string]$LiveDeviceId + ':repeat')}
        )) {
            $liveReport = Join-Path $evidence ($liveCase.Name + '.json')
            $liveArguments = foreach ($argument in @($app,$liveReport,$liveCase.Argument)) {
                if ($argument.Contains('"')) { throw 'Unexpected quote in live microphone test argument.' }
                '"' + $argument + '"'
            }
            $liveProcess = Start-Process -FilePath $harness -ArgumentList ($liveArguments -join ' ') -WorkingDirectory $installRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence ($liveCase.Name + '.stdout.log')) -RedirectStandardError (Join-Path $evidence ($liveCase.Name + '.stderr.log')) -PassThru
            $null = $liveProcess.Handle
            if (-not $liveProcess.WaitForExit(60000)) {
                # This process was created by this test; terminating it also releases
                # any audio interfaces if a driver prevented orderly cleanup.
                $liveProcess.Kill(); $null = $liveProcess.WaitForExit(5000)
                throw "Installed live microphone verification exceeded 60 seconds; its own harness was stopped: $($liveCase.Name)"
            }
            $liveProcess.WaitForExit()
            Check ($liveProcess.ExitCode -eq 0 -and (Test-Path -LiteralPath $liveReport -PathType Leaf)) "Installed live microphone verification exits successfully: $($liveCase.Name)"
            $liveResult = Get-Content -LiteralPath $liveReport -Raw -Encoding UTF8 | ConvertFrom-Json
            Check ([bool]$liveResult.passed) "Installed live microphone verification passes on the explicitly selected device: $($liveCase.Name)"
            $diagnostics.Add([pscustomobject]@{mode=$liveCase.Name;report=$liveReport;deviceId=$LiveDeviceId;elapsedMs=$liveResult.elapsedMs})
        }
    }
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Diagnostics preserve the installed settings'
    Check (-not (Test-Path -LiteralPath (Join-Path $installRoot 'codex-home'))) 'Diagnostics create no application authentication directory'
    Assert-NoTestResident
} catch { $failure = $_.Exception.ToString() }
finally {
    try {
        if (($setupProcess -and -not $setupProcess.HasExited) -or ($diagnosticProcess -and -not $diagnosticProcess.HasExited) -or ($interopProcess -and -not $interopProcess.HasExited) -or ($liveProcess -and -not $liveProcess.HasExited) -or ($shellProcess -and -not $shellProcess.HasExited)) { throw 'A QA process is still active; its installation was retained for inspection.' }
        $registered = Get-Registration $appId
        if ($registered) {
            if ($appId -notmatch '\AMinus700K\.Verification\.Exclusive_[a-f0-9]{12}\z' -or -not (Same-Path $registered $installRoot)) { throw 'QA registration identity mismatch; refusing uninstall.' }
            Assert-NoTestResident
            $uninstaller = Join-Path $installRoot 'unins000.exe'
            $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $evidence 'uninstall.log') + '"')) -WorkingDirectory $evidence -WindowStyle Hidden -Wait -PassThru
            Check ($uninstall.ExitCode -eq 0 -and -not (Get-Registration $appId)) 'Only the isolated QA installation is unregistered and uninstalled'
        }
        Check (-not (Test-Path -LiteralPath $groupPath)) 'No QA Start menu group remains'
    } catch { $cleanupFailure = $_.Exception.ToString() }
    if ((Get-ProductionSnapshot) -cne $productionSnapshot) { $cleanupFailure = 'Production installation registration, shortcuts, executable or settings changed during verification.' }
    else { $checks.Add('Production installation registration, shortcuts, executable and settings remain unchanged') }
    $stopwatch.Stop()
    $report = [ordered]@{passed=($null -eq $failure -and $null -eq $cleanupFailure);elapsedMs=$stopwatch.ElapsedMilliseconds;payload=$PayloadRoot;manifest=$manifestPath;installer=$setup;appId=$appId;fixtureDirectory=$installRoot;evidenceDirectory=$evidence;liveCapture=$runLiveCapture;checks=@($checks.ToArray());diagnostics=@($diagnostics.ToArray());error=$failure;cleanupError=$cleanupFailure}
    $json = $report | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $evidence 'report.json'),$json,$utf8)
    [IO.File]::WriteAllText((Join-Path $Workspace 'runtime\verify-exclusive-installed.json'),$json,$utf8)
}
if ($failure -or $cleanupFailure) { throw "Installed exclusive microphone verification failed. Evidence: $evidence`n$failure`n$cleanupFailure" }
Write-Output "Installed exclusive microphone verification passed ($($checks.Count) checks). Evidence: $evidence"

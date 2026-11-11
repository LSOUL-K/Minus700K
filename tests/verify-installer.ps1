param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$Installer = '',
    [string]$AppId = 'Minus700K',
    [string]$GroupName = 'Minus700K'
)
$ErrorActionPreference = 'Stop'
if (-not $Installer) { $Installer = Join-Path $Workspace 'dist\Minus700K-Setup.exe' }
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Workspace $Workspace -Installer $Installer -AppId $AppId -GroupName $GroupName
    if ($LASTEXITCODE -ne 0) { throw 'Installer verification failed in Windows PowerShell.' }
    return
}

$isolated = $AppId -ne 'Minus700K'
if ($isolated) {
    if ($AppId -notmatch '\AMinus700K\.Verification\.[A-Za-z0-9_-]+\z' -or $GroupName -cne $AppId) {
        throw 'An isolated installer test requires matching Minus700K.Verification.<id> AppId and GroupName values.'
    }
    $identityPath = $Installer + '.verification.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) { throw "The isolated build identity manifest is missing: $identityPath" }
    $identity = Get-Content -LiteralPath $identityPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($identity.appId -cne $AppId -or $identity.groupName -cne $GroupName -or
        $identity.installerSha256 -ne (Get-FileHash -LiteralPath $Installer).Hash) {
        throw 'The isolated installer hash or identity does not match the requested verification build.'
    }
} elseif ($GroupName -cne 'Minus700K') { throw 'The production AppId must use the production Start menu group.' }

function Get-InstallerRegistration([string]$Identifier = $AppId) {
    $registryPath = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $Identifier + '_is1'
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $key = $baseKey.OpenSubKey($registryPath)
            if ($null -ne $key) {
                try {
                    return [pscustomobject]@{ InstallLocation = [string]$key.GetValue('InstallLocation'); View = [string]$view }
                } finally { $key.Dispose() }
            }
        } finally { $baseKey.Dispose() }
    }
    return $null
}
function Get-ProductionSnapshot {
    $values = [ordered]@{}
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $key = $baseKey.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\Minus700K_is1')
            if ($null -eq $key) { $values[[string]$view] = $null; continue }
            try {
                $registration = [ordered]@{}
                foreach ($name in @($key.GetValueNames() | Sort-Object)) { $registration[$name] = $key.GetValue($name) }
                $values[[string]$view] = $registration
            } finally { $key.Dispose() }
        } finally { $baseKey.Dispose() }
    }
    $productionGroup = Join-Path ([Environment]::GetFolderPath('Programs')) 'Minus700K'
    $shortcuts = [ordered]@{}
    if (Test-Path -LiteralPath $productionGroup) {
        foreach ($file in @(Get-ChildItem -LiteralPath $productionGroup -File -Recurse | Sort-Object FullName)) {
            $shortcuts[$file.FullName] = (Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
    $values['shortcuts'] = $shortcuts
    return ($values | ConvertTo-Json -Depth 8 -Compress)
}
function Assert-AppStopped {
    $mutex = $null
    try { $mutex = [Threading.Mutex]::OpenExisting('Local\Minus700K_PTT_7E940DB1') }
    catch [Threading.WaitHandleCannotBeOpenedException] { return }
    if ($null -ne $mutex) {
        $mutex.Dispose()
        throw 'Minus700K is running. Close it yourself before testing; this test will not stop the application.'
    }
}
function Same-Path([string]$Left, [string]$Right) {
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return [string]::Equals([IO.Path]::GetFullPath($Left).TrimEnd('\'), [IO.Path]::GetFullPath($Right).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}

# A production build needs a clean account. An explicitly identified QA build
# uses separate registration and shortcuts, preserving any real installation.
$groupPath = Join-Path ([Environment]::GetFolderPath('Programs')) $GroupName
if (Get-InstallerRegistration) { throw 'A Minus700K installation is already registered. Run this test on a clean test account or machine.' }
if (Test-Path -LiteralPath $groupPath) { throw "A Minus700K Start menu group already exists. Run this test on a clean test account or machine: $groupPath" }
Assert-AppStopped
if (-not [Environment]::Is64BitOperatingSystem) { throw 'Installer verification requires 64-bit Windows.' }
$release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release
if ($release -lt 528040) { throw 'Installer verification requires .NET Framework 4.8 or newer.' }
. (Join-Path $Workspace 'installer\prepare-dependencies.ps1') -ApplicationRoot $Workspace -ErrorPath (Join-Path $Workspace 'runtime\unused-installer-error.txt')
if (-not (Test-VisualCppRuntime)) { throw 'This machine must already have the Visual C++ x64 runtime 14.51 or newer. Installer verification does not install shared runtime components.' }
if (-not (Test-Path -LiteralPath $Installer -PathType Leaf)) { throw "Build the installer first: $Installer" }
$Installer = [IO.Path]::GetFullPath($Installer)
. (Join-Path $Workspace 'install-codex.ps1') -ApplicationRoot $Workspace
$existingCodex = Find-CodexExecutable -Root $Workspace
if (-not $existingCodex) { throw 'This account must already have a usable Codex CLI. Installer verification does not download a CLI.' }
Assert-CodexExecutable $existingCodex
$productionSnapshot = if ($isolated) { Get-ProductionSnapshot } else { $null }

$id = [Guid]::NewGuid().ToString('N').Substring(0,12)
$evidence = Join-Path $Workspace ('runtime\installer-verification-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $id)
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('Minus700K-installer-test-' + $id)
$installRoot = Join-Path $fixtureRoot 'installed application'
$japaneseRoot = Join-Path $fixtureRoot 'Japanese fresh installation'
$failureRoot = Join-Path $fixtureRoot 'blocked installation'
$failureGroupPath = $groupPath
$reportPath = Join-Path $Workspace 'runtime\verify-installer.json'
$checks = New-Object Collections.Generic.List[string]
$utf8 = New-Object Text.UTF8Encoding($false)
$failure = $null
$cleanupFailure = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()
New-Item -ItemType Directory -Path $evidence,$fixtureRoot -Force | Out-Null

function Check([bool]$Value, [string]$Message) {
    if (-not $Value) { throw "Installer test failed: $Message" }
    $checks.Add($Message)
}
function Invoke-TestInstaller([string]$Directory, [string]$LogName, [ValidateSet('english','japanese')][string]$Language = 'english') {
    Assert-AppStopped
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/LANG=' + $Language),('/DIR="' + $Directory + '"'),('/LOG="' + (Join-Path $evidence $LogName) + '"'))
    $process = Start-Process -FilePath $Installer -ArgumentList $arguments -WorkingDirectory $fixtureRoot -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}
function Check-InitialLanguage([string]$ConfigPath, [ValidateSet('en','ja')][string]$Language) {
    $value = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $template = Get-Content -LiteralPath (Join-Path $Workspace 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $locale = Get-Content -LiteralPath (Join-Path $Workspace ("locales\$Language\prompts.json")) -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($value.AppLanguage -ceq $Language -and $value.Language -ceq $Language -and $value.SpeechVoice -ceq '') "Installer language selects UI, recognition and automatic voice: $Language"
    foreach ($field in @(@{Name='CustomInstructionsText';Key='defaults.customInstructions'},@{Name='ApplicationInstructionsText';Key='defaults.applicationInstructions'},@{Name='WebSearchTriggersText';Key='defaults.searchTriggers'})) {
        Check ($value.($field.Name) -ceq $locale.($field.Key)) "Installer selects the localized initial $($field.Name): $Language"
    }
    $languageFields = @('AppLanguage','Language','SpeechVoice','CustomInstructionsText','ApplicationInstructionsText','WebSearchTriggersText')
    foreach ($property in $template.PSObject.Properties) {
        if ($property.Name -notin $languageFields) { Check ($value.($property.Name) -ceq $property.Value) "Distribution default is preserved: $($property.Name) ($Language)" }
    }
    Check (-not $value.PSObject.Properties['SaveConversationHistory'] -and -not $value.PSObject.Properties['UseSavedHistory']) "Retired conversation-saving preferences are absent on first installation: $Language"
    return $value
}
function Invoke-TestUninstaller([string]$LogName) {
    Assert-AppStopped
    $registration = Get-InstallerRegistration
    if ($null -eq $registration -or -not (Same-Path $registration.InstallLocation $installRoot)) {
        throw 'Refusing to uninstall: the registered directory is not this test fixture.'
    }
    $uninstaller = Join-Path $installRoot 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) { throw "Test uninstaller is missing: $uninstaller" }
    $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $evidence $LogName) + '"'))
    $process = Start-Process -FilePath $uninstaller -ArgumentList $arguments -WorkingDirectory $fixtureRoot -WindowStyle Hidden -Wait -PassThru
    return $process.ExitCode
}

try {
    $exitCode = Invoke-TestInstaller $installRoot 'fresh-install.log'
    Check ($exitCode -eq 0) "Fresh installation succeeds (exit $exitCode)"
    $registration = Get-InstallerRegistration
    Check ($null -ne $registration -and (Same-Path $registration.InstallLocation $installRoot)) 'Installation is registered for the current user at the fixture path'

    $app = Join-Path $installRoot 'bin\Minus700K.exe'
    Check (Test-Path -LiteralPath $app -PathType Leaf) 'Application executable is installed under bin'
    Check ([Reflection.AssemblyName]::GetAssemblyName($app).Name -eq 'Minus700K') 'Installed application is a readable .NET assembly'
    foreach ($name in @('whisper-cli.exe','whisper.dll','ggml.dll','ggml-base.dll','ggml-cpu-x64.dll')) {
        Check (Test-Path -LiteralPath (Join-Path $installRoot ('vendor\whisper\Release\' + $name)) -PathType Leaf) "Speech recognition runtime included: $name"
    }
    $model = Join-Path $installRoot 'vendor\ggml-small-q5_1.bin'
    Check (-not (Test-Path -LiteralPath $model)) 'Fresh installation leaves the speech model for first launch'
    $configPath = Join-Path $installRoot 'config.json'
    Check (Test-Path -LiteralPath $configPath -PathType Leaf) 'Default configuration is created on first installation'
    $initialConfig = Check-InitialLanguage $configPath 'en'
    $initialConfigHash = (Get-FileHash -LiteralPath $configPath).Hash

    $shell = New-Object -ComObject WScript.Shell
    try {
        foreach ($shortcutCase in @(@{Name='Minus700K.lnk';Arguments=''},@{Name='Minus700K Settings.lnk';Arguments='--settings'})) {
            $shortcutPath = Join-Path $groupPath $shortcutCase.Name
            Check (Test-Path -LiteralPath $shortcutPath -PathType Leaf) "Start menu shortcut exists: $($shortcutCase.Name)"
            $shortcut = $shell.CreateShortcut($shortcutPath)
            try {
                Check ((Same-Path $shortcut.TargetPath $app) -and $shortcut.Arguments -eq $shortcutCase.Arguments) "Start menu shortcut launches the expected app/action: $($shortcutCase.Name)"
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null }
        }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
    Assert-AppStopped
    $checks.Add('Silent installation does not start the resident application')

    $settingsOutput = & (Join-Path $Workspace 'tests\verify-settings-window.ps1') -Executable $app -ApplicationRoot $installRoot
    [IO.File]::WriteAllText((Join-Path $evidence 'settings-window.log'), ($settingsOutput -join [Environment]::NewLine), $utf8)
    $checks.Add('Installed application opens a visible settings window on the private test desktop')
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $initialConfigHash) 'Settings window verification leaves installed defaults unchanged'

    $whisperExecutable = Join-Path $installRoot 'vendor\whisper\Release\whisper-cli.exe'
    $whisperProcess = Start-Process -FilePath $whisperExecutable -ArgumentList '--help' -WorkingDirectory (Split-Path -Parent $whisperExecutable) -WindowStyle Hidden -RedirectStandardOutput (Join-Path $evidence 'whisper-help.stdout.log') -RedirectStandardError (Join-Path $evidence 'whisper-help.stderr.log') -Wait -PassThru
    Check ($whisperProcess.ExitCode -eq 0) "Installed speech recognition executable loads its native runtime (exit $($whisperProcess.ExitCode))"

    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $config.SpeechVolume = 41
    $config | Add-Member -NotePropertyName SaveConversationHistory -NotePropertyValue $true
    $config | Add-Member -NotePropertyName UseSavedHistory -NotePropertyValue $true
    $config.CustomInstructionsText = 'Keep this edited instruction exactly.'
    $config.WebSearchTriggersText = "my custom trigger`r`nunchanged"
    $config.Language = 'auto'
    $config.SpeechVoice = 'fixture-explicit-speech-voice'
    [IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 20), $utf8)
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash
    $sentinel = Join-Path $installRoot 'runtime\installer-preservation-test.txt'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    [IO.File]::WriteAllText($sentinel, 'Preserve user runtime data during upgrade and uninstall.', $utf8)
    $sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash

    # Simulate a previously downloaded model; setup must not alter its bytes.
    [IO.File]::WriteAllText($model, 'Previously downloaded model preservation fixture.', $utf8)
    $modelHash = (Get-FileHash -LiteralPath $model).Hash
    $modelTime = (Get-Item -LiteralPath $model).LastWriteTimeUtc
    $exitCode = Invoke-TestInstaller $installRoot 'upgrade.log'
    Check ($exitCode -eq 0) "Upgrade succeeds (exit $exitCode)"
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Upgrade preserves the existing configuration bytes'
    Check ((Get-FileHash -LiteralPath $model).Hash -eq $modelHash -and (Get-Item -LiteralPath $model).LastWriteTimeUtc -eq $modelTime) 'Upgrade reuses the downloaded model without rewriting it'
    $sameLanguage = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($sameLanguage.Language -ceq 'auto' -and $sameLanguage.SpeechVoice -ceq 'fixture-explicit-speech-voice') 'Same-language upgrade preserves explicit speech preferences'
    Check ((Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json).SaveConversationHistory -eq $true) 'Same-language upgrade leaves legacy configuration bytes untouched; retired preferences are ignored by the application'
    Check ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Upgrade preserves user runtime files'
    Check (Test-Path -LiteralPath $existingCodex -PathType Leaf) 'Upgrade preserves the pre-existing Codex CLI'

    $exitCode = Invoke-TestInstaller $installRoot 'upgrade-language-japanese.log' 'japanese'
    Check ($exitCode -eq 0) "Upgrade applies a newly selected installer language (exit $exitCode)"
    $switched = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $japanese = Get-Content -LiteralPath (Join-Path $Workspace 'locales\ja\prompts.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($switched.AppLanguage -ceq 'ja' -and $switched.Language -ceq 'ja' -and $switched.SpeechVoice -ceq '') 'Language-changing upgrade selects Japanese UI, recognition and automatic voice'
    Check ($switched.ApplicationInstructionsText -ceq $japanese.'defaults.applicationInstructions') 'Language-changing upgrade translates stock application instructions'
    Check ($switched.CustomInstructionsText -ceq $config.CustomInstructionsText -and $switched.WebSearchTriggersText -ceq $config.WebSearchTriggersText) 'Language-changing upgrade preserves edited instructions and search triggers'
    Check (-not $switched.PSObject.Properties['SaveConversationHistory'] -and -not $switched.PSObject.Properties['UseSavedHistory'] -and $switched.SpeechVolume -eq 41) 'Language-changing settings save removes retired preferences and preserves unrelated settings'
    Check ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Language-changing upgrade preserves user runtime files'
    $configHash = (Get-FileHash -LiteralPath $configPath).Hash

    $exitCode = Invoke-TestUninstaller 'uninstall.log'
    Check ($exitCode -eq 0) "Uninstall succeeds (exit $exitCode)"
    Check (-not (Test-Path -LiteralPath $app)) 'Uninstall removes the application executable'
    Check (-not (Test-Path -LiteralPath $model)) 'Uninstall removes the default model downloaded by the application'
    Check ((Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'Uninstall preserves settings'
    Check ((Get-FileHash -LiteralPath $sentinel).Hash -eq $sentinelHash) 'Uninstall preserves user runtime files'
    Check ($null -eq (Get-InstallerRegistration)) 'Uninstall removes its application registration'
    Check (-not (Test-Path -LiteralPath (Join-Path $groupPath 'Minus700K.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $groupPath 'Minus700K Settings.lnk'))) 'Uninstall removes both test Start menu shortcuts'
    Check (-not (Test-Path -LiteralPath $groupPath)) 'Uninstall removes the test-created Start menu group'
    Check (Test-Path -LiteralPath $existingCodex -PathType Leaf) 'Uninstall leaves the pre-existing Codex CLI in place'

    $installRoot = $japaneseRoot
    $exitCode = Invoke-TestInstaller $installRoot 'fresh-install-japanese.log' 'japanese'
    Check ($exitCode -eq 0) "Fresh Japanese installation succeeds (exit $exitCode)"
    Check-InitialLanguage (Join-Path $installRoot 'config.json') 'ja' | Out-Null
    $exitCode = Invoke-TestUninstaller 'uninstall-japanese.log'
    Check ($exitCode -eq 0 -and $null -eq (Get-InstallerRegistration)) 'Fresh Japanese test installation is uninstalled'
    Check (-not (Test-Path -LiteralPath $groupPath)) 'Japanese uninstall removes only its test Start menu group'

    New-Item -ItemType Directory -Path $failureRoot -Force | Out-Null
    $failureConfig = Join-Path $failureRoot 'config.json'
    [IO.File]::WriteAllText($failureConfig, (@{CodexPath=(Join-Path $failureRoot 'missing.exe');SpeechVolume=41} | ConvertTo-Json), $utf8)
    $failureConfigHash = (Get-FileHash -LiteralPath $failureConfig).Hash
    $exitCode = Invoke-TestInstaller $failureRoot 'prerequisite-failure.log'
    Check ($exitCode -ne 0) "Invalid configured CLI prevents installation (exit $exitCode)"
    Check (-not (Test-Path -LiteralPath (Join-Path $failureRoot 'bin\Minus700K.exe'))) 'Prerequisite failure occurs before application files are installed'
    Check ((Get-FileHash -LiteralPath $failureConfig).Hash -eq $failureConfigHash) 'Prerequisite failure preserves the existing configuration'
    Check ($null -eq (Get-InstallerRegistration)) 'Prerequisite failure does not register an installation'
    Check (-not (Test-Path -LiteralPath $failureGroupPath)) 'Prerequisite failure does not create a Start menu group'
    Check (Test-Path -LiteralPath $existingCodex -PathType Leaf) 'Prerequisite failure leaves the pre-existing Codex CLI in place'
    if ($isolated) { Check ((Get-ProductionSnapshot) -ceq $productionSnapshot) 'Isolated verification preserves the real installation registration and shortcuts' }
} catch {
    $failure = $_.Exception.ToString()
} finally {
    # On failure, uninstall only a registration belonging to this exact fixture.
    # Keep configs, sentinels and logs as evidence; never delete shared user data.
    try {
        $registration = Get-InstallerRegistration
        if ($null -ne $registration -and (Same-Path $registration.InstallLocation $installRoot)) {
            $cleanupExit = Invoke-TestUninstaller 'failure-cleanup.log'
            if ($cleanupExit -ne 0 -or $null -ne (Get-InstallerRegistration)) { throw "Test installation cleanup failed (exit $cleanupExit)." }
        }
    } catch { $cleanupFailure = $_.Exception.ToString() }
    if ($isolated -and (Get-ProductionSnapshot) -cne $productionSnapshot) {
        $cleanupFailure = 'The real installation registration or shortcuts changed during isolated verification.'
    }
    $stopwatch.Stop()
    $report = [ordered]@{
        passed=($null -eq $failure -and $null -eq $cleanupFailure)
        elapsedMs=$stopwatch.ElapsedMilliseconds
        installer=$Installer
        installerSha256=(Get-FileHash -LiteralPath $Installer).Hash
        appId=$AppId
        groupName=$GroupName
        existingCodex=$existingCodex
        evidenceDirectory=$evidence
        fixtureDirectory=$fixtureRoot
        checks=@($checks.ToArray())
        error=$failure
        cleanupError=$cleanupFailure
    }
    $json = $report | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($reportPath, $json, $utf8)
    [IO.File]::WriteAllText((Join-Path $evidence 'report.json'), $json, $utf8)
}
if ($failure -or $cleanupFailure) { throw "Installer verification failed. See $reportPath`n$failure`n$cleanupFailure" }
Write-Output "Installer verification passed ($($checks.Count) checks). Evidence: $evidence"

param([string]$OutputDirectory = 'dist')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
$destination = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$zipPath = Join-Path $destination ('Minus700K-source-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
# A positive file list prevents runtime logs, credentials, and personal settings
# from being included, even when packaging a working installation.
$names = @('LICENSE','README.md','THIRD-PARTY-NOTICES.md','.gitignore','app.manifest','build.ps1','setup.ps1','migrate-config.ps1','package-source.ps1','Start-Minus700K.vbs','Stop-Minus700K.vbs','Connect-ChatGPT.vbs','Open-Settings.vbs','config.example.json')
$names += @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Filter '*.cs' | ForEach-Object { 'src/' + $_.Name })
$names += @(Get-ChildItem -LiteralPath (Join-Path $root 'locales') -Recurse -File -Filter '*.json' | ForEach-Object { 'locales/' + $_.Directory.Name + '/' + $_.Name })
$names += @('LOCALIZATION.md')
$names += @('install-codex.ps1','tests/verify-codex-setup.ps1')
$names += @('build-installer.ps1','installer/Minus700K.iss','installer/prepare-dependencies.ps1','tests/verify-installer.ps1','tests/verify-installer-dependencies.ps1')
$names += @('tests/InstallerStartupHarness.cs','tests/verify-installer-startup.ps1')
$names += @('tests/StartupRecoveryHarness.cs','tests/verify-startup-recovery.ps1')
$names += @('tests/SpeechModelHarness.cs','tests/verify-speech-model.ps1')
$names += @('tests/UninstallRunningFixture.cs','tests/verify-uninstall-running.ps1')
$names += @('tests/SharedShortcutsHarness.cs','tests/verify-shared-shortcuts.ps1','tests/UpdateShutdownHarness.cs','tests/verify-update-shutdown.ps1')
$names += @('tests/ExclusiveMicrophoneLiveHarness.cs','tests/CoreAudioInteropHarness.cs','tests/verify-exclusive-installed.ps1')
$names += @('tests/ShellIconsHarness.cs')
$names += @('tests/ShortcutCaptureHarness.cs','tests/verify-shortcut-capture.ps1')
$names += @('tests/SettingsAltKeysHarness.cs','tests/verify-settings-alt-keys.ps1')
$names += @('installer/prepare-update.ps1','tests/verify-installer-update.ps1','tests/InstallerUpdateFixture.cs','tests/verify-update-process-race.ps1')
$names += @('README.en.md','CONTRIBUTING.md')
$names += @('.github/ISSUE_TEMPLATE/bug_report.yml','.github/ISSUE_TEMPLATE/feature_request.yml','.github/ISSUE_TEMPLATE/config.yml','.github/pull_request_template.md')
$names += @('assets/icons/minus700k.ico','assets/icons/tray-ready.ico','assets/icons/tray-recording.ico','assets/icons/tray-processing.ico','assets/icons/tray-speaking.ico','assets/icons/tray-paused.ico','assets/icons/minus700k-master.svg')
$names += @('tests/verify.ps1','tests/verify-config-migration.ps1','tests/verify-settings-window.ps1','tests/HiddenSettingsHarness.cs','tests/HiddenDesktopLauncher.cs','tests/verify-modeless-settings.ps1','tests/ModelessSettingsHarness.cs')
$archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $names) {
        $source = Join-Path $root ($name -replace '/', '\')
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source file: $name" }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $source, 'Minus700K/' + ($name -replace '\\', '/'), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Output $zipPath
Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Select-Object Hash

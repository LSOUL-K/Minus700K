param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executablePath = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
$resolvedExecutable = (Resolve-Path -LiteralPath $executablePath).ProviderPath
$run = Join-Path $root ('runtime\shortcut-capture-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('M700K-shortcut-capture-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $run 'ShortcutCaptureHarness.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ShortcutCaptureHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Shortcut capture harness build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
$reportPath = Join-Path $run 'report.json'
$stdout = Join-Path $run 'launcher.stdout.log'; $stderr = Join-Path $run 'launcher.stderr.log'
$arguments = foreach ($argument in @($harness,$resolvedExecutable,$fixture,$reportPath)) {
    if ($argument.Contains('"')) { throw 'Unexpected quote in diagnostic path.' }
    '"' + $argument + '"'
}
# Only standalone SettingsForm is instantiated, with an empty fixture PlanStore.
# No resident IPC, SendInput, recording, credentials or requests are used. Native
# keyboard hooks and hotkey reservations belong only to the private desktop.
$process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$null = $process.Handle
if (-not $process.WaitForExit(35000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Shortcut capture test timed out. Evidence: $run" }
$process.WaitForExit()
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Shortcut capture test produced no report (exit $($process.ExitCode)). Evidence: $run" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Shortcut capture test failed: $($report.error) Settings: $($report.settingsFeedback) Evidence: $run" }
$summary = Get-Content -LiteralPath $stdout -Raw
if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Missing private desktop summary: $run" }
Write-Output "Shortcut capture passed ($(@($report.checks).Count) checks), foreground mismatch samples: $($Matches[1])."
Write-Output "Report: $reportPath"

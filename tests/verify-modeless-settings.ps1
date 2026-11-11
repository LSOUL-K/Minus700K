param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Do not stop or restart the installed application. The child repeats this guard
# and holds the resident mutex for the full test to close the startup race.
$resident = $null
$acquired = $false
try {
    try { $resident = [Threading.Mutex]::OpenExisting('Local\Minus700K_PTT_7E940DB1') }
    catch [Threading.WaitHandleCannotBeOpenedException] { }
    if ($resident) {
        try { $acquired = $resident.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Minus700K is running. Close it yourself before this opt-in integration test; the script does not stop or restart it.' }
    }
} finally {
    if ($resident) { if ($acquired) { $resident.ReleaseMutex() }; $resident.Dispose() }
}

$executablePath = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
$resolvedExecutable = (Resolve-Path -LiteralPath $executablePath).ProviderPath
$run = Join-Path (Join-Path $root 'runtime\modeless-settings-test') ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
# Atomic config writes add a GUID suffix to the filename. Keep this isolated
# installation short even when the source checkout/report path is deeply nested.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('M700K-modeless-' + [Guid]::NewGuid().ToString('N'))
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $run 'ModelessSettingsHarness.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'ModelessSettingsHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Modeless settings harness build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
$reportPath = Join-Path $run 'report.json'
$stdout = Join-Path $run 'launcher.stdout.log'
$stderr = Join-Path $run 'launcher.stderr.log'
$arguments = foreach ($argument in @($harness,$resolvedExecutable,$fixture,$reportPath)) {
    if ($argument.Contains('"')) { throw 'Double quotes are not supported in diagnostic paths.' }
    '"' + $argument + '"'
}
# All windows and native focus messages remain on a private desktop. The fixture
# uses an empty connection store and never starts recording, capture, or an AI request.
$process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
# Retain the native handle until ExitCode is read under Windows PowerShell.
$null = $process.Handle
if (-not $process.WaitForExit(40000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "Private desktop launcher timed out. Diagnostic files: $run"
}
$process.WaitForExit()
$process.Refresh()
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Modeless test did not produce a report (exit $($process.ExitCode)). Diagnostic files: $run" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$summary = Get-Content -LiteralPath $stdout -Raw
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Modeless settings test failed: $($report.error) Settings feedback: $($report.settingsFeedback) Report: $reportPath" }
if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Private desktop launcher summary is missing. Diagnostic files: $run" }
Write-Output "modeless-settings-test passed ($(@($report.checks).Count) checks), active desktop foreground mismatch samples: $($Matches[1])"
Write-Output 'No recording, screenshots, AI requests, or personal credential access were performed.'
Write-Output "Report: $reportPath"

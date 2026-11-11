param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$resident = $null
try { $resident = [Threading.Mutex]::OpenExisting('Local\Minus700K_PTT_7E940DB1') }
catch [Threading.WaitHandleCannotBeOpenedException] { }
if ($resident) { $resident.Dispose(); throw 'Minus700K is running. This test never stops an existing application.' }
$executablePath = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
$resolvedExecutable = (Resolve-Path -LiteralPath $executablePath).ProviderPath
$run = Join-Path $root ('runtime\shared-shortcuts-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('M700K-shared-keys-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $run 'SharedShortcutsHarness.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'SharedShortcutsHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Shared shortcut harness build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
$reportPath = Join-Path $run 'report.json'
$stdout = Join-Path $run 'launcher.stdout.log'; $stderr = Join-Path $run 'launcher.stderr.log'
$arguments = foreach ($argument in @($harness,$resolvedExecutable,$fixture,$reportPath)) {
    if ($argument.Contains('"')) { throw 'Unexpected quote in diagnostic path.' }
    '"' + $argument + '"'
}
# The only key input is a delegate injected into this fixture's VoiceContext.
# No SendInput, recording, screenshot, or AI request reaches the user's session.
$process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$null = $process.Handle
if (-not $process.WaitForExit(35000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Shared shortcut test timed out. Evidence: $run" }
$process.WaitForExit()
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Shared shortcut test produced no report (exit $($process.ExitCode)). Evidence: $run" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Shared shortcut test failed: $($report.error) Settings: $($report.settingsFeedback) Evidence: $run" }
$summary = Get-Content -LiteralPath $stdout -Raw
if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Missing private desktop summary: $run" }
Write-Output "Shared shortcuts passed ($(@($report.checks).Count) checks), foreground mismatch samples: $($Matches[1])."
Write-Output "Report: $reportPath"

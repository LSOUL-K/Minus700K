param([string]$Executable = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$executablePath = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
$resolvedExecutable = (Resolve-Path -LiteralPath $executablePath).ProviderPath
$run = Join-Path $root ('runtime\capture-recovery-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$fixture = Join-Path $run 'fixture'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $run 'CaptureRecoveryHarness.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'CaptureRecoveryHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Capture recovery harness build failed.' }
$reportPath = Join-Path $run 'report.json'
$stdout = Join-Path $run 'harness.stdout.log'
$stderr = Join-Path $run 'harness.stderr.log'
$arguments = foreach ($argument in @($resolvedExecutable,$fixture,$reportPath)) {
    if ($argument.Contains('"')) { throw 'Unexpected quote in diagnostic path.' }
    '"' + $argument + '"'
}
# Reflection calls only the pure recovery loop, in-memory frame validator and
# OptionalScreenshot. DesktopCapture's native capture methods are never invoked.
# The application entry point, resident IPC, microphone and network are untouched.
$process = Start-Process -FilePath $harness -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$null = $process.Handle
if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Capture recovery test timed out. Evidence: $run" }
$process.WaitForExit()
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Capture recovery test produced no report (exit $($process.ExitCode)). Evidence: $run" }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Capture recovery test failed: $($report.error) Evidence: $run" }
Write-Output "Capture recovery passed ($(@($report.checks).Count) checks; synthetic frames only)."
Write-Output "Executable SHA256: $((Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash)"
Write-Output "Report: $reportPath"

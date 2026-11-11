param([string]$Executable = '', [string]$ApplicationRoot = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$application = if ($ApplicationRoot) { $ApplicationRoot } else { $root }
$executablePath = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
$resolvedApplication = (Resolve-Path -LiteralPath $application).ProviderPath.TrimEnd('\')
$resolvedExecutable = (Resolve-Path -LiteralPath $executablePath).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $resolvedApplication 'config.json') -PathType Leaf)) {
    throw 'Run setup.ps1 first, or specify an initialized installation with -ApplicationRoot.'
}
$testRoot = Join-Path $root 'runtime\settings-window-test'
$run = Join-Path $testRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness = Join-Path $run 'HiddenSettingsHarness.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'HiddenSettingsHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Settings visibility harness build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
$reportPath = Join-Path $run 'report.json'
$stdout = Join-Path $run 'launcher.stdout.log'
$stderr = Join-Path $run 'launcher.stderr.log'
$arguments = foreach ($argument in @($harness,$resolvedExecutable,$resolvedApplication,$reportPath)) {
    if ($argument.Contains('"')) { throw 'Double quotes are not supported in diagnostic paths.' }
    '"' + $argument + '"'
}
# The launcher creates a private desktop without switching to it. Its child uses
# STARTF_USESHOWWINDOW + SW_HIDE, reproducing the resident's hidden startup.
$process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
# Windows PowerShell may otherwise release its native process handle before
# ExitCode is read. Retain it while waiting for the diagnostic child.
$null = $process.Handle
if (-not $process.WaitForExit(40000)) {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    throw "Private desktop launcher timed out. Diagnostic files: $run"
}
$process.WaitForExit()
$summary = Get-Content -LiteralPath $stdout -Raw
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) {
    throw "Settings visibility test failed (exit $($process.ExitCode)). Diagnostic files: $run"
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.passed) { throw "Native settings window was not visible. Report: $reportPath" }
if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Launcher summary is missing. Diagnostic files: $run" }
$focusChanges = [int]$Matches[1]
Write-Output "settings-window-test passed ($(@($report.samples).Count) native visibility samples), active desktop foreground mismatch samples: $focusChanges"
Write-Output "Report: $reportPath"

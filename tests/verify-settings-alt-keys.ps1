param([string]$Executable='', [switch]$ExpectUnhandled)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable=Join-Path $root 'bin\Minus700K.exe' }
$resolvedExecutable=(Resolve-Path -LiteralPath $Executable).ProviderPath
$run=Join-Path $root ('runtime\settings-alt-keys-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('M700K-settings-alt-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run,$fixture -Force | Out-Null
if ($ExpectUnhandled) { [IO.File]::WriteAllText((Join-Path $fixture 'expect-unhandled'),'baseline') }
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness=Join-Path $run 'SettingsAltKeysHarness.exe'; $launcher=Join-Path $run 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'SettingsAltKeysHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Settings Alt-key harness build failed.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
$reportPath=Join-Path $run 'report.json'; $stdout=Join-Path $run 'launcher.stdout.log'; $stderr=Join-Path $run 'launcher.stderr.log'
$arguments=foreach ($argument in @($harness,$resolvedExecutable,$fixture,$reportPath)) { if ($argument.Contains('"')) { throw 'Unexpected quote in test path.' }; '"'+$argument+'"' }
$process=Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$null=$process.Handle
if (-not $process.WaitForExit(35000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Settings Alt-key test timed out. Evidence: $run" }
$process.WaitForExit()
if (-not (Test-Path -LiteralPath $reportPath)) { throw "Settings Alt-key test produced no report (exit $($process.ExitCode)). Evidence: $run" }
$report=Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Settings Alt-key test failed: $($report.error) Evidence: $run" }
$summary=Get-Content -LiteralPath $stdout -Raw
if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Missing private desktop summary: $run" }
Write-Output "Settings Alt-key test passed ($($report.checks.Count) checks, $($report.unhandledSysChars) unhandled SYSCHAR), foreground mismatch samples: $($Matches[1])."
Write-Output "Report: $reportPath"

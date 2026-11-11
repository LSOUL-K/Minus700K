param([string]$Executable='', [switch]$LiveDownload)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable=Join-Path $workspace 'bin\Minus700K.exe' }
$run=Join-Path $workspace ('runtime\speech-model-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
$bin=Join-Path $run 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
$app=Join-Path $bin 'Minus700K.exe'; Copy-Item -LiteralPath $Executable -Destination $app
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$harness=Join-Path $bin 'SpeechModelHarness.exe'; $launcher=Join-Path $bin 'HiddenDesktopLauncher.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harness" "/r:$app" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'SpeechModelHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Speech model harness build failed' }
$launcherSource=Join-Path $run 'SpeechModelDesktopLauncher.cs'
$launcherText=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs'))
if ($LiveDownload) { $launcherText=$launcherText.Replace('>=20000','>=600000') }
[IO.File]::WriteAllText($launcherSource,$launcherText)
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/out:$launcher" $launcherSource
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed' }
$arguments=@($harness,$run,$(if ($LiveDownload) { 'live' } else { 'offline' }),'unused')
$quoted=foreach ($argument in $arguments) { if ($argument.Contains('"')) { throw 'Unexpected quote in test path' }; '"'+$argument+'"' }
$stdout=Join-Path $run 'launcher.stdout.log'; $stderr=Join-Path $run 'launcher.stderr.log'
$process=Start-Process -FilePath $launcher -ArgumentList ($quoted -join ' ') -WorkingDirectory $run -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
$null=$process.Handle
if (-not $process.WaitForExit($(if ($LiveDownload) { 600000 } else { 45000 }))) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Test timed out: $run" }
$process.WaitForExit()
$report=Get-Content -LiteralPath (Join-Path $run 'report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Speech model verification failed: $($report.error) Evidence: $run" }
Get-Content -LiteralPath $stdout
Write-Output "Speech model verification passed ($($report.checks.Count) checks): $run"

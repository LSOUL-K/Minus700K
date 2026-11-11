param([string]$Executable = '', [string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Workspace 'bin\Minus700K.exe' }
$executablePath = (Resolve-Path -LiteralPath $Executable).ProviderPath
function Assert-ResidentStopped {
    $mutex = $null
    try { $mutex = [Threading.Mutex]::OpenExisting('Local\Minus700K_PTT_7E940DB1') }
    catch [Threading.WaitHandleCannotBeOpenedException] { return }
    if ($null -ne $mutex) { $mutex.Dispose(); throw 'Minus700K is running. This test will not stop or modify it.' }
}
Assert-ResidentStopped
$run = Join-Path $Workspace ('runtime\update-shutdown-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
$harnessTemplate = Join-Path $run 'UpdateShutdownHarness.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harnessTemplate" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'UpdateShutdownHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update shutdown harness build failed.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object Collections.Generic.List[object]
foreach ($mode in @('coordinator','resident','standalone','recovery')) {
    Assert-ResidentStopped
    # Keep the application path short enough for atomic config-save suffixes,
    # including when the workspace itself is on a deeply nested UNC share.
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('M700K-update-' + [Guid]::NewGuid().ToString('N'))
    $evidence = Join-Path $run $mode
    $bin = Join-Path $fixture 'bin'; $whisper = Join-Path $fixture 'vendor\whisper'
    New-Item -ItemType Directory -Path $evidence,$bin,$whisper -Force | Out-Null
    $app = Join-Path $bin 'Minus700K.exe'; $harness = Join-Path $bin 'UpdateShutdownHarness.exe'
    Copy-Item -LiteralPath $executablePath -Destination $app
    Copy-Item -LiteralPath $harnessTemplate -Destination $harness
    foreach ($path in @((Join-Path $whisper 'whisper-cli.exe'),(Join-Path $fixture 'model.bin'),(Join-Path $fixture 'codex.exe'))) { [IO.File]::WriteAllBytes($path,[byte[]]@(0)) }
    $config = Get-Content -LiteralPath (Join-Path $Workspace 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $config.AppLanguage = 'ja'; $config.Language = 'ja'; $config.SpeechVoice = ''
    $config.PushToTalkKey = 'F21'; $config.CancelKey = 'F22'; $config.ReplayKey = 'F23'
    $config.WhisperModel = 'model.bin'; $config.CodexPath = Join-Path $fixture 'codex.exe'
    $config.KeepRequestFiles = $false
    $config.PlayCues = $false; $config.PlayProcessingCues = $false; $config.SpeechVolume = 0; $config.CueVolume = 0
    $config.MicrophoneDevice = if ($mode -eq 'recovery') { 32767 } else { -1 }
    [IO.File]::WriteAllText((Join-Path $fixture 'config.json'),($config | ConvertTo-Json -Depth 20),$utf8)
    [IO.File]::WriteAllText((Join-Path $fixture 'update-test.txt'),$mode,$utf8)
    $reportPath = Join-Path $evidence 'report.json'
    $stdout = Join-Path $evidence 'launcher.stdout.log'; $stderr = Join-Path $evidence 'launcher.stderr.log'
    $arguments = foreach ($argument in @($harness,$app,$fixture,$reportPath)) {
        if ($argument.Contains('"')) { throw 'Unexpected quote in diagnostic path.' }
        '"' + $argument + '"'
    }
    $process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $fixture -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $null = $process.Handle
    if (-not $process.WaitForExit(30000)) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue; throw "Update shutdown launcher timed out: $evidence" }
    $process.WaitForExit()
    if (-not (Test-Path -LiteralPath $reportPath)) { throw "Update shutdown did not create a report ($mode, exit $($process.ExitCode)): $evidence" }
    $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or -not $report.passed) { throw "Update shutdown failed ($mode): $($report.error). Evidence: $evidence" }
    $summary = Get-Content -LiteralPath $stdout -Raw
    if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Missing private desktop summary: $evidence" }
    $results.Add([pscustomobject]@{mode=$mode;checks=$report.checks.Count;foregroundMismatchSamples=[int]$Matches[1];fixture=$fixture;report=$reportPath})
    Write-Output "Update shutdown passed ($mode, $($report.checks.Count) checks)."
}
Assert-ResidentStopped
$result = [ordered]@{passed=$true;executable=$executablePath;cases=@($results.ToArray());evidenceDirectory=$run}
$json = $result | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $run 'report.json'),$json,$utf8)
[IO.File]::WriteAllText((Join-Path $Workspace 'runtime\verify-update-shutdown.json'),$json,$utf8)
Write-Output "Update shutdown verification passed. Evidence: $run"

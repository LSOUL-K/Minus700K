param([string]$Executable = '', [string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Workspace 'bin\Minus700K.exe' }
$executablePath = (Resolve-Path -LiteralPath $Executable).ProviderPath
function Assert-ResidentStopped {
    $mutex = $null
    try { $mutex = [Threading.Mutex]::OpenExisting('Local\Minus700K_PTT_7E940DB1') }
    catch [Threading.WaitHandleCannotBeOpenedException] { return }
    if ($null -ne $mutex) { $mutex.Dispose(); throw 'Minus700K is running. Close it before this test; the test will not stop it.' }
}
Assert-ResidentStopped
$run = Join-Path $Workspace ('runtime\installer-startup-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$launcher = Join-Path $run 'HiddenDesktopLauncher.exe'
$harnessTemplate = Join-Path $run 'InstallerStartupHarness.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 "/out:$launcher" (Join-Path $PSScriptRoot 'HiddenDesktopLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Private desktop launcher build failed.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/out:$harnessTemplate" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'InstallerStartupHarness.cs')
if ($LASTEXITCODE -ne 0) { throw 'Installer startup harness build failed.' }
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object Collections.Generic.List[object]
foreach ($language in @('ja','en')) {
    foreach ($mode in @('post-install','normal')) {
        Assert-ResidentStopped
        $fixture = Join-Path $run ($language + '-' + $mode)
        $bin = Join-Path $fixture 'bin'
        $whisper = Join-Path $fixture 'vendor\whisper'
        New-Item -ItemType Directory -Path $bin,$whisper -Force | Out-Null
        $app = Join-Path $bin 'Minus700K.exe'
        $harness = Join-Path $bin 'InstallerStartupHarness.exe'
        Copy-Item -LiteralPath $executablePath -Destination $app
        Copy-Item -LiteralPath $harnessTemplate -Destination $harness
        # Startup only checks these paths. No recognition, Codex process, AI
        # request, or recording is performed by this fixture.
        foreach ($path in @((Join-Path $whisper 'whisper-cli.exe'),(Join-Path $fixture 'model.bin'),(Join-Path $fixture 'codex.exe'))) {
            [IO.File]::WriteAllBytes($path, [byte[]]@(0))
        }
        $config = Get-Content -LiteralPath (Join-Path $Workspace 'config.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $config.AppLanguage = $language; $config.Language = $language; $config.SpeechVoice = ''
        $config.PushToTalkKey = 'F21'; $config.CancelKey = 'F22'; $config.ReplayKey = 'F23'
        $config.WhisperModel = 'model.bin'; $config.CodexPath = Join-Path $fixture 'codex.exe'
        $config.KeepRequestFiles = $false
        $config.PlayCues = $false; $config.PlayProcessingCues = $false; $config.SpeechVolume = 0; $config.CueVolume = 0
        [IO.File]::WriteAllText((Join-Path $fixture 'config.json'), ($config | ConvertTo-Json -Depth 20), $utf8)
        [IO.File]::WriteAllText((Join-Path $fixture 'startup-test.json'), (@{language=$language;postInstall=($mode -eq 'post-install')} | ConvertTo-Json), $utf8)
        $reportPath = Join-Path $fixture 'report.json'
        $stdout = Join-Path $fixture 'launcher.stdout.log'; $stderr = Join-Path $fixture 'launcher.stderr.log'
        $arguments = foreach ($argument in @($harness,$app,$fixture,$reportPath)) {
            if ($argument.Contains('"')) { throw 'Unexpected quote in diagnostic path.' }
            '"' + $argument + '"'
        }
        $process = Start-Process -FilePath $launcher -ArgumentList ($arguments -join ' ') -WorkingDirectory $fixture -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
        $null = $process.Handle
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            throw "Installer startup launcher timed out. Evidence: $fixture"
        }
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $reportPath)) { throw "Installer startup failed ($language/$mode, exit $($process.ExitCode)). Evidence: $fixture" }
        $report = Get-Content -LiteralPath $reportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $report.passed) { throw "Installer startup failed ($language/$mode): $($report.error)" }
        $summary = Get-Content -LiteralPath $stdout -Raw
        if ($summary -notmatch 'activeDesktopForegroundMismatchSamples=(\d+)') { throw "Missing private desktop summary: $fixture" }
        $results.Add([pscustomobject]@{language=$language;mode=$mode;checks=$report.checks.Count;foregroundMismatchSamples=[int]$Matches[1];report=$reportPath})
        Write-Output "Installer startup passed ($language/$mode, $($report.checks.Count) checks)."
    }
}
Assert-ResidentStopped
$result = [ordered]@{passed=$true;executable=$executablePath;cases=@($results.ToArray());evidenceDirectory=$run}
$json = $result | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $run 'report.json'), $json, $utf8)
[IO.File]::WriteAllText((Join-Path $Workspace 'runtime\verify-installer-startup.json'), $json, $utf8)
Write-Output "Installer startup verification passed. Evidence: $run"

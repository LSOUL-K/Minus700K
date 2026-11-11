param([switch]$IncludeCodex,[string]$Executable='')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = if ($Executable) { $Executable } else { Join-Path $root 'bin\Minus700K.exe' }
foreach ($mode in @('localization-test','english-behavior-test','plan-auth-test','plan-transport-test','codex-storage-test','settings-storage-test','settings-ui-test','model-settings-test','self-test','processing-cue-test','logging-test','transcription-storage-test','context-test','instructions-test','search-policy-test','search-trigger-test','speech-text-test','capture-fallback-test','replay-test','search-fallback-test','recent-history-test','history-isolation-test') + $(if ($IncludeCodex) { @('pipeline-test','application-test','search-live-test','search-keyword-live-test','voice-only-test','auto-search-live-test','history-isolation-live-test') } else { @() })) {
    $proc = Start-Process -FilePath $exe -ArgumentList "--$mode" -WorkingDirectory $root -WindowStyle Hidden -Wait -PassThru
    $report = Get-Content -LiteralPath (Join-Path $root "runtime\$mode.json") -Raw | ConvertFrom-Json
    if ($proc.ExitCode -ne 0 -or -not $report.passed) { throw "$mode failed: $($report.error)" }
    Write-Output "$mode passed ($($report.elapsedMs) ms), foreground mismatch samples: $($report.foregroundMismatchSamples)"
}

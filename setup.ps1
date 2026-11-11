param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-Location -LiteralPath $PSScriptRoot
$target = Join-Path $PSScriptRoot 'config.json'
if (Test-Path -LiteralPath $target) { & (Join-Path $PSScriptRoot 'migrate-config.ps1') -ApplicationRoot $PSScriptRoot -RemoveLegacyFiles }
else { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config.example.json') -Destination $target }
& (Join-Path $PSScriptRoot 'install-codex.ps1') -ApplicationRoot $PSScriptRoot | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$downloads = Join-Path $PSScriptRoot 'vendor'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
function Get-VerifiedFile($Url, $Target, $Hash) {
    if ((Test-Path -LiteralPath $Target) -and ((Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash -eq $Hash)) { return }
    $partial = "$Target.download"
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        try { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial; break }
        catch { if ($attempt -eq 2) { throw }; Start-Sleep -Seconds (2 + $attempt * 2) }
    }
    if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) { throw "SHA256 mismatch: $Target" }
    Move-Item -LiteralPath $partial -Destination $Target -Force
}
$archive = Join-Path $downloads 'whisper-b5454-x64.zip'
Get-VerifiedFile 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip' $archive '6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb'
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $downloads 'whisper') -Force
Get-VerifiedFile 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin' (Join-Path $downloads 'ggml-small-q5_1.bin') 'ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') }
Write-Output 'Developer setup complete. Start Minus700K, open Settings from its notification-area icon, and select Connection > Continue with ChatGPT.'

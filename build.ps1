param([string]$OutputPath = 'bin\Minus700K.exe')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$speech = Get-ChildItem -LiteralPath (Join-Path $env:WINDIR 'Microsoft.NET\assembly\GAC_MSIL\System.Speech') -Filter System.Speech.dll -Recurse | Select-Object -First 1 -ExpandProperty FullName
$output = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $PSScriptRoot $OutputPath }
New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
$iconRoot = Join-Path $PSScriptRoot 'assets\icons'
$mainIcon = Join-Path $iconRoot 'minus700k.ico'
if (-not (Test-Path -LiteralPath $mainIcon -PathType Leaf)) { throw "Application icon is missing: $mainIcon" }
$iconResources = foreach ($name in @('tray-ready','tray-recording','tray-processing','tray-speaking','tray-paused')) {
    $icon = Join-Path $iconRoot "$name.ico"
    if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw "Tray icon is missing: $icon" }
    "/resource:$icon,Minus700K.Icons.$name.ico"
}
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$localeResources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'locales') -Recurse -File -Filter '*.json' | ForEach-Object {
    $locale = $_.Directory.Name
    "/resource:$($_.FullName),Minus700K.Locales.$locale.$($_.Name)"
})
if ($localeResources.Count -eq 0) { throw 'Localization catalogs are missing.' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /unsafe /codepage:65001 "/out:$output" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$mainIcon" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Security.dll "/r:$speech" $iconResources $localeResources $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Output "Built $output"

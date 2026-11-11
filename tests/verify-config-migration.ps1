param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Workspace $Workspace
    if ($LASTEXITCODE -ne 0) { throw 'Migration verification failed in Windows PowerShell.' }
    return
}
Add-Type -AssemblyName System.Web.Extensions
$serializer = New-Object Web.Script.Serialization.JavaScriptSerializer
$serializer.MaxJsonLength = 8388608
$scriptPath = Join-Path $Workspace 'migrate-config.ps1'
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$work = Join-Path ([IO.Path]::GetTempPath()) ('M700K-migration-' + [Guid]::NewGuid().ToString('N').Substring(0,12))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$checks = New-Object Collections.Generic.List[string]
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw "Migration test failed: $Message" } }
function Write-Text([string]$Path, [string]$Text) { [IO.File]::WriteAllText($Path, $Text, $utf8) }
function Save-Config([string]$Directory, $Config) { Write-Text (Join-Path $Directory 'config.json') (ConvertTo-Json -InputObject $Config -Depth 100) }
function Read-Config([string]$Directory) { $serializer.DeserializeObject([IO.File]::ReadAllText((Join-Path $Directory 'config.json'), $utf8)) }
function New-Fixture([string]$Name) { $path = Join-Path $work $Name; New-Item -ItemType Directory -Path $path -Force | Out-Null; return $path }
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Expect-Failure([string]$Directory, [string]$Name) {
    $configPath = Join-Path $Directory 'config.json'
    $before = Hash $configPath
    $failed = $false
    try { & $scriptPath -ApplicationRoot $Directory -RemoveLegacyFiles | Out-Null } catch { $failed = $true }
    Check $failed "$Name must fail"
    Check ((Hash $configPath) -eq $before) "$Name must preserve config bytes"
    $checks.Add($Name)
}

# Construct Unicode without depending on the PowerShell script's source encoding.
$jp = [string][char]0x65E5 + [char]0x672C + [char]0x8A9E
$custom = $jp + "`r`n" + '# comment stays' + "`nquote: `"ok`" / path: C:\sample\" + "`t" + [char]0x2605
$triggers = '# keep this comment' + "`r`n" + $jp + "`nlatest`n`n"
$normal = New-Fixture 'unicode-and-off'
$legacy = [ordered]@{ CustomInstructionsFile = 'custom-instructions.txt'; WebSearchTriggersFile = 'web-search-triggers.txt'; CustomInstructionsEnabled = $false; WebSearchTriggersEnabled = $true; Unknown = @{ Text = ($jp + "`nunknown"); Numbers = @(1,2,3); Flag = $false; IsoDate = '2026-11-09T12:34:56.123+09:00' }; SpeechVolume = 41 }
Save-Config $normal $legacy
[IO.File]::WriteAllText((Join-Path $normal 'config.json'), [IO.File]::ReadAllText((Join-Path $normal 'config.json'), $utf8), (New-Object Text.UTF8Encoding($true, $true)))
Write-Text (Join-Path $normal 'custom-instructions.txt') $custom
[IO.File]::WriteAllText((Join-Path $normal 'web-search-triggers.txt'), $triggers, (New-Object Text.UTF8Encoding($true, $true)))
Write-Text (Join-Path $normal 'custom-instructions.example.txt') 'obsolete sample 1'
Write-Text (Join-Path $normal 'web-search-triggers.example.txt') 'obsolete sample 2'
$beforeConfig = Hash (Join-Path $normal 'config.json')
$beforeText = Hash (Join-Path $normal 'web-search-triggers.txt')
& $scriptPath -ApplicationRoot $normal -RemoveLegacyFiles | Out-Null
$migrated = Read-Config $normal
Check ($migrated.CustomInstructionsText -ceq $custom -and $migrated.WebSearchTriggersText -ceq $triggers) 'Unicode, BOM, comments, quotes and mixed line endings survive'
Check (-not $migrated.CustomInstructionsEnabled -and $migrated.WebSearchTriggersEnabled) 'OFF state survives'
Check ($migrated.SpeechVolume -eq 41 -and $migrated.Unknown.Text -ceq ($jp + "`nunknown") -and $migrated.Unknown.Numbers.Count -eq 3 -and -not $migrated.Unknown.Flag) 'unknown nested fields survive'
Check ($migrated.Unknown.IsoDate -is [string] -and $migrated.Unknown.IsoDate -ceq '2026-11-09T12:34:56.123+09:00') 'unknown ISO date remains an exact string'
Check (-not $migrated.ContainsKey('CustomInstructionsFile') -and -not $migrated.ContainsKey('WebSearchTriggersFile')) 'legacy path fields are removed'
foreach ($name in @('custom-instructions.txt','web-search-triggers.txt','custom-instructions.example.txt','web-search-triggers.example.txt')) { Check (-not (Test-Path -LiteralPath (Join-Path $normal $name))) "obsolete file removed: $name" }
$backup = @(Get-ChildItem -LiteralPath (Join-Path $normal 'runtime\config-migration-backups') -Directory)
Check ($backup.Count -eq 1) 'one migration backup exists'
Check ((Hash (Join-Path $backup[0].FullName 'config.json')) -eq $beforeConfig -and (Hash (Join-Path $backup[0].FullName 'web-search-triggers.txt')) -eq $beforeText) 'backup preserves exact original bytes'
$once = Hash (Join-Path $normal 'config.json')
& $scriptPath -ApplicationRoot $normal -RemoveLegacyFiles | Out-Null
Check ((Hash (Join-Path $normal 'config.json')) -eq $once -and @(Get-ChildItem -LiteralPath (Join-Path $normal 'runtime\config-migration-backups') -Directory).Count -eq 1) 'second run makes no changes'
$checks.Add('Unicode / BOM / line endings / comments / OFF / unknown fields / backups / cleanup / idempotence')

$empty = New-Fixture 'empty-and-legacy-disabled'
Save-Config $empty @{ CustomInstructionsFile = ''; WebSearchTriggersFile = 'web-search-triggers.txt'; CustomInstructionsEnabled = $true; WebSearchTriggersEnabled = $false }
Write-Text (Join-Path $empty 'custom-instructions.txt') $custom
Write-Text (Join-Path $empty 'web-search-triggers.txt') ''
& $scriptPath -ApplicationRoot $empty | Out-Null
$value = Read-Config $empty
Check (-not $value.CustomInstructionsEnabled -and -not $value.WebSearchTriggersEnabled -and $value.CustomInstructionsText -ceq $custom -and $value.WebSearchTriggersText -ceq '') 'legacy empty path remains disabled and edited standard text survives; empty text remains empty'
Check (Test-Path -LiteralPath (Join-Path $empty 'custom-instructions.txt')) 'cleanup requires explicit switch'
$checks.Add('legacy empty path / edited standard text / empty content / opt-in cleanup')

$inline = New-Fixture 'inline-authoritative'
Save-Config $inline @{ CustomInstructionsText = ''; WebSearchTriggersText = $triggers; CustomInstructionsEnabled = $true; WebSearchTriggersEnabled = $false; CustomInstructionsFile = 'missing-old.txt'; WebSearchTriggersFile = 'missing-old-2.txt' }
& $scriptPath -ApplicationRoot $inline -RemoveLegacyFiles | Out-Null
$value = Read-Config $inline
Check ($value.CustomInstructionsText -ceq '' -and $value.WebSearchTriggersText -ceq $triggers -and $value.CustomInstructionsEnabled -and -not $value.WebSearchTriggersEnabled) 'existing inline fields win without reading missing legacy paths'
$checks.Add('existing inline text is authoritative')
$mixed = New-Fixture 'mixed-inline-empty-path'
Save-Config $mixed @{ CustomInstructionsText = $custom; WebSearchTriggersText = $triggers; CustomInstructionsEnabled = $true; WebSearchTriggersEnabled = $true; CustomInstructionsFile = ''; WebSearchTriggersFile = $null }
& $scriptPath -ApplicationRoot $mixed | Out-Null
$value = Read-Config $mixed
Check (-not $value.CustomInstructionsEnabled -and -not $value.WebSearchTriggersEnabled -and $value.CustomInstructionsText -ceq $custom -and $value.WebSearchTriggersText -ceq $triggers) 'mixed inline text survives while legacy empty or null paths stay disabled'
$checks.Add('mixed inline text and disabled legacy paths')

$external = New-Fixture 'external-path'
$externalFile = Join-Path $work 'user-owned.txt'
Write-Text $externalFile $custom
Save-Config $external @{ CustomInstructionsFile = $externalFile; WebSearchTriggersText = ''; WebSearchTriggersEnabled = $false }
& $scriptPath -ApplicationRoot $external -RemoveLegacyFiles | Out-Null
Check ((Test-Path -LiteralPath $externalFile) -and (Read-Config $external).CustomInstructionsText -ceq $custom) 'external path is read but never removed'
$checks.Add('external file preserved')

$missing = New-Fixture 'missing-file'
Save-Config $missing @{ CustomInstructionsFile = 'missing.txt'; WebSearchTriggersText = '' }
Write-Text (Join-Path $missing 'web-search-triggers.txt') 'keep me'
Expect-Failure $missing 'missing nonempty file path'
Check (Test-Path -LiteralPath (Join-Path $missing 'web-search-triggers.txt')) 'failed migration never cleans up files'

$invalid = New-Fixture 'invalid-json'
Write-Text (Join-Path $invalid 'config.json') '{"CustomInstructionsFile":'
Expect-Failure $invalid 'invalid JSON'
$badConfig = New-Fixture 'invalid-config-utf8'
[IO.File]::WriteAllBytes((Join-Path $badConfig 'config.json'), [byte[]]@(123,34,120,34,58,34,255,34,125))
Expect-Failure $badConfig 'invalid config UTF-8'
$badText = New-Fixture 'invalid-text-utf8'
Save-Config $badText @{ CustomInstructionsFile = 'custom-instructions.txt'; WebSearchTriggersText = '' }
[IO.File]::WriteAllBytes((Join-Path $badText 'custom-instructions.txt'), [byte[]]@(255,254,65))
Expect-Failure $badText 'invalid legacy text UTF-8'
$locked = New-Fixture 'read-failure'
Save-Config $locked @{ CustomInstructionsFile = 'locked.txt'; WebSearchTriggersText = '' }
Write-Text (Join-Path $locked 'locked.txt') 'cannot read during test'
$lock = [IO.File]::Open((Join-Path $locked 'locked.txt'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Expect-Failure $locked 'legacy file read failure' } finally { $lock.Dispose() }
$large = New-Fixture 'oversize-text'
Save-Config $large @{ CustomInstructionsText = ('x' * 250001); WebSearchTriggersText = '' }
Expect-Failure $large 'oversized inline text'
$largeFile = New-Fixture 'oversize-file'
[IO.File]::WriteAllBytes((Join-Path $largeFile 'config.json'), (New-Object byte[] 8388609))
Expect-Failure $largeFile 'oversized config bytes'
$expanded = New-Fixture 'oversize-migrated-bytes'
$wide = [string][char]0x65E5
$nearLimit = @{ Unknown = ($wide * 2795500); CustomInstructionsFile = 'custom-instructions.txt'; WebSearchTriggersText = '' }
Save-Config $expanded $nearLimit
Write-Text (Join-Path $expanded 'custom-instructions.txt') ($wide * 2000)
Check ((Get-Item -LiteralPath (Join-Path $expanded 'config.json')).Length -lt 8388608) 'serialized-size fixture starts below byte limit'
Expect-Failure $expanded 'migrated config byte limit'

$defaults = $serializer.DeserializeObject([IO.File]::ReadAllText((Join-Path $Workspace 'config.example.json'), $utf8))
$catalog = $serializer.DeserializeObject([IO.File]::ReadAllText((Join-Path $Workspace 'locales\ja\prompts.json'), $utf8))
foreach ($pair in @(@{ Key = 'defaults.customInstructions'; Field = 'CustomInstructionsText' }, @{ Key = 'defaults.searchTriggers'; Field = 'WebSearchTriggersText' })) {
    Check ($defaults.ContainsKey($pair.Field) -and $defaults[$pair.Field] -is [string]) "example contains string setting: $($pair.Field)"
    Check ($catalog.ContainsKey($pair.Key) -and $catalog[$pair.Key] -is [string]) "Japanese catalog contains string default: $($pair.Key)"
    Check ($catalog[$pair.Key] -ceq $defaults[$pair.Field]) "example matches built-in default: $($pair.Field)"
}
$checks.Add('config example matches both built-in defaults')
[pscustomobject]@{ Passed = $true; FixtureRoot = $work; Checks = $checks.ToArray() } | ConvertTo-Json -Depth 5

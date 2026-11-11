param([string]$ApplicationRoot = $PSScriptRoot, [switch]$RemoveLegacyFiles)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # Match the application's .NET Framework serializer, including string values.
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-ApplicationRoot',$ApplicationRoot)
    if ($RemoveLegacyFiles) { $arguments += '-RemoveLegacyFiles' }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Config migration failed in Windows PowerShell.' }
    return
}
Add-Type -AssemblyName System.Web.Extensions
$serializer = New-Object Web.Script.Serialization.JavaScriptSerializer
$serializer.MaxJsonLength = 8388608
$serializer.RecursionLimit = 100
$root = [IO.Path]::GetFullPath($ApplicationRoot).TrimEnd('\','/')
$configPath = Join-Path $root 'config.json'
$utf8 = New-Object Text.UTF8Encoding($false, $true)

function Read-Snapshot([string]$Path, [switch]$Text) {
    if ((Get-Item -LiteralPath $Path -Force).Length -gt 8388608) { throw "File exceeds the 8 MiB limit: $Path" }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 8388608) { throw "File exceeds the 8 MiB limit: $Path" }
    $value = $null
    if ($Text) {
        $value = $utf8.GetString($bytes)
        if ($value.Length -gt 0 -and $value[0] -eq [char]0xFEFF) { $value = $value.Substring(1) }
    }
    [pscustomobject]@{ Path = $Path; Bytes = $bytes; Text = $value }
}
function Same-Bytes([byte[]]$First, [byte[]]$Second) {
    if ($First.Length -ne $Second.Length) { return $false }
    for ($i = 0; $i -lt $First.Length; $i++) { if ($First[$i] -ne $Second[$i]) { return $false } }
    return $true
}
function Read-Config([string]$Text) {
    if ($Text.Length -gt 8388608) { throw 'config.json exceeds the 8 MiB JSON character limit.' }
    $value = $serializer.DeserializeObject($Text)
    if ($null -eq $value -or $value -isnot [Collections.IDictionary]) { throw 'config.json must contain a JSON object.' }
    return $value
}
function Property($Object, [string]$Name) {
    $matches = @($Object.Keys | Where-Object { $_ -ieq $Name })
    if ($matches.Count -gt 1) { throw "Ambiguous setting: $Name" }
    if ($matches.Count -eq 1) { return [pscustomobject]@{ Name = $matches[0]; Value = $Object[$matches[0]] } }
    return $null
}
function Set-Property($Object, [string]$Name, $Value) {
    $existing = Property $Object $Name
    if ($null -ne $existing) { $Object.Remove($existing.Name) | Out-Null }
    $Object[$Name] = $Value
}
function Assert-Unchanged($Snapshot) {
    if (-not (Same-Bytes $Snapshot.Bytes ([IO.File]::ReadAllBytes($Snapshot.Path)))) { throw "File changed during migration: $($Snapshot.Path)" }
}
function Backup-File($Snapshot, [string]$Destination) {
    [IO.File]::WriteAllBytes($Destination, $Snapshot.Bytes)
    if (-not (Same-Bytes $Snapshot.Bytes ([IO.File]::ReadAllBytes($Destination)))) { throw "Backup verification failed: $Destination" }
}

# Use the same per-config mutex as the settings UI. Manual file edits are checked
# again immediately before replacement; run this upgrade with the app stopped.
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($configPath.ToUpperInvariant()))).Replace('-','').Substring(0,24) }
finally { $sha.Dispose() }
$gate = New-Object Threading.Mutex($false, ('Local\Minus700K_Settings_' + $digest))
$acquired = $false
$temporary = $null
try {
    try { $acquired = $gate.WaitOne(10000) } catch [Threading.AbandonedMutexException] { $acquired = $true }
    if (-not $acquired) { throw 'Another process is updating the settings. Try again after it finishes.' }
    $original = Read-Snapshot $configPath -Text
    $config = Read-Config $original.Text
    $defaults = Read-Config ((Read-Snapshot (Join-Path $PSScriptRoot 'config.example.json') -Text).Text)
    $sources = New-Object Collections.Generic.List[object]
    $changed = $false
    foreach ($pair in @(
        @{ Text = 'CustomInstructionsText'; File = 'CustomInstructionsFile'; Enabled = 'CustomInstructionsEnabled'; Standard = 'custom-instructions.txt' },
        @{ Text = 'WebSearchTriggersText'; File = 'WebSearchTriggersFile'; Enabled = 'WebSearchTriggersEnabled'; Standard = 'web-search-triggers.txt' }
    )) {
        $textProperty = Property $config $pair.Text
        $fileProperty = Property $config $pair.File
        $enabledProperty = Property $config $pair.Enabled
        if ($null -ne $enabledProperty -and $enabledProperty.Value -isnot [bool]) { throw "$($pair.Enabled) must be true or false." }
        if ($null -ne $fileProperty -and $null -ne $fileProperty.Value -and $fileProperty.Value -isnot [string]) { throw "$($pair.File) must be a file path or null." }
        $legacyDisabled = $null -ne $fileProperty -and [string]::IsNullOrWhiteSpace([string]$fileProperty.Value)
        if ($null -eq $textProperty) {
            if ($null -ne $fileProperty -and -not $legacyDisabled) {
                $path = [string]$fileProperty.Value
                if (-not [IO.Path]::IsPathRooted($path)) { $path = Join-Path $root $path }
                $source = Read-Snapshot ([IO.Path]::GetFullPath($path)) -Text
                $sources.Add($source)
                $textValue = $source.Text
            } else {
                $standardPath = Join-Path $root $pair.Standard
                if (Test-Path -LiteralPath $standardPath -PathType Leaf) {
                    $source = Read-Snapshot $standardPath -Text
                    $sources.Add($source)
                    $textValue = $source.Text
                } else { $textValue = (Property $defaults $pair.Text).Value }
            }
            if ($textValue -isnot [string]) { throw "Missing text default: $($pair.Text)" }
            Set-Property $config $pair.Text $textValue
            $changed = $true
        } elseif ($textProperty.Value -isnot [string]) { throw "$($pair.Text) must be a string." }
        if ((Property $config $pair.Text).Value.Length -gt 250000) { throw "$($pair.Text) exceeds 250,000 characters." }
        if ($legacyDisabled) { Set-Property $config $pair.Enabled $false; $changed = $true }
        if ($null -eq (Property $config $pair.Enabled)) { Set-Property $config $pair.Enabled (-not $legacyDisabled); $changed = $true }
        if ($null -ne $fileProperty) { $config.Remove($fileProperty.Name) | Out-Null; $changed = $true }
    }
    $serialized = $serializer.Serialize($config)
    if ($utf8.GetByteCount($serialized) -gt 8388608) { throw 'Migrated config exceeds the 8 MiB file limit.' }
    $roundtrip = Read-Config $serialized
    foreach ($name in @('CustomInstructionsText','WebSearchTriggersText')) {
        if ((Property $roundtrip $name).Value -cne (Property $config $name).Value) { throw "Text roundtrip failed: $name" }
    }
    $obsolete = New-Object Collections.Generic.List[object]
    if ($RemoveLegacyFiles) {
        foreach ($name in @('custom-instructions.txt','web-search-triggers.txt','custom-instructions.example.txt','web-search-triggers.example.txt')) {
            $path = [IO.Path]::GetFullPath((Join-Path $root $name))
            if ([IO.Path]::GetDirectoryName($path) -ine $root) { throw "Cleanup target is outside the application directory: $path" }
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $item = Get-Item -LiteralPath $path -Force
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Write-Warning "Legacy link was preserved: $path"; continue }
                $obsolete.Add((Read-Snapshot $path))
            }
        }
    }
    if (-not $changed -and $obsolete.Count -eq 0) { Write-Output 'Config already uses inline text. No files changed.'; return }
    $backupRoot = Join-Path $root ('runtime\config-migration-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($backupRoot) | Out-Null
    Backup-File $original (Join-Path $backupRoot 'config.json')
    foreach ($old in $obsolete) { Backup-File $old (Join-Path $backupRoot ([IO.Path]::GetFileName($old.Path))) }
    foreach ($source in $sources) { Assert-Unchanged $source }
    Assert-Unchanged $original
    if ($changed) {
        $temporary = Join-Path $root ('.config-migrate-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($temporary, $serialized, $utf8)
        $verified = Read-Config ((Read-Snapshot $temporary -Text).Text)
        if ($serializer.Serialize($verified) -cne $serialized) { throw 'Temporary config verification failed.' }
        foreach ($source in $sources) { Assert-Unchanged $source }
        Assert-Unchanged $original
        [IO.File]::Replace($temporary, $configPath, [Management.Automation.Language.NullString]::Value)
        $temporary = $null
    }
    $committed = Read-Config ((Read-Snapshot $configPath -Text).Text)
    if ($serializer.Serialize($committed) -cne $serialized) { throw "Committed config verification failed. Backup: $backupRoot" }
    foreach ($old in $obsolete) {
        # Only exact, app-owned root files can be removed. Keep any concurrent edits.
        try {
            Assert-Unchanged $old
            $item = Get-Item -LiteralPath $old.Path -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'File became a link.' }
            Remove-Item -LiteralPath $old.Path -Force
        } catch { Write-Warning "Legacy file was preserved: $($old.Path). $($_.Exception.Message)" }
    }
    Write-Output "Config migration verified. Backup: $backupRoot"
} finally {
    if ($null -ne $temporary -and (Test-Path -LiteralPath $temporary -PathType Leaf)) { Remove-Item -LiteralPath $temporary -Force }
    if ($acquired) { $gate.ReleaseMutex() }
    $gate.Dispose()
}

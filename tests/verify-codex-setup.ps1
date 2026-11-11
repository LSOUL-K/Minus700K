param([string]$Workspace = (Split-Path -Parent $PSScriptRoot), [string]$Executable = '')
$ErrorActionPreference = 'Stop'
if (-not $Executable) { $Executable = Join-Path $Workspace 'bin\Minus700K.exe' }
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Workspace $Workspace -Executable $Executable
    if ($LASTEXITCODE -ne 0) { throw 'Codex setup verification failed in Windows PowerShell.' }
    return
}
. (Join-Path $Workspace 'install-codex.ps1') -ApplicationRoot $Workspace
$assembly = [Reflection.Assembly]::LoadFrom($Executable)
$locator = $assembly.GetType('Minus700K.AssistantService').GetMethod('FindCodex', [Reflection.BindingFlags]'Static,NonPublic', $null, [type[]]@([string],[string],[string],[string]), $null)
if ($null -eq $locator) { throw 'Build the updated application before running this test.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('M700K-codex-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$checks = New-Object Collections.Generic.List[string]
$utf8 = New-Object Text.UTF8Encoding($false)
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw "Codex setup test failed: $Message" } }
function Write-TestFile([string]$Path, [string]$Text = '') {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8)
    return $Path
}
function Check-Discovery([string]$Name, [string]$Configured, [string]$PathValue, [string]$LocalData, [string]$InstallDirectory, [string]$Expected) {
    $actual = Find-CodexExecutable -Configured $Configured -Root $work -PathValue $PathValue -LocalData $LocalData -InstallDirectory $InstallDirectory
    Check ($actual -eq $Expected) "$Name (setup)"
    $appConfigured = if ($Configured -and -not [IO.Path]::IsPathRooted($Configured)) { Join-Path $work $Configured } else { $Configured }
    $actual = $locator.Invoke($null, [object[]]@([string]$appConfigured,[string]$PathValue,[string]$LocalData,[string]$InstallDirectory))
    Check ($actual -eq $Expected) "$Name (application)"
    $checks.Add($Name)
}
$originalInvoke = ${function:Invoke-CodexInstaller}
$originalSignature = ${function:Assert-CodexInstallerSignature}
$previousEnvironment = @{}
foreach ($name in @('CODEX_NON_INTERACTIVE','CODEX_INSTALL_DAEMON_ONLY','CODEX_INSTALL_DEFER_SELECTION','MINUS700K_SETUP_TEST_RESULT')) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$originalLocation = Get-Location
try {
    $localData = Join-Path $work 'local-data'
    $standalone = Write-TestFile (Join-Path $localData 'Programs\OpenAI\Codex\bin\codex.exe')
    $desktopOld = Write-TestFile (Join-Path $localData 'OpenAI\Codex\bin\old\codex.exe')
    $desktopNew = Write-TestFile (Join-Path $localData 'OpenAI\Codex\bin\new\codex.exe')
    (Get-Item -LiteralPath $desktopOld).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-1)
    $custom = Write-TestFile (Join-Path $work 'custom-install\codex.exe')
    $pathExe = Write-TestFile (Join-Path $work 'path with spaces\codex.exe')
    $configured = Write-TestFile (Join-Path $work 'configured\codex.exe')
    $pathValue = ';"' + (Split-Path -Parent $pathExe) + '";'
    Check-Discovery 'Explicit configuration takes priority' 'configured\codex.exe' $pathValue $localData (Split-Path -Parent $custom) $configured
    Check-Discovery 'Quoted PATH with empty entries' '' $pathValue $localData (Split-Path -Parent $custom) $pathExe
    Check-Discovery 'Custom official installation directory' '' '' $localData (Split-Path -Parent $custom) $custom
    Check-Discovery 'Fresh installation without a refreshed PATH' '' '' $localData '' $standalone
    Remove-Item -LiteralPath $standalone
    Check-Discovery 'Existing desktop CLI, newest copy' '' '' $localData '' $desktopNew
    $missing = Join-Path $work 'missing-local-data'
    Check ($null -eq (Find-CodexExecutable -Root $work -PathValue '' -LocalData $missing -InstallDirectory '')) 'Missing CLI returns no candidate'
    $failed = $false
    try { $locator.Invoke($null, [object[]]@('','',$missing,'')) | Out-Null } catch { $failed = $true }
    Check $failed 'Application reports a missing CLI'
    $failed = $false
    try { Find-CodexExecutable -Configured 'missing.exe' -Root $work -PathValue $pathValue -LocalData $localData | Out-Null } catch { $failed = $true }
    Check $failed 'Invalid explicit path does not silently choose another installation'
    $checks.Add('Missing CLI and invalid explicit path')

    $unsigned = Write-TestFile (Join-Path $work 'unsigned.ps1') "throw 'must not execute'"
    $failed = $false
    try { & $originalSignature $unsigned } catch { $failed = $true }
    Check $failed 'Unsigned installer is rejected'
    $checks.Add('Unsigned installer rejected')

    $env:CODEX_NON_INTERACTIVE = 'original-value'
    $env:CODEX_INSTALL_DAEMON_ONLY = '1'
    $env:CODEX_INSTALL_DEFER_SELECTION = '1'
    $env:MINUS700K_SETUP_TEST_RESULT = Join-Path $work 'child-environment.json'
    $childScript = Write-TestFile (Join-Path $work 'fake-installer.ps1') '@{ NonInteractive=$env:CODEX_NON_INTERACTIVE; DaemonOnly=$env:CODEX_INSTALL_DAEMON_ONLY; Deferred=$env:CODEX_INSTALL_DEFER_SELECTION } | ConvertTo-Json | Set-Content -LiteralPath $env:MINUS700K_SETUP_TEST_RESULT; exit 0'
    & $originalInvoke $childScript
    $child = Get-Content -LiteralPath $env:MINUS700K_SETUP_TEST_RESULT -Raw | ConvertFrom-Json
    Check ($child.NonInteractive -eq '1' -and -not $child.DaemonOnly -and -not $child.Deferred) 'Installer runs without prompts or daemon-only overrides'
    Check ($env:CODEX_NON_INTERACTIVE -eq 'original-value' -and $env:CODEX_INSTALL_DAEMON_ONLY -eq '1' -and $env:CODEX_INSTALL_DEFER_SELECTION -eq '1') 'Parent environment restored after success'
    Write-TestFile $childScript 'exit 7' | Out-Null
    $failed = $false
    try { & $originalInvoke $childScript } catch { $failed = $_.Exception.Message -match 'code 7' }
    Check ($failed -and $env:CODEX_NON_INTERACTIVE -eq 'original-value') 'Installer failure propagates and restores environment'
    $checks.Add('Real child process success / failure / environment restoration')

    # Substitute only external operations. No network or installed CLI is changed.
    function Find-CodexExecutable { param($Configured,$Root) if ($script:scenario.Existing -or $script:scenario.Installed) { return $script:fakeExe } }
    function Invoke-WebRequest { param($Uri,$OutFile,$TimeoutSec,[switch]$UseBasicParsing)
        $script:scenario.Downloads++
        if ($script:scenario.Failure -eq 'download') { throw 'simulated download failure' }
        Check ($Uri -eq 'https://chatgpt.com/codex/install.ps1') 'Official installer URL'
        $script:downloadFile = $OutFile
        [IO.File]::WriteAllText($OutFile, 'test installer')
    }
    function Assert-CodexInstallerSignature { param($Installer)
        $script:scenario.Signatures++
        if ($script:scenario.Failure -eq 'signature') { throw 'simulated signature failure' }
    }
    function Invoke-CodexInstaller { param($Installer)
        Check ($script:scenario.Signatures -eq 1) 'Signature checked before executing'
        $script:scenario.Runs++
        if ($script:scenario.Failure -eq 'installer') { throw 'simulated installer failure' }
        $script:scenario.Installed = $script:scenario.Failure -ne 'not-found'
    }
    function Assert-CodexExecutable { param($Executable)
        $script:scenario.Versions++
        if ($script:scenario.Failure -eq 'version') { throw 'simulated invalid executable' }
    }
    $script:fakeExe = $configured
    $flowRoot = Join-Path $work 'flow'
    $flowConfig = Write-TestFile (Join-Path $flowRoot 'config.json') '{"CodexPath":"","CustomValue":"preserve"}'
    $configHash = (Get-FileHash -LiteralPath $flowConfig).Hash
    foreach ($case in @('existing','install','download','signature','installer','not-found','version')) {
        $script:scenario = @{ Existing=($case -eq 'existing'); Installed=$false; Failure=$case; Downloads=0; Signatures=0; Runs=0; Versions=0 }
        $script:downloadFile = $null
        $failed = $false
        try { $result = Install-CodexIfMissing -Root $flowRoot } catch { $failed = $true }
        if ($case -in @('existing','install')) {
            Check (-not $failed -and $result -eq $configured) "$case succeeds"
            if ($case -eq 'existing') { Check ($scenario.Downloads -eq 0 -and $scenario.Runs -eq 0) 'Existing CLI is reused without downloads' }
            else {
                Check ($scenario.Downloads -eq 1 -and $scenario.Runs -eq 1 -and $scenario.Versions -eq 1) 'Missing CLI installed and verified'
                Install-CodexIfMissing -Root $flowRoot | Out-Null
                Check ($scenario.Downloads -eq 1 -and $scenario.Runs -eq 1) 'Second run reuses the installed CLI'
            }
        } else { Check $failed "$case failure stops setup" }
        if ($case -in @('download','signature')) { Check ($scenario.Runs -eq 0) "$case failure prevents installer execution" }
        if ($script:downloadFile) { Check (-not (Test-Path -LiteralPath (Split-Path -Parent $script:downloadFile))) 'Temporary installer removed' }
        Check ((Get-FileHash -LiteralPath $flowConfig).Hash -eq $configHash) 'User configuration preserved'
        $checks.Add("Installation flow: $case")
    }

    $setupRoot = Join-Path $work 'setup-failure'
    Write-TestFile (Join-Path $setupRoot 'config.example.json') '{}' | Out-Null
    Write-TestFile (Join-Path $setupRoot 'install-codex.ps1') "throw 'simulated setup failure'" | Out-Null
    Copy-Item -LiteralPath (Join-Path $Workspace 'setup.ps1') -Destination (Join-Path $setupRoot 'setup.ps1')
    $failed = $false
    try { & (Join-Path $setupRoot 'setup.ps1') -SkipBuild } catch { $failed = $_.Exception.Message -match 'simulated setup failure' }
    Check ($failed -and -not (Test-Path -LiteralPath (Join-Path $setupRoot 'vendor'))) 'Setup stops before recognition downloads when CLI installation fails'
    $checks.Add('Setup integration stops on installation failure')

    $report = [ordered]@{ passed=$true; checks=@($checks.ToArray()); executable=$Executable }
    $reportPath = Join-Path $Workspace 'runtime\verify-codex-setup.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5), $utf8)
    Write-Output "Codex setup verification passed ($($checks.Count) checks). Report: $reportPath"
} finally {
    Set-Location -LiteralPath $originalLocation.Path
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $resolvedWork) -like 'M700K-codex-test-*') {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}

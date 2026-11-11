param([string]$Workspace = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Workspace $Workspace
    if ($LASTEXITCODE -ne 0) { throw 'Installer prerequisite verification failed in Windows PowerShell.' }
    return
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('M700K-dependencies-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
. (Join-Path $Workspace 'installer\prepare-dependencies.ps1') -ApplicationRoot $work -ErrorPath (Join-Path $work 'error.txt')
$originalInstall = ${function:Install-VisualCppRuntime}
$checks = New-Object Collections.Generic.List[string]
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw "Installer prerequisite test failed: $Message" } }

# All operations that could download or install software are replaced. Even the
# runtime probe is simulated, so this test never changes system prerequisites.
function Test-VisualCppRuntime {
    $script:state.Probes++
    if ($script:state.Probes -eq 1) { return $script:state.Existing }
    return $script:state.AfterInstall
}
function Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
    Check ($Uri -eq 'https://aka.ms/vc14/vc_redist.x64.exe') 'Download uses the official x64 URL'
    $script:state.Downloads++
    $script:state.TemporaryDirectory = Split-Path -Parent $OutFile
    $script:state.Events.Add('download')
    if ($script:state.DownloadFailure) { throw 'Simulated network failure' }
    [IO.File]::WriteAllText($OutFile, 'Simulated redistributable, never executable')
}
function Get-AuthenticodeSignature {
    param($LiteralPath)
    Check (Test-Path -LiteralPath $LiteralPath -PathType Leaf) 'Installer exists before verification'
    $script:state.Signatures++
    $script:state.Events.Add('signature')
    $certificate = $null
    if ($script:state.Publisher) {
        $certificate = [pscustomobject]@{ Publisher = $script:state.Publisher }
        $certificate | Add-Member -MemberType ScriptMethod -Name GetNameInfo -Value { param($Type,$ForIssuer) return $this.Publisher }
    }
    return [pscustomobject]@{ Status = $script:state.SignatureStatus; SignerCertificate = $certificate }
}
function Start-Process {
    param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    Check ($script:state.Events.Count -eq 2 -and $script:state.Events[0] -eq 'download' -and $script:state.Events[1] -eq 'signature') 'Signature validation precedes execution'
    Check ($Verb -eq 'RunAs' -and $WindowStyle -eq 'Hidden' -and $Wait -and $PassThru) 'Installer elevation and wait options are explicit'
    Check (($ArgumentList -join ' ') -eq '/install /passive /norestart') 'No automatic restart or unrelated installer operation'
    Check ((Split-Path -Parent $FilePath) -eq $script:state.TemporaryDirectory) 'Only the downloaded installer is executed'
    $script:state.Runs++
    $script:state.Events.Add('execute')
    if ($script:state.ProcessFailure) { throw 'Simulated cancelled administrator prompt' }
    return [pscustomobject]@{ ExitCode = $script:state.ExitCode }
}

try {
    foreach ($name in @('existing','install','unsigned','wrong-publisher','no-certificate','download-failure','installer-failure','newer-version-error','elevation-cancelled','restart-required','restart-initiated','postcheck-failed')) {
        $script:state = @{
            Existing = ($name -eq 'existing'); AfterInstall = ($name -ne 'postcheck-failed')
            DownloadFailure = ($name -eq 'download-failure'); ProcessFailure = ($name -eq 'elevation-cancelled')
            SignatureStatus = $(if ($name -eq 'unsigned') { 'NotSigned' } else { 'Valid' })
            Publisher = $(if ($name -eq 'wrong-publisher') { 'Unrelated publisher' } elseif ($name -eq 'no-certificate') { '' } else { 'Microsoft Corporation' })
            ExitCode = $(switch ($name) { 'installer-failure' { 1603 }; 'newer-version-error' { 1638 }; 'restart-required' { 3010 }; 'restart-initiated' { 1641 }; default { 0 } })
            Probes = 0; Downloads = 0; Signatures = 0; Runs = 0; TemporaryDirectory = $null
            Events = New-Object Collections.Generic.List[string]
        }
        $failed = $false
        $result = $null
        try { $result = & $originalInstall } catch { $failed = $true }
        if ($name -eq 'existing') {
            Check (-not $failed -and $result -eq 0 -and $state.Downloads -eq 0 -and $state.Runs -eq 0) 'An installed runtime requires no download or elevation'
        } elseif ($name -eq 'install') {
            Check (-not $failed -and $result -eq 0 -and $state.Probes -eq 2 -and $state.Runs -eq 1) 'A successful installation is verified afterward'
        } elseif ($name -in @('restart-required','restart-initiated')) {
            Check (-not $failed -and $result -eq 3010 -and $state.Probes -eq 1 -and $state.Runs -eq 1) "$name propagates restart before later dependency work"
        } else {
            Check $failed "$name must stop installation"
            if ($name -in @('unsigned','wrong-publisher','no-certificate','download-failure')) {
                Check ($state.Runs -eq 0) "$name must never execute the downloaded file"
            }
            if ($name -eq 'postcheck-failed') { Check ($state.Probes -eq 2) 'Runtime must be verified even when the installer reports success' }
        }
        if ($state.TemporaryDirectory) { Check (-not (Test-Path -LiteralPath $state.TemporaryDirectory)) "$name removes its temporary download directory" }
        $checks.Add($name)
    }

    function Install-VisualCppRuntime {
        if ($script:mainFailure) { throw $script:mainFailure }
        return 3010
    }
    $script:mainFailure = ''
    Check ((Invoke-InstallerDependencies) -eq 3010) 'Dependency entry point propagates restart without attempting Codex installation'
    $checks.Add('Entry point stops before Codex when a restart is needed')
    $script:mainFailure = 'Simulated dependency failure ' + [char]0x65E5 + [char]0x672C + [char]0x8A9E
    Check ((Invoke-InstallerDependencies 2>$null) -eq 1) 'Dependency entry point propagates prerequisite failure'
    $errorFile = Join-Path $work 'error.txt'
    $errorBytes = [IO.File]::ReadAllBytes($errorFile)
    Check ([Text.Encoding]::UTF8.GetString($errorBytes) -ceq $script:mainFailure) 'Error report preserves Unicode in UTF-8 without a BOM'
    $checks.Add('Entry point writes a UTF-8 error report and fails')

    $report = [ordered]@{ passed = $true; checks = @($checks.ToArray()); mockedNetworkAndInstall = $true }
    $reportPath = Join-Path $Workspace 'runtime\verify-installer-dependencies.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
    [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
    Write-Output "Installer prerequisite verification passed ($($checks.Count) checks). Report: $reportPath"
} finally {
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedWork.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedWork) -like 'M700K-dependencies-test-*') {
        Remove-Item -LiteralPath $resolvedWork -Recurse -Force
    }
}

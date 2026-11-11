param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [string]$HelperPath = '',
    [string]$ReportPath = ''
)
$ErrorActionPreference = 'Stop'
if (-not $HelperPath) { $HelperPath = Join-Path $Workspace 'installer\prepare-update.ps1' }
if (-not $ReportPath) { $ReportPath = Join-Path $Workspace 'runtime\verify-update-process-race.json' }
if ($PSVersionTable.PSEdition -eq 'Core') {
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Workspace $Workspace -HelperPath $HelperPath -ReportPath $ReportPath
    if ($LASTEXITCODE -ne 0) { throw 'Update process race verification failed in Windows PowerShell.' }
    return
}

# Import function definitions only. Never execute the helper's application
# shutdown entry point. All process/CIM calls below are deterministic doubles;
# this test neither starts nor stops an application or accesses its data.
$helper = (Resolve-Path -LiteralPath $HelperPath).ProviderPath
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput([IO.File]::ReadAllText($helper), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw 'The update helper contains PowerShell parse errors.' }
$definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]}, $true))
foreach ($functionName in @('Same-UpdatePath','Test-UpdateProcess','Get-UpdateProcesses')) {
    $definition = @($definitions | Where-Object Name -eq $functionName)
    if ($definition.Count -ne 1) { throw "Expected exactly one helper function: $functionName" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}

$script:expectedPath = 'C:\Minus700K-race-fixture\bin\Minus700K.exe'
$script:otherPath = 'C:\Unrelated-race-fixture\bin\Minus700K.exe'
$script:startedUtc = [DateTime]::UtcNow.AddMinutes(-1)
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $script:currentSid = $currentIdentity.User.Value } finally { $currentIdentity.Dispose() }
$currentProcess = [Diagnostics.Process]::GetCurrentProcess()
try { $script:currentSession = $currentProcess.SessionId } finally { $currentProcess.Dispose() }
$script:target = [pscustomobject]@{ Id=424242; StartedUtc=$script:startedUtc; Executable=$script:expectedPath; Standalone=$false; CommandLine='fixture' }
$results = New-Object Collections.Generic.List[object]
$checks = New-Object Collections.Generic.List[object]

function Reset-Fixture {
    $script:processMissing=$false; $script:cimMode='matching'; $script:ownerMode='matching'
    $script:exitDuringCim=$false; $script:exitDuringOwner=$false
    $script:processReads=0; $script:ownerReads=0; $script:cimReads=0
    $script:fixtureProcess = [pscustomobject]@{
        HasExited=$false; FixedStartTime=$script:startedUtc; ThrowStartTime=$false
        ExitWhenWait=$false; ThrowWait=$false; DisposedCount=0; WaitCalls=0; WaitMilliseconds=0
    }
    $script:fixtureProcess | Add-Member -MemberType ScriptProperty -Name StartTime -Value {
        if ($this.ThrowStartTime) { throw 'Fixture start time query failed.' }
        return $this.FixedStartTime
    }
    $script:fixtureProcess | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value {
        param([int]$milliseconds)
        $this.WaitCalls++; $this.WaitMilliseconds=$milliseconds
        if ($this.ThrowWait) { throw 'Fixture exit query failed.' }
        if ($this.ExitWhenWait) { $this.HasExited=$true }
        return $this.HasExited
    }
    $script:fixtureProcess | Add-Member -MemberType ScriptMethod -Name Dispose -Value { $this.DisposedCount++ }
    $script:fixtureIdentity = [pscustomobject]@{
        ProcessId=$script:target.Id; CreationDate=$script:startedUtc; ExecutablePath=$script:expectedPath
        SessionId=$script:currentSession; CommandLine='fixture --settings'
    }
}
function Get-Process {
    [CmdletBinding()] param([int]$Id)
    if ($Id -ne $script:target.Id) { throw 'A fixture attempted to query an unexpected process ID.' }
    if ($script:processMissing) { return $null }
    $script:processReads++
    return $script:fixtureProcess
}
function Get-CimInstance {
    [CmdletBinding()] param([string]$ClassName,[string]$Filter)
    if ($ClassName -ne 'Win32_Process' -or ($Filter -ne "Name='Minus700K.exe'" -and $Filter -ne ('ProcessId=' + $script:target.Id))) { throw 'Unexpected CIM fixture query.' }
    $script:cimReads++
    if ($script:exitDuringCim) { $script:fixtureProcess.HasExited=$true }
    if ($script:cimMode -eq 'throw') { throw 'Fixture CIM query failed.' }
    if ($script:cimMode -eq 'missing') { return $null }
    return $script:fixtureIdentity
}
function Invoke-CimMethod {
    [CmdletBinding()] param([object]$InputObject,[string]$MethodName)
    if ($MethodName -ne 'GetOwnerSid' -or $InputObject.ProcessId -ne $script:target.Id) { throw 'Unexpected CIM fixture method.' }
    $script:ownerReads++
    if ($script:exitDuringOwner) { $script:fixtureProcess.HasExited=$true }
    if ($script:ownerMode -eq 'throw') { throw 'Fixture owner query failed.' }
    return [pscustomobject]@{ ReturnValue=0; Sid=$(if ($script:ownerMode -eq 'different') { 'S-1-0-0' } else { $script:currentSid }) }
}
function Check([bool]$Condition,[string]$Message) {
    $checks.Add([pscustomobject]@{passed=$Condition; description=$Message})
}
function Invoke-FixtureCase([string]$Name,[string]$Expected,[scriptblock]$Arrange,[switch]$Enumerate,[string]$ExpectedError='') {
    Reset-Fixture
    & $Arrange
    $caught=$null; $actual=$null
    try {
        if ($Enumerate) {
            $found=@(Get-UpdateProcesses $script:expectedPath)
            $actual=[string]$found.Count
            if ($found.Count -gt 0) {
                Check ($found.Count -eq 1 -and $found[0].Id -eq $script:target.Id -and $found[0].Executable -eq $script:expectedPath -and $found[0].StartedUtc -eq $script:startedUtc) ($Name + ': target identity is preserved')
            }
        } else { $actual=[string](Test-UpdateProcess $script:target) }
    } catch { $caught=$_.Exception.Message; $actual='error' }
    Check ($actual -ceq $Expected) ($Name + ': expected ' + $Expected + ', got ' + $actual)
    if ($ExpectedError) { Check ($caught -ceq $ExpectedError) ($Name + ': the original failure is preserved') }
    Check ($script:fixtureProcess.DisposedCount -eq $script:processReads) ($Name + ': every acquired process is disposed')
    Check ($script:fixtureProcess.WaitCalls -eq 0 -or ($script:fixtureProcess.WaitMilliseconds -gt 0 -and $script:fixtureProcess.WaitMilliseconds -le 1000)) ($Name + ': any exit grace period is bounded to one second')
    $results.Add([pscustomobject]@{
        name=$Name; expected=$Expected; actual=$actual; error=$caught
        processReads=$script:processReads; disposed=$script:fixtureProcess.DisposedCount
        hasExited=$script:fixtureProcess.HasExited; waitCalls=$script:fixtureProcess.WaitCalls
        waitMilliseconds=$script:fixtureProcess.WaitMilliseconds; cimReads=$script:cimReads; ownerReads=$script:ownerReads
    })
}

Invoke-FixtureCase 'live matching process' 'True' {}
Invoke-FixtureCase 'already exited process' 'False' { $script:fixtureProcess.HasExited=$true }
Invoke-FixtureCase 'process no longer exists' 'False' { $script:processMissing=$true }
Invoke-FixtureCase 'exit during CIM with missing executable path' 'False' { $script:exitDuringCim=$true; $script:fixtureIdentity.ExecutablePath=$null }
Invoke-FixtureCase 'exit during CIM with different executable path' 'False' { $script:exitDuringCim=$true; $script:fixtureIdentity.ExecutablePath=$script:otherPath }
Invoke-FixtureCase 'exit during CIM exception' 'False' { $script:exitDuringCim=$true; $script:cimMode='throw' }
Invoke-FixtureCase 'exit during CIM with matching path' 'False' { $script:exitDuringCim=$true }
Invoke-FixtureCase 'exit during missing CIM instance' 'False' { $script:exitDuringCim=$true; $script:cimMode='missing' }
Invoke-FixtureCase 'exit settles while waiting after missing path' 'False' { $script:fixtureProcess.ExitWhenWait=$true; $script:fixtureIdentity.ExecutablePath=$null }
Invoke-FixtureCase 'exit settles after start time query fails' 'False' { $script:fixtureProcess.ExitWhenWait=$true; $script:fixtureProcess.ThrowStartTime=$true }
Invoke-FixtureCase 'exit during CIM with missing start time' 'False' { $script:exitDuringCim=$true; $script:fixtureIdentity.CreationDate=$null }
Invoke-FixtureCase 'live missing executable path is rejected' 'error' { $script:fixtureIdentity.ExecutablePath=$null }
Invoke-FixtureCase 'live different executable path is rejected' 'error' { $script:fixtureIdentity.ExecutablePath=$script:otherPath }
Invoke-FixtureCase 'live CIM exception is rejected' 'error' { $script:cimMode='throw' }
Invoke-FixtureCase 'live missing CIM instance is rejected' 'error' { $script:cimMode='missing' }
Invoke-FixtureCase 'live start time query failure is rejected' 'error' { $script:fixtureProcess.ThrowStartTime=$true }
Invoke-FixtureCase 'live missing CIM start time is rejected' 'error' { $script:fixtureIdentity.CreationDate=$null }
Invoke-FixtureCase 'failed exit recheck preserves the CIM error' 'error' { $script:cimMode='throw'; $script:fixtureProcess.ThrowWait=$true } -ExpectedError 'Fixture CIM query failed.'
Invoke-FixtureCase 'PID reused before process query' 'False' { $script:fixtureProcess.FixedStartTime=$script:startedUtc.AddSeconds(1) }
Invoke-FixtureCase 'PID reused before CIM query' 'False' { $script:fixtureIdentity.CreationDate=$script:startedUtc.AddSeconds(1) }

# The same enumeration runs before shutdown and again before file replacement.
# Stale CIM snapshots after normal exit must not fail this final confirmation.
Invoke-FixtureCase 'enumeration retains live matching target' '1' {} -Enumerate
Invoke-FixtureCase 'enumeration skips an already exited target' '0' { $script:fixtureProcess.HasExited=$true } -Enumerate
Invoke-FixtureCase 'enumeration skips a disappeared target' '0' { $script:processMissing=$true } -Enumerate
Invoke-FixtureCase 'enumeration skips a reused process ID' '0' { $script:fixtureProcess.FixedStartTime=$script:startedUtc.AddSeconds(1) } -Enumerate
Invoke-FixtureCase 'enumeration ignores another installation' '0' { $script:fixtureIdentity.ExecutablePath=$script:otherPath } -Enumerate
Invoke-FixtureCase 'enumeration skips stale missing path after exit settles' '0' { $script:fixtureIdentity.ExecutablePath=$null; $script:fixtureProcess.ExitWhenWait=$true } -Enumerate
Invoke-FixtureCase 'enumeration skips exit during owner lookup' '0' { $script:exitDuringOwner=$true } -Enumerate
Invoke-FixtureCase 'enumeration skips owner lookup failure after exit' '0' { $script:exitDuringOwner=$true; $script:ownerMode='throw' } -Enumerate
Invoke-FixtureCase 'enumeration skips owner mismatch after exit' '0' { $script:exitDuringOwner=$true; $script:ownerMode='different' } -Enumerate
Invoke-FixtureCase 'enumeration rejects live missing path' 'error' { $script:fixtureIdentity.ExecutablePath=$null } -Enumerate
Invoke-FixtureCase 'enumeration rejects live owner lookup failure' 'error' { $script:ownerMode='throw' } -Enumerate
Invoke-FixtureCase 'enumeration rejects a different live owner' 'error' { $script:ownerMode='different' } -Enumerate
Invoke-FixtureCase 'enumeration rejects a different live session' 'error' { $script:fixtureIdentity.SessionId=$script:currentSession+1 } -Enumerate
Invoke-FixtureCase 'enumeration preserves owner error when exit recheck fails' 'error' { $script:ownerMode='throw'; $script:fixtureProcess.ThrowWait=$true } -Enumerate -ExpectedError 'Fixture owner query failed.'

# Exercise the final rescan loop itself. These local overrides return only
# in-memory snapshots and suppress result-file writes and all shutdown events.
$waitDefinition=@($definitions | Where-Object Name -eq 'Wait-ApplicationForUpdate')
if ($waitDefinition.Count -ne 1) { throw 'Expected exactly one update wait function.' }
foreach ($newTargetAlive in @($false,$true)) {
    $script:rescanReads=0; $script:rescanChecks=0; $script:rescanWrites=0
    $script:rescanNewTargetAlive=$newTargetAlive
    $script:rescanTarget=[pscustomobject]@{
        Id=$script:target.Id; StartedUtc=$(if ($newTargetAlive) { $script:startedUtc.AddSeconds(1) } else { $script:startedUtc })
        Executable=$script:expectedPath; Standalone=$false; CommandLine='fixture'
    }
    $rescan = & {
        . ([scriptblock]::Create($waitDefinition[0].Extent.Text))
        function Get-UpdateProcesses {
            param([string]$Executable)
            $script:rescanReads++
            if ($script:rescanReads -eq 1) { return $script:target }
            if ($script:rescanReads -eq 2) { return $script:rescanTarget }
            return @()
        }
        function Test-UpdateProcess {
            param([object]$Target)
            $script:rescanChecks++
            return ($script:rescanNewTargetAlive -and $Target.StartedUtc -ne $script:startedUtc)
        }
        function Write-UpdateResult { param([object]$Result,[string]$Path); $script:rescanWrites++ }
        try {
            $value=Wait-ApplicationForUpdate -Root 'C:\Minus700K-race-fixture' -OutputPath 'unused-in-memory-result' -CancellationPath '' -ParentId 0 -Seconds 1
            [pscustomobject]@{actual=[string]$value.passed; error=$null}
        } catch { [pscustomobject]@{actual='error'; error=$_.Exception.Message} }
    }
    $caseName=if ($newTargetAlive) { 'final rescan rejects a genuinely reused live PID' } else { 'final rescan skips the original PID that exited after enumeration' }
    $expected=if ($newTargetAlive) { 'error' } else { 'True' }
    Check ($rescan.actual -ceq $expected) ($caseName + ': expected ' + $expected + ', got ' + $rescan.actual)
    if ($newTargetAlive) { Check ($rescan.error -ceq 'An application process identifier was reused during update preparation. Please retry the update.') ($caseName + ': live PID reuse remains explicit') }
    $results.Add([pscustomobject]@{name=$caseName; expected=$expected; actual=$rescan.actual; error=$rescan.error; enumerationCalls=$script:rescanReads; identityChecks=$script:rescanChecks; resultWrites=$script:rescanWrites})
}

$failed=@($checks | Where-Object { -not $_.passed })
$report=[ordered]@{
    passed=($failed.Count -eq 0); helper=$helper; helperSha256=(Get-FileHash -LiteralPath $helper).Hash
    powershellVersion=$PSVersionTable.PSVersion.ToString(); is64BitProcess=[Environment]::Is64BitProcess
    caseCount=$results.Count; checkCount=$checks.Count; failedCheckCount=$failed.Count
    cases=@($results.ToArray()); checks=@($checks.ToArray()); failedChecks=$failed
}
$reportFile=[IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $reportFile) -Force | Out-Null
[IO.File]::WriteAllText($reportFile, ($report | ConvertTo-Json -Depth 7), (New-Object Text.UTF8Encoding($false)))
if ($failed.Count -gt 0) {
    foreach ($item in $failed) { Write-Output $item.description }
    throw "Update process race verification failed ($($failed.Count) checks). Report: $reportFile"
}
Write-Output "Update process race verification passed ($($checks.Count) checks / $($results.Count) cases; $([IntPtr]::Size * 8)-bit). Report: $reportFile"

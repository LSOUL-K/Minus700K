param(
    [Parameter(Mandatory = $true)][string]$ApplicationRoot,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [Parameter(Mandatory = $true)][string]$ErrorPath,
    [string]$CancelPath = '',
    [int]$InstallerProcessId = 0,
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'

function Same-UpdatePath([string]$Left, [string]$Right) {
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    return [string]::Equals([IO.Path]::GetFullPath($Left).TrimEnd('\'), [IO.Path]::GetFullPath($Right).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
}
function Get-UpdateProcesses([string]$Executable) {
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $currentSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
    foreach ($item in @(Get-CimInstance Win32_Process -Filter "Name='Minus700K.exe'")) {
        if (-not [string]::IsNullOrWhiteSpace([string]$item.ExecutablePath) -and -not (Same-UpdatePath $item.ExecutablePath $Executable)) { continue }
        $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            try {
                # CIM enumeration is a snapshot. A process can finish before
                # its path or owner is checked, including the final rescan.
                if ($process.HasExited) { continue }
                if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $item.CreationDate.ToUniversalTime()).TotalSeconds) -gt 0.01) { continue }
                if ([string]::IsNullOrWhiteSpace([string]$item.ExecutablePath)) {
                    throw 'The executable path of a running Minus700K process could not be verified. No application files were replaced.'
                }
                $owner = Invoke-CimMethod -InputObject $item -MethodName GetOwnerSid
                if ($owner.ReturnValue -ne 0 -or $owner.Sid -ne $currentSid -or $item.SessionId -ne $currentSession) {
                    throw 'The installation is in use by another Windows user or session. No application was stopped.'
                }
                if ($process.HasExited) { continue }
                $standalone = [string]$item.CommandLine -match '(?:^|\s)--(?:settings|sign-in)(?:\s|$)'
                [pscustomobject]@{ Id=[int]$item.ProcessId; StartedUtc=$item.CreationDate.ToUniversalTime(); Executable=$Executable; Standalone=$standalone; CommandLine=[string]$item.CommandLine }
            } catch {
                # Ignore missing identity data only after actual termination.
                # A live, unverified process still prevents file replacement.
                $identityFailure=$_
                try { if ($process.WaitForExit(1000)) { continue } } catch { }
                throw $identityFailure
            }
        } finally { $process.Dispose() }
    }
}
function Test-UpdateProcess([object]$Target) {
    $process = Get-Process -Id $Target.Id -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $false }
    try {
        try {
            if ($process.HasExited) { return $false }
            # Keep the identity tied to the original process, not merely a reused PID.
            if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $Target.StartedUtc).TotalSeconds) -gt 0.01) { return $false }
            # Inno Setup can launch 32-bit PowerShell on 64-bit Windows. Its
            # Get-Process.Path is then unavailable for a 64-bit application; CIM
            # supplies the executable path without that cross-bitness limitation.
            $identity=Get-CimInstance Win32_Process -Filter ('ProcessId=' + $Target.Id)
            if ($null -eq $identity) { throw 'The running process identity could not be verified while preparing the update.' }
            if ([Math]::Abs(($identity.CreationDate.ToUniversalTime() - $Target.StartedUtc).TotalSeconds) -gt 0.01) { return $false }
            if (-not (Same-UpdatePath $identity.ExecutablePath $Target.Executable)) { throw 'The process identity changed while preparing the update.' }
            return (-not $process.HasExited)
        } catch {
            # The cooperative shutdown can finish during the CIM query. Give
            # that exit a bounded grace period, rather than treating a vanished
            # path as a different executable. Never suppress a live mismatch.
            $identityFailure=$_
            try { if ($process.WaitForExit(1000)) { return $false } } catch { }
            throw $identityFailure
        }
    } finally { $process.Dispose() }
}
function Write-UpdateResult([object]$Result, [string]$Path) {
    [IO.File]::WriteAllText($Path, ($Result | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
    $running = if ($Result.wasRunning) { '1' } else { '0' }
    $stopped = if ($Result.stoppedProcesses.Count -gt 0) { '1' } else { '0' }
    $passed = if ($Result.passed) { '1' } else { '0' }
    [IO.File]::WriteAllText($Path + '.ini', "[update]`r`npassed=$passed`r`nwasRunning=$running`r`nstoppedAny=$stopped`r`nrestartArguments=$($Result.restartArguments)`r`n", [Text.Encoding]::ASCII)
}
function Wait-ApplicationForUpdate([string]$Root, [string]$OutputPath, [string]$CancellationPath, [int]$ParentId, [int]$Seconds) {
    $executable = [IO.Path]::GetFullPath((Join-Path $Root 'bin\Minus700K.exe'))
    $targets = @(Get-UpdateProcesses $executable)
    $result = [ordered]@{ passed=$false; wasRunning=($targets.Count -gt 0); restartArguments=''; stoppedProcesses=@(); error='' }
    if ($targets.Count -gt 0 -and @($targets | Where-Object { -not $_.Standalone }).Count -eq 0) { $result.restartArguments='--settings' }
    Write-UpdateResult $result $OutputPath
    $signals = @{}; $capabilityWait = @{}
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        while ($true) {
            if ($timer.Elapsed.TotalSeconds -ge $Seconds) { throw 'The application did not finish its work within the update wait period. No process was force-closed and no application files were replaced.' }
            if ($CancellationPath -and (Test-Path -LiteralPath $CancellationPath)) { throw 'Update preparation was cancelled. The application was not force-closed.' }
            if ($ParentId -gt 0 -and $null -eq (Get-Process -Id $ParentId -ErrorAction SilentlyContinue)) { throw 'The installer exited while waiting for the application.' }
            $remaining = @($targets | Where-Object { Test-UpdateProcess $_ })
            $result.stoppedProcesses = @($targets | Where-Object { $_.Id -notin @($remaining.Id) } | ForEach-Object { $_.Id })
            if ($remaining.Count -eq 0) {
                # A launch that raced the initial enumeration must also finish
                # before any installation file can be replaced.
                $newTargets = @(Get-UpdateProcesses $executable)
                if ($newTargets.Count -eq 0) { break }
                foreach ($target in $newTargets) {
                    if (-not (Test-UpdateProcess $target)) { continue }
                    if ($target.Id -in @($targets.Id)) { throw 'An application process identifier was reused during update preparation. Please retry the update.' }
                    $targets += $target; $result.wasRunning=$true; if (-not $target.Standalone) { $result.restartArguments='' }
                }
                continue
            }
            foreach ($target in $remaining) {
                if (-not $signals.ContainsKey($target.Id)) {
                    try { $signals[$target.Id] = [Threading.EventWaitHandle]::OpenExisting('Local\Minus700K_Update_7E940DB1_' + $target.Id) }
                    catch [Threading.WaitHandleCannotBeOpenedException] { }
                }
                if ($signals.ContainsKey($target.Id)) {
                    # A short-lived heartbeat lets the application resume normal
                    # input if this helper is cancelled or the installer closes.
                    [void]$signals[$target.Id].Set()
                    continue
                }
                # A newly launched process may not yet have created its event.
                # An older executable cannot atomically reserve an idle state:
                # status.json can say "ready" while connection work is active.
                # Never fall back to its unconditional Stop event or force kill.
                if (-not $capabilityWait.ContainsKey($target.Id)) { $capabilityWait[$target.Id]=$timer.ElapsedMilliseconds }
                if ($timer.ElapsedMilliseconds - $capabilityWait[$target.Id] -ge 5000) {
                    throw 'This installed version does not support safe automatic updating. Wait for its current work to finish, close Minus700K once, then retry. No application files were replaced.'
                }
            }
            Start-Sleep -Milliseconds 200
        }
        $result.passed=$true
        Write-UpdateResult $result $OutputPath
        return $result
    } catch {
        $result.error=$_.Exception.Message
        $result.stoppedProcesses = @($targets | Where-Object { try { -not (Test-UpdateProcess $_) } catch { $false } } | ForEach-Object { $_.Id })
        Write-UpdateResult $result $OutputPath
        throw
    } finally { foreach ($signal in $signals.Values) { $signal.Dispose() } }
}

if ($MyInvocation.InvocationName -ne '.') {
    try {
        $null = Wait-ApplicationForUpdate -Root $ApplicationRoot -OutputPath $ResultPath -CancellationPath $CancelPath -ParentId $InstallerProcessId -Seconds $TimeoutSeconds
        exit 0
    } catch {
        [IO.File]::WriteAllText($ErrorPath, $_.Exception.Message, (New-Object Text.UTF8Encoding($false)))
        exit 1
    }
}

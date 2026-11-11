param(
    [string]$Workspace = (Split-Path -Parent $PSScriptRoot),
    [Parameter(Mandatory=$true)][string]$PayloadRoot,
    [string]$CompilerPath = '',
    [string]$InstallerDefinition = ''
)
$ErrorActionPreference='Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-Workspace',$Workspace,'-PayloadRoot',$PayloadRoot)
    if ($CompilerPath) { $arguments+=@('-CompilerPath',$CompilerPath) }
    if ($InstallerDefinition) { $arguments+=@('-InstallerDefinition',$InstallerDefinition) }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Uninstall running-application verification failed in Windows PowerShell.' }
    return
}
if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot 'bin\Minus700K.exe'))) { throw 'PayloadRoot must contain a previously built installer payload.' }
if (-not $CompilerPath) { $CompilerPath=Join-Path $Workspace 'vendor\inno-setup-7.1.1\ISCC.exe' }
if (-not $InstallerDefinition) { $InstallerDefinition=Join-Path $Workspace 'installer\Minus700K.iss' }
$id=[Guid]::NewGuid().ToString('N').Substring(0,12)
$appId='Minus700K.Verification.Uninstall.'+$id
$residentMutex='Local\Minus700K_Verification_Uninstall_'+$id
$installingMutex='Local\Minus700K_Verification_UninstallGate_'+$id
$work=Join-Path $Workspace ('runtime\uninstall-running-test-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$id)
$installRoot=Join-Path ([IO.Path]::GetTempPath()) ('Minus700K-uninstall-test-'+$id)
$utf8=New-Object Text.UTF8Encoding($false)
$checks=New-Object Collections.Generic.List[string]
$fixtures=New-Object Collections.Generic.List[object]
$failure=$null; $cleanupFailure=$null; $installed=$false
New-Item -ItemType Directory -Path $work -Force | Out-Null
function Check([bool]$Value,[string]$Message) { if (-not $Value) { throw "Uninstall test failed: $Message" }; $checks.Add($Message) }
function Quote([string]$Value) { if ($Value.Contains('"')) { throw 'Unexpected quote in diagnostic argument.' }; return '"'+$Value+'"' }
function Read-Registration([string]$Identifier) {
    $snapshot=[ordered]@{}
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {
            $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$Identifier+'_is1')
            if (-not $key) { $snapshot[[string]$view]=$null; continue }
            try { $values=[ordered]@{}; foreach ($name in @($key.GetValueNames() | Sort-Object)) { $values[$name]=$key.GetValue($name) }; $snapshot[[string]$view]=$values }
            finally { $key.Dispose() }
        } finally { $base.Dispose() }
    }
    return ($snapshot | ConvertTo-Json -Depth 8 -Compress)
}
function Get-RegisteredRoot {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $base=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,$view)
        try {
            $key=$base.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\'+$appId+'_is1')
            if ($key) { try { return [string]$key.GetValue('InstallLocation') } finally { $key.Dispose() } }
        } finally { $base.Dispose() }
    }
    return $null
}
function Read-Shortcuts([string]$Group) {
    $path=Join-Path ([Environment]::GetFolderPath('Programs')) $Group
    $files=@()
    if (Test-Path -LiteralPath $path) { $files=@(Get-ChildItem -LiteralPath $path -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash }) }
    return ($files | ConvertTo-Json -Compress)
}
function Read-InstalledFiles {
    return (@(Get-ChildItem -LiteralPath $installRoot -Recurse -File | Sort-Object FullName | ForEach-Object { $_.FullName+':'+(Get-FileHash -LiteralPath $_.FullName).Hash }) | ConvertTo-Json -Compress)
}
function Wait-File([string]$Path) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $Path)) { if ($clock.ElapsedMilliseconds -gt 10000) { throw "Fixture did not write $Path" }; Start-Sleep -Milliseconds 30 }
}
function Test-StartupGate {
    try { $gate=[Threading.Mutex]::OpenExisting($installingMutex); try { return $true } finally { $gate.Dispose() } }
    catch [Threading.WaitHandleCannotBeOpenedException] { return $false }
}
function Start-Fixture([string]$Root,[string]$Mode,[switch]$Resident,[switch]$Gate) {
    $token=[Guid]::NewGuid().ToString('N')
    $arguments=@(('--fixture-token='+$token))
    if ($Mode) { $arguments+=$Mode }
    if ($Gate) { $arguments+=('--resident-mutex='+$installingMutex) }
    elseif ($Resident) { $arguments+=('--resident-mutex='+$residentMutex) }
    $exe=Join-Path $Root 'bin\Minus700K.exe'
    $process=Start-Process -FilePath $exe -ArgumentList ($arguments -join ' ') -WorkingDirectory $Root -WindowStyle Hidden -PassThru
    $null=$process.Handle
    $fixture=[pscustomobject]@{Root=$Root;Process=$process;Token=$token}; $fixtures.Add($fixture)
    Wait-File (Join-Path $Root ('fixture-'+$token+'-ready.json'))
    Check (-not $process.HasExited) "Disposable application process starts ($Mode)"
    return $fixture
}
function Stop-Fixture([object]$Fixture) {
    if (-not $Fixture.Process.HasExited) {
        [IO.File]::WriteAllText((Join-Path $Fixture.Root ('fixture-'+$Fixture.Token+'-exit')),'exit',$utf8)
        Check ($Fixture.Process.WaitForExit(5000)) 'Disposable application closes cooperatively'
    }
}
function Uninstall-Test([string]$LogName,[switch]$ExpectGateHeld) {
    $registered=Get-RegisteredRoot
    if (-not $registered -or [IO.Path]::GetFullPath($registered).TrimEnd('\') -ine [IO.Path]::GetFullPath($installRoot).TrimEnd('\') -or $appId -notlike 'Minus700K.Verification.Uninstall.*') { throw 'Test installation identity mismatch; refusing uninstall.' }
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG='+ (Quote (Join-Path $work $LogName))))
    $process=Start-Process -FilePath (Join-Path $installRoot 'unins000.exe') -ArgumentList ($arguments -join ' ') -WorkingDirectory $work -WindowStyle Hidden -PassThru
    $null=$process.Handle
    if (-not $process.WaitForExit(30000)) { throw 'Isolated uninstaller exceeded its bounded test wait.' }
    $process.WaitForExit()
    if ($ExpectGateHeld) {
        Check (Test-StartupGate) 'Uninstall leaves another process startup-prevention gate in place'
    } else {
        # Inno's original unins000.exe may exit just before its temporary child
        # finishes DeinitializeUninstall. Wait for that child's actual gate release.
        $gateWait=[Diagnostics.Stopwatch]::StartNew()
        while ((Test-StartupGate) -and $gateWait.ElapsedMilliseconds -lt 5000) { Start-Sleep -Milliseconds 20 }
        Check (-not (Test-StartupGate)) "Uninstall releases the startup-prevention gate after exit ($LogName)"
    }
    return $process.ExitCode
}
$productionRegistration=Read-Registration 'Minus700K'; $productionShortcuts=Read-Shortcuts 'Minus700K'
try {
    Check (-not (Get-RegisteredRoot)) 'The unique test registration is absent before testing'
    Check (-not (Test-StartupGate)) 'The unique startup-prevention gate is absent before testing'
    . (Join-Path $Workspace 'installer\prepare-dependencies.ps1') -ApplicationRoot $Workspace -ErrorPath (Join-Path $work 'unused-error.txt')
    if (-not (Test-VisualCppRuntime)) { throw 'The runtime must already be installed; this test does not install shared dependencies.' }
    . (Join-Path $Workspace 'install-codex.ps1') -ApplicationRoot $Workspace
    $codex=Find-CodexExecutable -Root $Workspace
    if (-not $codex) { throw 'Codex must already be installed; this test does not download it.' }
    Assert-CodexExecutable $codex
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $template=Join-Path $work 'Minus700K.exe'
    & $csc /nologo /target:winexe /platform:x64 /codepage:65001 "/out:$template" /r:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'UninstallRunningFixture.cs')
    Check ($LASTEXITCODE -eq 0) 'Disposable process fixture compiles'
    $payload=Join-Path $work 'payload'
    Copy-Item -LiteralPath $PayloadRoot -Destination $payload -Recurse
    Copy-Item -LiteralPath $template -Destination (Join-Path $payload 'bin\Minus700K.exe') -Force
    $output=Join-Path $work 'installer'
    & $CompilerPath '/Qp' "/DRepoRoot=$Workspace" "/DPayloadRoot=$payload" "/DInstallerAppId=$appId" "/DInstallerGroupName=$appId" "/DInstallerResidentMutex=$residentMutex" "/DInstallerInstallingMutex=$installingMutex" "/O$output" $InstallerDefinition *> (Join-Path $work 'compile.log')
    Check ($LASTEXITCODE -eq 0) 'Isolated Inno installer compiles'
    $arguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=english',('/DIR='+(Quote $installRoot)),('/LOG='+(Quote (Join-Path $work 'install.log'))))
    $process=Start-Process -FilePath (Join-Path $output 'Minus700K-Setup.exe') -ArgumentList ($arguments -join ' ') -WorkingDirectory $work -WindowStyle Hidden -Wait -PassThru
    $installed=[bool](Get-RegisteredRoot)
    Check ($process.ExitCode -eq 0 -and $installed) 'Fresh isolated installation succeeds'
    # App-created models are not part of the installer's ordinary file log.
    # Exercise the explicit uninstall cleanup with small, uniquely owned files.
    $modelPath=Join-Path $installRoot 'vendor\ggml-small-q5_1.bin'
    $customModelPath=Join-Path $installRoot 'vendor\user-custom-model.bin'
    $externalModelRoot=Join-Path $work 'external-model'
    New-Item -ItemType Directory -Path $externalModelRoot -Force | Out-Null
    $externalModelPath=Join-Path $externalModelRoot 'ggml-small-q5_1.bin'
    Check (-not (Test-Path -LiteralPath $modelPath)) 'Fresh installation does not bundle the first-launch model'
    [IO.File]::WriteAllText($modelPath,'Downloaded default model cleanup fixture.',$utf8)
    [IO.File]::WriteAllText($customModelPath,'User-provided model must remain.',$utf8)
    [IO.File]::WriteAllText($externalModelPath,'External configured model must remain.',$utf8)
    $modelHash=(Get-FileHash -LiteralPath $modelPath).Hash
    $modelTime=(Get-Item -LiteralPath $modelPath).LastWriteTimeUtc
    $customModelHash=(Get-FileHash -LiteralPath $customModelPath).Hash
    $externalModelHash=(Get-FileHash -LiteralPath $externalModelPath).Hash
    $configPath=Join-Path $installRoot 'config.json'
    $config=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $config | Add-Member -NotePropertyName WhisperModel -NotePropertyValue $externalModelPath -Force
    [IO.File]::WriteAllText($configPath,($config | ConvertTo-Json -Depth 20),$utf8)
    $upgradeArguments=@('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/LANG=english',('/DIR='+(Quote $installRoot)),('/LOG='+(Quote (Join-Path $work 'upgrade.log'))))
    $upgrade=Start-Process -FilePath (Join-Path $output 'Minus700K-Setup.exe') -ArgumentList ($upgradeArguments -join ' ') -WorkingDirectory $work -WindowStyle Hidden -Wait -PassThru
    Check ($upgrade.ExitCode -eq 0) 'Updating an installation with an app-downloaded model succeeds'
    Check ((Get-FileHash -LiteralPath $modelPath).Hash -ceq $modelHash -and (Get-Item -LiteralPath $modelPath).LastWriteTimeUtc -eq $modelTime) 'Update preserves the downloaded default model bytes and modification time'
    Check ((Get-FileHash -LiteralPath $customModelPath).Hash -ceq $customModelHash -and (Get-FileHash -LiteralPath $externalModelPath).Hash -ceq $externalModelHash) 'Update preserves custom and external model files'
    $updatedConfig=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($updatedConfig.WhisperModel -ceq $externalModelPath) 'Update preserves the configured external model path'
    $configHash=(Get-FileHash -LiteralPath $configPath).Hash
    foreach ($case in @(@{Name='resident';Mode='';Resident=$true},@{Name='settings';Mode='--settings';Resident=$false},@{Name='sign-in';Mode='--sign-in';Resident=$false})) {
        $fixture=Start-Fixture $installRoot $case.Mode -Resident:$case.Resident
        $filesBefore=Read-InstalledFiles; $registrationBefore=Read-Registration $appId; $shortcutsBefore=Read-Shortcuts $appId
        $exit=Uninstall-Test ('blocked-'+$case.Name+'.log')
        Check ($exit -ne 0) "Uninstall refuses a running application ($($case.Name), exit $exit)"
        Check (-not $fixture.Process.HasExited) "Uninstall does not terminate the running application ($($case.Name))"
        Check ((Read-InstalledFiles) -ceq $filesBefore) "Blocked uninstall preserves every installed file ($($case.Name))"
        Check ((Get-FileHash -LiteralPath $modelPath).Hash -ceq $modelHash -and (Get-FileHash -LiteralPath $externalModelPath).Hash -ceq $externalModelHash) "Blocked uninstall retains the default and external models ($($case.Name))"
        Check ((Read-Registration $appId) -ceq $registrationBefore) "Blocked uninstall preserves installation registration ($($case.Name))"
        Check ((Read-Shortcuts $appId) -ceq $shortcutsBefore) "Blocked uninstall preserves shortcuts ($($case.Name))"
        Stop-Fixture $fixture
    }
    $otherRoot=Join-Path $work 'other installation'
    New-Item -ItemType Directory -Path (Join-Path $otherRoot 'bin') -Force | Out-Null
    Copy-Item -LiteralPath $template -Destination (Join-Path $otherRoot 'bin\Minus700K.exe')
    $gateHolder=Start-Fixture $otherRoot '--settings' -Gate
    Check (Test-StartupGate) 'Another disposable process holds the startup-prevention gate'
    $filesBefore=Read-InstalledFiles; $registrationBefore=Read-Registration $appId; $shortcutsBefore=Read-Shortcuts $appId
    $exit=Uninstall-Test 'blocked-existing-gate.log' -ExpectGateHeld
    Check ($exit -ne 0 -and -not $gateHolder.Process.HasExited) 'Existing maintenance gate blocks uninstall without terminating its holder'
    Check ((Read-InstalledFiles) -ceq $filesBefore -and (Read-Registration $appId) -ceq $registrationBefore -and (Read-Shortcuts $appId) -ceq $shortcutsBefore) 'Existing-gate refusal preserves files, registration and shortcuts'
    Stop-Fixture $gateHolder
    Check (-not (Test-StartupGate)) 'The maintenance gate disappears when its actual holder exits'
    $other=Start-Fixture $otherRoot '--settings'
    $exit=Uninstall-Test 'stopped-uninstall.log'
    Check ($exit -eq 0) 'Uninstall succeeds after all processes from its own installation close'
    Check (-not (Get-RegisteredRoot) -and -not (Test-Path -LiteralPath (Join-Path $installRoot 'bin\Minus700K.exe'))) 'Successful uninstall removes application and registration'
    Check (-not $other.Process.HasExited) 'Same-name process in another folder does not block uninstall and remains running'
    Check (Test-Path -LiteralPath (Join-Path $installRoot 'config.json')) 'Successful uninstall retains user settings'
    Check (-not (Test-Path -LiteralPath $modelPath)) 'Successful uninstall removes the app-downloaded default model'
    Check ((Get-FileHash -LiteralPath $customModelPath).Hash -ceq $customModelHash) 'Successful uninstall preserves a custom model in the same vendor folder'
    Check ((Get-FileHash -LiteralPath $externalModelPath).Hash -ceq $externalModelHash) 'Successful uninstall preserves an external configured model with the same filename'
    Check ((Get-FileHash -LiteralPath $configPath).Hash -ceq $configHash) 'Successful uninstall preserves configuration bytes and the external model selection'
    $installed=$false
    Stop-Fixture $other
    Check ((Read-Registration 'Minus700K') -ceq $productionRegistration -and (Read-Shortcuts 'Minus700K') -ceq $productionShortcuts) 'Production registration and shortcuts remain unchanged'
} catch { $failure=$_.Exception.ToString() }
finally {
    try {
        foreach ($fixture in $fixtures) { Stop-Fixture $fixture }
        if (Get-RegisteredRoot) { $exit=Uninstall-Test 'cleanup-uninstall.log'; if ($exit -ne 0) { throw "Isolated uninstaller cleanup failed ($exit)." } }
        if ((Read-Registration 'Minus700K') -cne $productionRegistration -or (Read-Shortcuts 'Minus700K') -cne $productionShortcuts) { throw 'Production registration or shortcuts changed during testing.' }
    } catch { $cleanupFailure=$_.Exception.ToString() }
    $report=[ordered]@{passed=($null -eq $failure -and $null -eq $cleanupFailure);checks=@($checks.ToArray());appId=$appId;installingMutex=$installingMutex;fixtureDirectory=$installRoot;evidenceDirectory=$work;error=$failure;cleanupError=$cleanupFailure}
    $json=$report | ConvertTo-Json -Depth 7
    [IO.File]::WriteAllText((Join-Path $work 'report.json'),$json,$utf8)
    [IO.File]::WriteAllText((Join-Path $Workspace 'runtime\verify-uninstall-running.json'),$json,$utf8)
}
if ($failure -or $cleanupFailure) { throw "Uninstall verification failed. Evidence: $work`n$failure`n$cleanupFailure" }
Write-Output "Uninstall running-application verification passed ($($checks.Count) checks). Evidence: $work"

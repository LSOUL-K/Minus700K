param(
    [Parameter(Mandatory = $true)][string]$ApplicationRoot,
    [Parameter(Mandatory = $true)][string]$ErrorPath
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Test-VisualCppRuntime {
    # The bundled whisper.cpp executable was linked with MSVC 14.51. The
    # matching x64 runtime also supplies VCOMP140 for its CPU backends.
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $registry.OpenSubKey('SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64')
        if ($null -eq $key) { return $false }
        try {
            if ([int]$key.GetValue('Installed', 0) -ne 1) { return $false }
            $version = $null
            $text = ([string]$key.GetValue('Version', '')).TrimStart('v', 'V')
            if (-not [version]::TryParse($text, [ref]$version) -or $version -lt [version]'14.51.0.0') { return $false }
        } finally { $key.Dispose() }
    } finally { $registry.Dispose() }

    # A 32-bit PowerShell must bypass filesystem redirection when checking x64
    # DLLs; checking SysWOW64 would incorrectly accept only an x86 runtime.
    $systemDirectory = Join-Path $env:WINDIR $(if ([Environment]::Is64BitProcess) { 'System32' } else { 'Sysnative' })
    foreach ($name in @('MSVCP140.dll', 'VCRUNTIME140.dll', 'VCRUNTIME140_1.dll', 'VCOMP140.DLL')) {
        $path = Join-Path $systemDirectory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $false }
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        $fileVersion = New-Object Version($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
        if ($fileVersion -lt [version]'14.51.0.0') { return $false }
    }
    return $true
}

function Install-VisualCppRuntime {
    if (Test-VisualCppRuntime) { return 0 }
    Write-Host 'Installing the Microsoft Visual C++ x64 runtime. Windows may request administrator approval.'
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $directory = Join-Path $temporaryRoot ('Minus700K-vcredist-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    $installer = Join-Path $directory 'vc_redist.x64.exe'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://aka.ms/vc14/vc_redist.x64.exe' -OutFile $installer -TimeoutSec 180 | Out-Null
        $signature = Get-AuthenticodeSignature -LiteralPath $installer
        if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
            $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'Microsoft Corporation') {
            throw 'The Visual C++ installer does not have a valid Microsoft signature. No installer was run.'
        }
        $process = Start-Process -FilePath $installer -ArgumentList @('/install', '/passive', '/norestart') -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -in @(3010, 1641)) { return 3010 }
        if ($process.ExitCode -ne 0) { throw "The Microsoft Visual C++ installer exited with code $($process.ExitCode)." }
        if (-not (Test-VisualCppRuntime)) { throw 'The Microsoft Visual C++ x64 runtime is still missing or older than 14.51 after installation.' }
        return 0
    } finally {
        $resolvedDirectory = [IO.Path]::GetFullPath($directory)
        if ($resolvedDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $resolvedDirectory) -like 'Minus700K-vcredist-*') {
            Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $resolvedDirectory -Force -ErrorAction SilentlyContinue
        }
    }
}

function Invoke-InstallerDependencies {
    try {
        if (-not [Environment]::Is64BitOperatingSystem) { throw 'Minus700K requires a 64-bit version of Windows.' }
        $runtimeExitCode = Install-VisualCppRuntime
        if ($runtimeExitCode -eq 3010) { return 3010 }
        & (Join-Path $PSScriptRoot 'install-codex.ps1') -ApplicationRoot $ApplicationRoot | Out-Null
        return 0
    } catch {
        $message = $_.Exception.Message
        try { [IO.File]::WriteAllText($ErrorPath, $message, (New-Object Text.UTF8Encoding($false))) }
        catch { Write-Error "Could not write the installation error report: $($_.Exception.Message)" -ErrorAction Continue }
        Write-Error $message -ErrorAction Continue
        return 1
    }
}

if ($MyInvocation.InvocationName -ne '.') { exit (Invoke-InstallerDependencies) }

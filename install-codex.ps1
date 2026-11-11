param([string]$ApplicationRoot = $PSScriptRoot)
$ErrorActionPreference = 'Stop'

function Find-CodexExecutable {
    param(
        [string]$Configured,
        [string]$Root = $PSScriptRoot,
        [string]$PathValue = $env:PATH,
        [string]$LocalData = [Environment]::GetFolderPath('LocalApplicationData'),
        [string]$InstallDirectory = $env:CODEX_INSTALL_DIR
    )
    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        $candidate = if ([IO.Path]::IsPathRooted($Configured)) { $Configured } else { Join-Path $Root $Configured }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -or [IO.Path]::GetExtension($candidate) -ine '.exe') {
            throw "The CodexPath in config.json must point to an existing codex.exe: $candidate"
        }
        return [IO.Path]::GetFullPath($candidate)
    }
    foreach ($directory in ($PathValue -split ';')) {
        if ([string]::IsNullOrWhiteSpace($directory)) { continue }
        try {
            $candidate = Join-Path $directory.Trim().Trim('"') 'codex.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
        } catch { }
    }
    foreach ($directory in @($InstallDirectory, (Join-Path $LocalData 'Programs\OpenAI\Codex\bin'))) {
        if ([string]::IsNullOrWhiteSpace($directory)) { continue }
        $candidate = Join-Path $directory 'codex.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return [IO.Path]::GetFullPath($candidate) }
    }
    $desktopDirectory = Join-Path $LocalData 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $desktopDirectory -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $desktopDirectory -Filter codex.exe -File -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    return $null
}

function Assert-CodexInstallerSignature([string]$Installer) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Installer
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -ne 'OpenAI OpCo, LLC') {
        throw 'The Codex installer does not have a valid OpenAI signature. No installer was run.'
    }
}

function Invoke-CodexInstaller([string]$Installer) {
    # The installer runs separately so its exit and environment changes cannot
    # interrupt setup. It installs the complete official per-user runtime.
    $previous = @{}
    $overrides = @{ CODEX_NON_INTERACTIVE = '1'; CODEX_INSTALL_DAEMON_ONLY = $null; CODEX_INSTALL_DEFER_SELECTION = $null }
    foreach ($name in $overrides.Keys) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
    try {
        foreach ($name in $overrides.Keys) { [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process') }
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Installer | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "The official Codex installer exited with code $LASTEXITCODE." }
    } finally {
        foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    }
}

function Assert-CodexExecutable([string]$Executable) {
    $version = & $Executable --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($version -join ' ') -notmatch '\bcodex-cli\s+\S+') {
        throw "Codex CLI could not be started: $Executable"
    }
    Write-Host ($version -join [Environment]::NewLine)
}

function Install-CodexIfMissing([string]$Root) {
    $configured = ''
    $configPath = Join-Path $Root 'config.json'
    if (Test-Path -LiteralPath $configPath -PathType Leaf) {
        $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $configured = [string]$config.CodexPath
    }
    $executable = Find-CodexExecutable -Configured $configured -Root $Root
    if ($executable) {
        Assert-CodexExecutable $executable
        Write-Host "Using installed Codex CLI: $executable"
        return $executable
    }

    Write-Host 'Installing Codex CLI with the official OpenAI Windows installer...'
    $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Minus700K-codex-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    $installer = Join-Path $temporaryDirectory 'codex-install.ps1'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri 'https://chatgpt.com/codex/install.ps1' -OutFile $installer -TimeoutSec 120 | Out-Null
        Assert-CodexInstallerSignature $installer
        Invoke-CodexInstaller $installer
        # The parent shell still has its old PATH. Also check the installation
        # directories directly, as the application does on its first launch.
        $executable = Find-CodexExecutable -Root $Root
        if (-not $executable) { throw 'The installer finished, but codex.exe was not found.' }
        Assert-CodexExecutable $executable
        Write-Host "Installed Codex CLI: $executable"
        return $executable
    } catch {
        throw "Codex CLI setup failed: $($_.Exception.Message) Install the official Codex CLI and try the Minus700K installer again."
    } finally {
        Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $temporaryDirectory -Force -ErrorAction SilentlyContinue
    }
}

if ($MyInvocation.InvocationName -ne '.') { Install-CodexIfMissing -Root $ApplicationRoot }

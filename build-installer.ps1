param(
    [string]$OutputDirectory = 'dist',
    [string]$CompilerPath = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'Building the installer requires 64-bit Windows.'
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = $PSScriptRoot
$output = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }))
$vendor = Join-Path $root 'vendor'
$work = Join-Path $root ('runtime\installer-build-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$payload = Join-Path $work 'payload'
foreach ($directory in @($output,$vendor,$payload,(Join-Path $payload 'bin'),(Join-Path $payload 'licenses'))) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

function Get-VerifiedInstallerInput([string]$Url, [string]$Target, [string]$Hash) {
    if ((Test-Path -LiteralPath $Target -PathType Leaf) -and (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash -eq $Hash) { return }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Target) -Force | Out-Null
    $partial = $Target + '.' + [Guid]::NewGuid().ToString('N') + '.download'
    try {
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            try { Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial -TimeoutSec 600; break }
            catch { if ($attempt -eq 2) { throw }; Start-Sleep -Seconds (2 + 2 * $attempt) }
        }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $Hash) { throw "SHA256 mismatch: $Url" }
        Move-Item -LiteralPath $partial -Destination $Target -Force
    } finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
    }
}

function Test-InnoPublisher([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    return $signature.Status -eq 'Valid' -and $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -eq 'Pyrsys B.V.'
}

function Find-InnoCompiler([string]$Configured) {
    if (-not [string]::IsNullOrWhiteSpace($Configured)) {
        $candidate = if ([IO.Path]::IsPathRooted($Configured)) { $Configured } else { Join-Path $root $Configured }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Inno Setup compiler was not found: $candidate" }
        return [IO.Path]::GetFullPath($candidate)
    }
    $command = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($baseDirectory in @($env:ProgramFiles,${env:ProgramFiles(x86)},(Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))) {
        if ([string]::IsNullOrWhiteSpace($baseDirectory)) { continue }
        $candidate = Join-Path $baseDirectory 'Inno Setup 7\ISCC.exe'
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $cache = Join-Path $vendor 'inno-setup-7.1.1'
    $candidate = Join-Path $cache 'ISCC.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }

    # This immutable release is linked by https://jrsoftware.org/isdl.php.
    # Portable mode keeps the compiler and its configuration in the build cache.
    $compilerSetup = Join-Path $vendor 'innosetup-7.1.1-x64.exe'
    if (-not (Test-Path -LiteralPath $compilerSetup -PathType Leaf) -or -not (Test-InnoPublisher $compilerSetup)) {
        $partial = $compilerSetup + '.' + [Guid]::NewGuid().ToString('N') + '.download.exe'
        try {
            Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_1/innosetup-7.1.1-x64.exe' -OutFile $partial -TimeoutSec 600
            if (-not (Test-InnoPublisher $partial)) { throw 'The Inno Setup download does not have a valid Pyrsys B.V. signature. No compiler installer was run.' }
            Move-Item -LiteralPath $partial -Destination $compilerSetup -Force
        } finally {
            if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial -Force }
        }
    }
    Write-Host 'Preparing the portable Inno Setup 7.1.1 compiler...'
    $arguments = '/PORTABLE=1 /CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOICONS /DIR="' + $cache + '"'
    $process = Start-Process -FilePath $compilerSetup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Inno Setup compiler preparation failed (exit $($process.ExitCode))." }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Inno Setup did not create its compiler: $candidate" }
    return $candidate
}

Write-Host "Preparing installer payload: $payload"
# Do not copy a working installation wholesale. Only these distributable files
# and freshly verified upstream binaries enter the installer payload.
foreach ($name in @('config.example.json','README.md','README.en.md','LICENSE','THIRD-PARTY-NOTICES.md')) {
    $source = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing distribution file: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $payload $name)
}
$application = Join-Path $payload 'bin\Minus700K.exe'
if ($SkipBuild) {
    Copy-Item -LiteralPath (Join-Path $root 'bin\Minus700K.exe') -Destination $application
} else {
    & (Join-Path $root 'build.ps1') -OutputPath $application
}
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) { throw 'The application build did not produce Minus700K.exe.' }

$archive = Join-Path $vendor 'whisper-b5454-x64.zip'
Get-VerifiedInstallerInput 'https://github.com/ggml-org/whisper.cpp/releases/download/b5454/whisper-bin-x64.zip' $archive '6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb'
$extracted = Join-Path $work 'whisper-archive'
Expand-Archive -LiteralPath $archive -DestinationPath $extracted
$release = Join-Path $extracted 'Release'
$whisper = Join-Path $release 'whisper-cli.exe'
if (-not (Test-Path -LiteralPath $whisper -PathType Leaf)) { throw 'The verified whisper.cpp archive has an unexpected layout.' }
$dlls = @(Get-ChildItem -LiteralPath $release -Filter '*.dll' -File)
if ($dlls.Count -eq 0) { throw 'The verified whisper.cpp archive contains no runtime DLLs.' }
$whisperDestination = Join-Path $payload 'vendor\whisper\Release'
New-Item -ItemType Directory -Path $whisperDestination -Force | Out-Null
Copy-Item -LiteralPath $whisper -Destination $whisperDestination
foreach ($dll in $dlls) { Copy-Item -LiteralPath $dll.FullName -Destination $whisperDestination }

# Fixed upstream tags and hashes preserve the actual redistribution notices.
$licenses = @(
    @{ Name='whisper.cpp-LICENSE.txt'; Url='https://raw.githubusercontent.com/ggml-org/whisper.cpp/b5454/LICENSE'; Hash='94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d' },
    @{ Name='Whisper-LICENSE.txt'; Url='https://raw.githubusercontent.com/openai/whisper/v20250625/LICENSE'; Hash='b5d65a59060e68c4ff940e1eddfa6f94b2d68fdf58ed7f4dd57721c997e35e9d' },
    @{ Name='SDL2-LICENSE.txt'; Url='https://raw.githubusercontent.com/libsdl-org/SDL/release-2.28.5/LICENSE.txt'; Hash='9928507f684c1965d07f2b6ef4b4723d5efc2f6b4ab731f743a413c51c319927' },
    @{ Name='Inno-Setup-LICENSE.txt'; Url='https://raw.githubusercontent.com/jrsoftware/issrc/is-7_1_1/license.txt'; Hash='2e5346868c2a18434489824e11d65c3031620f792fefc415d05f19cd441abf5c' }
)
foreach ($license in $licenses) {
    $cachedLicense = Join-Path (Join-Path $vendor 'installer-licenses') $license.Name
    Get-VerifiedInstallerInput $license.Url $cachedLicense $license.Hash
    Copy-Item -LiteralPath $cachedLicense -Destination (Join-Path (Join-Path $payload 'licenses') $license.Name)
}

$manifest = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{ File=$_.FullName.Substring($payload.Length + 1).Replace('\','/'); Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$utf8 = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $work 'payload-manifest.json'), (ConvertTo-Json -InputObject $manifest -Depth 4), $utf8)
$compiler = Find-InnoCompiler $CompilerPath
$definition = Join-Path $root 'installer\Minus700K.iss'
if (-not (Test-Path -LiteralPath $definition -PathType Leaf)) { throw "Missing installer definition: $definition" }
Write-Host "Compiling installer with $compiler"
& $compiler "/DRepoRoot=$root" "/DPayloadRoot=$payload" "/O$output" $definition
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE). Payload retained at $payload" }
$installer = Join-Path $output 'Minus700K-Setup.exe'
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "The compiler did not produce $installer" }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($installer + '.sha256', "$hash  Minus700K-Setup.exe`r`n", $utf8)
Write-Output "Installer: $installer"
Write-Output "SHA256: $hash"
Write-Output "Payload manifest: $(Join-Path $work 'payload-manifest.json')"

param(
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [ValidateScript({
        $rogerParsedVersion = $null
        [Version]::TryParse($_, [ref]$rogerParsedVersion) -and
            $rogerParsedVersion.Major -lt 65535 -and $rogerParsedVersion.Minor -lt 65535 -and $rogerParsedVersion.Build -lt 65535
    })]
    [string]$Version = '0.3.0',
    [string]$CompilerPath,
    [switch]$BootstrapCompiler
)

$ErrorActionPreference = 'Stop'
$rogerRoot = Split-Path -Parent $PSScriptRoot
$rogerArtifacts = Join-Path $rogerRoot 'artifacts'
$rogerSource = Join-Path $rogerArtifacts 'Roger-win-x64'
$rogerCompilerDirectory = Join-Path $rogerRoot '.tools/inno-setup-6.7.4'
$rogerPinnedCompiler = Join-Path $rogerCompilerDirectory 'compiler/ISCC.exe'
$rogerPawnDirectory = Join-Path $rogerRoot '.tools/prerequisites/PawnIO-2.2.0'
$rogerPawnInstaller = Join-Path $rogerPawnDirectory 'PawnIO_setup.exe'
$rogerPawnSha256 = '1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032'

function Assert-RogerSignedFile([string]$Path, [string]$ExpectedHash, [string]$PublisherPattern) {
    if (-not [string]::Equals((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw "SHA-256 mismatch: $Path"
    }
    $rogerSignature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($rogerSignature.Status -ne 'Valid' -or $rogerSignature.SignerCertificate.Subject -notmatch $PublisherPattern) {
        throw "Windows signature or publisher validation failed: $Path"
    }
}

function Initialize-RogerCompiler {
    New-Item -ItemType Directory -Force -Path $rogerCompilerDirectory | Out-Null
    $rogerDownload = Join-Path $rogerCompilerDirectory 'innosetup-6.7.4.exe'
    $rogerExpectedCompilerHash = 'A549929319F9120716677C685535CA4671E4B937B5FDC87E2585F32D8EB30E8C'
    if (-not (Test-Path -LiteralPath $rogerDownload)) {
        Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' -OutFile $rogerDownload
    }
    Assert-RogerSignedFile $rogerDownload $rogerExpectedCompilerHash '(^|,\s*)O=Pyrsys B\.V\.(,|$)'
    if (-not (Test-Path -LiteralPath $rogerPinnedCompiler)) {
        $rogerPortablePath = Join-Path $rogerCompilerDirectory 'compiler'
        $rogerPortableLog = Join-Path $rogerCompilerDirectory 'portable-extract.log'
        # Official portable mode: workspace files only, no Add/Remove Programs entry,
        # no file associations, no Start menu/desktop links, and no automatic restart.
        $rogerProcess = Start-Process -FilePath $rogerDownload -ArgumentList @(
            '/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS',
            ('/DIR="' + $rogerPortablePath + '"'), ('/LOG="' + $rogerPortableLog + '"')
        ) -WindowStyle Hidden -PassThru -Wait
        if ($rogerProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $rogerPinnedCompiler)) {
            throw "Official portable compiler extraction failed (exit $($rogerProcess.ExitCode))."
        }
    }
    return $rogerPinnedCompiler
}

function Initialize-RogerPawnIo {
    New-Item -ItemType Directory -Force -Path $rogerPawnDirectory | Out-Null
    if (-not (Test-Path -LiteralPath $rogerPawnInstaller)) {
        $rogerReviewedCopy = Join-Path $rogerRoot '.tools/pawnio-review/PawnIO_setup.exe'
        if (Test-Path -LiteralPath $rogerReviewedCopy) {
            Copy-Item -LiteralPath $rogerReviewedCopy -Destination $rogerPawnInstaller
        }
        else {
            Invoke-WebRequest -Uri 'https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe' -OutFile $rogerPawnInstaller
        }
    }
    Assert-RogerSignedFile $rogerPawnInstaller $rogerPawnSha256 '(^|,\s*)CN=namazso\.eu(,|$)'
}

if ($BootstrapCompiler -and [string]::IsNullOrWhiteSpace($CompilerPath)) {
    $CompilerPath = Initialize-RogerCompiler
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $rogerCandidates = @(
        $rogerPinnedCompiler,
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    )
    $CompilerPath = $rogerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
        $rogerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($null -ne $rogerCommand) { $CompilerPath = $rogerCommand.Source }
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup compiler unavailable. Supply -CompilerPath or use -BootstrapCompiler for the verified official portable compiler.'
}
$CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
$rogerCompilerSignature = Get-AuthenticodeSignature -LiteralPath $CompilerPath
if ($rogerCompilerSignature.Status -ne 'Valid' -or $rogerCompilerSignature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Pyrsys B\.V\.(,|$)') {
    throw 'Use an official Windows-signed Inno Setup compiler (Pyrsys B.V., Inno Setup 6.6.0 or newer).'
}

$rogerRequiredFiles = @('Roger.exe', 'Roger.dll', 'Roger.runtimeconfig.json', 'Roger.deps.json', 'hostfxr.dll', 'coreclr.dll', 'PresentationFramework.dll', 'LICENSE', 'README.md', 'THIRD-PARTY-NOTICES.md', 'DEPENDENCIES.txt')
foreach ($rogerRequiredFile in $rogerRequiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $rogerSource $rogerRequiredFile) -PathType Leaf)) { throw "Portable publish is incomplete: missing $rogerRequiredFile" }
}
$rogerPublishedVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $rogerSource 'Roger.dll')).Version.ToString(3)
if ($rogerPublishedVersion -ne $Version) { throw "Portable version $rogerPublishedVersion does not match installer version $Version. Run publish.ps1 first." }
Initialize-RogerPawnIo

# If these files were added after publish, keep the packaged license set complete.
foreach ($rogerNoticeDirectory in @('PawnIO', 'InnoSetup')) {
    $rogerNoticeSource = Join-Path $rogerRoot ('third_party/' + $rogerNoticeDirectory)
    $rogerNoticeDestination = Join-Path $rogerSource ('third_party/' + $rogerNoticeDirectory)
    New-Item -ItemType Directory -Force -Path $rogerNoticeDestination | Out-Null
    Copy-Item -Path (Join-Path $rogerNoticeSource '*') -Destination $rogerNoticeDestination -Recurse -Force
}

& $CompilerPath '/Qp' ("/DAppVersion=$Version") ("/DAppSourceDir=$rogerSource") ("/DAppOutputDir=$rogerArtifacts") ("/DPawnIoInstaller=$rogerPawnInstaller") (Join-Path $rogerRoot 'installer/Roger.iss')
if ($LASTEXITCODE -ne 0) { throw 'Roger installer compilation failed.' }
$rogerSetup = Join-Path $rogerArtifacts "Roger-Setup-$Version-x64.exe"
if (-not (Test-Path -LiteralPath $rogerSetup)) { throw 'Installer compiler did not produce the expected EXE.' }
$rogerSetupVersion = (Get-Item -LiteralPath $rogerSetup).VersionInfo.ProductVersion.Trim().Trim([char]0).Trim()
if ($rogerSetupVersion -ne "$Version.0") { throw "Installer version metadata mismatch: $rogerSetupVersion" }
$rogerSetupHash = (Get-FileHash -LiteralPath $rogerSetup -Algorithm SHA256).Hash.ToLowerInvariant()
"$rogerSetupHash  Roger-Setup-$Version-x64.exe" | Set-Content -LiteralPath (Join-Path $rogerArtifacts "Roger-Setup-$Version-x64.sha256") -Encoding ascii
Write-Host "Installer: $rogerSetup"

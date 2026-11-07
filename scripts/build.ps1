param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [ValidateScript({
        $rogerParsedVersion = $null
        [Version]::TryParse($_, [ref]$rogerParsedVersion) -and
            $rogerParsedVersion.Major -lt 65535 -and $rogerParsedVersion.Minor -lt 65535 -and $rogerParsedVersion.Build -lt 65535
    })]
    [string]$Version = '0.1.0',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$rogerRoot = Split-Path -Parent $PSScriptRoot
$rogerLocalDotnet = Join-Path $rogerRoot '.tools/dotnet/dotnet.exe'
$rogerDotnet = if (Test-Path -LiteralPath $rogerLocalDotnet) { $rogerLocalDotnet } else { 'dotnet' }

Push-Location $rogerRoot
try {
    $rogerBuildProject = if ($SkipTests) { 'src/Roger/Roger.csproj' } else { 'tests/Roger.Smoke/Roger.Smoke.csproj' }
    # The smoke project references the application: one build gives both assemblies the same release version.
    & $rogerDotnet build $rogerBuildProject --configuration $Configuration "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Roger build failed.' }
    if (-not $SkipTests) {
        & $rogerDotnet run --project 'tests/Roger.Smoke/Roger.Smoke.csproj' --configuration $Configuration --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Roger smoke checks failed.' }
    }
}
finally {
    Pop-Location
}

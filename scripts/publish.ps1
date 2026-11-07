param(
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [ValidateScript({
        $rogerParsedVersion = $null
        [Version]::TryParse($_, [ref]$rogerParsedVersion) -and
            $rogerParsedVersion.Major -lt 65535 -and $rogerParsedVersion.Minor -lt 65535 -and $rogerParsedVersion.Build -lt 65535
    })]
    [string]$Version = '0.2.1'
)

$ErrorActionPreference = 'Stop'
$rogerRoot = Split-Path -Parent $PSScriptRoot
$rogerLocalDotnet = Join-Path $rogerRoot '.tools/dotnet/dotnet.exe'
$rogerDotnet = if (Test-Path -LiteralPath $rogerLocalDotnet) { $rogerLocalDotnet } else { 'dotnet' }
$rogerArtifacts = [IO.Path]::GetFullPath((Join-Path $rogerRoot 'artifacts'))
$rogerOutput = [IO.Path]::GetFullPath((Join-Path $rogerArtifacts 'Roger-win-x64'))
$rogerZip = Join-Path $rogerArtifacts 'Roger-win-x64.zip'

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration Release -Version $Version
if (-not $rogerOutput.StartsWith($rogerArtifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output must stay inside this workspace artifacts directory.'
}
New-Item -ItemType Directory -Force -Path $rogerArtifacts | Out-Null
if (Test-Path -LiteralPath $rogerOutput) {
    Remove-Item -LiteralPath $rogerOutput -Recurse -Force
}

Push-Location $rogerRoot
try {
    & $rogerDotnet publish 'src/Roger/Roger.csproj' --configuration Release --runtime win-x64 --self-contained true "-p:Version=$Version" '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' --output $rogerOutput
    if ($LASTEXITCODE -ne 0) { throw 'Roger publish failed.' }
    $rogerPublishedVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $rogerOutput 'Roger.dll')).Version.ToString(3)
    if ($rogerPublishedVersion -ne $Version) { throw "Published assembly version $rogerPublishedVersion does not match requested release $Version." }
    Copy-Item -LiteralPath (Join-Path $rogerRoot 'LICENSE'), (Join-Path $rogerRoot 'README.md'), (Join-Path $rogerRoot 'THIRD-PARTY-NOTICES.md') -Destination $rogerOutput
    Copy-Item -LiteralPath (Join-Path $rogerRoot 'third_party') -Destination (Join-Path $rogerOutput 'third_party') -Recurse
    # Keep original package metadata and supplied license/notice texts with the release.
    $rogerAssets = Get-Content -LiteralPath (Join-Path $rogerRoot 'src/Roger/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($rogerLibrary in $rogerAssets.libraries.PSObject.Properties) {
        if ($rogerLibrary.Value.type -ne 'package') { continue }
        foreach ($rogerPackageFolder in $rogerAssets.packageFolders.PSObject.Properties) {
            $rogerPackagePath = Join-Path $rogerPackageFolder.Name $rogerLibrary.Value.path
            if (-not (Test-Path -LiteralPath $rogerPackagePath)) { continue }
            $rogerNoticePath = Join-Path $rogerOutput ('third_party/packages/' + $rogerLibrary.Name)
            $rogerNoticeFiles = Get-ChildItem -LiteralPath $rogerPackagePath -File -Recurse | Where-Object {
                $_.Extension -eq '.nuspec' -or $_.Name -match '^(LICENSE|LICENCE|COPYING|NOTICE|THIRD[-_]PARTY)'
            }
            foreach ($rogerNoticeFile in $rogerNoticeFiles) {
                $rogerRelativeNotice = $rogerNoticeFile.FullName.Substring($rogerPackagePath.Length).TrimStart([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
                $rogerNoticeDestination = Join-Path $rogerNoticePath $rogerRelativeNotice
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $rogerNoticeDestination) | Out-Null
                Copy-Item -LiteralPath $rogerNoticeFile.FullName -Destination $rogerNoticeDestination
            }
            break
        }
    }
    $rogerPackageList = & $rogerDotnet list 'src/Roger/Roger.csproj' package --include-transitive --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Unable to write the dependency inventory.' }
    $rogerPackageList | Set-Content -LiteralPath (Join-Path $rogerOutput 'DEPENDENCIES.txt') -Encoding utf8
    Compress-Archive -Path (Join-Path $rogerOutput '*') -DestinationPath $rogerZip -Force
    $rogerHash = (Get-FileHash -LiteralPath $rogerZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$rogerHash  Roger-win-x64.zip" | Set-Content -LiteralPath (Join-Path $rogerArtifacts 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Portable build: $rogerZip"
}
finally {
    Pop-Location
}

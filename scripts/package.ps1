param(
    [ValidatePattern('\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z')]
    [string]$Version = '0.3.0',
    [string]$CompilerPath,
    [switch]$BootstrapCompiler
)

$ErrorActionPreference = 'Stop'
$rogerRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version
$rogerInstallerParameters = @{ Version = $Version }
if ($CompilerPath) { $rogerInstallerParameters.CompilerPath = $CompilerPath }
if ($BootstrapCompiler) { $rogerInstallerParameters.BootstrapCompiler = $true }
& (Join-Path $PSScriptRoot 'build-installer.ps1') @rogerInstallerParameters

$rogerArtifacts = Join-Path $rogerRoot 'artifacts'
$rogerNames = @('Roger-win-x64.zip', "Roger-Setup-$Version-x64.exe")
$rogerHashes = foreach ($rogerName in $rogerNames) {
    $rogerFile = Join-Path $rogerArtifacts $rogerName
    if (-not (Test-Path -LiteralPath $rogerFile -PathType Leaf)) { throw "Missing release package: $rogerName" }
    $rogerHash = (Get-FileHash -LiteralPath $rogerFile -Algorithm SHA256).Hash.ToLowerInvariant()
    "$rogerHash  $rogerName"
}
$rogerHashes | Set-Content -LiteralPath (Join-Path $rogerArtifacts 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Release packages and SHA256SUMS.txt are ready in $rogerArtifacts"

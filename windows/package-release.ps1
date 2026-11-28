#requires -Version 5.1
<#
.SYNOPSIS
Collects all release assets of one Missum version in artifacts\release\<Version>.

.DESCRIPTION
Run after windows\build.ps1 -Version <Version>. Produces:
  Missum-Setup-<Version>.exe            installer (app + AI stack sources, per user, no admin)
  Missum-<Version>-win-x64-portable.zip  portable app without installer
  Missum-AiStack-<x.y.z>.zip            AI stack sources (for manual or LAN setups)
  SHA256SUMS.txt                        checksums of the three files
Used locally and by .github\workflows\ci.yml / release.yml.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^$|^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string] $Version,

    [string] $InnoSetupCompiler,

    # CI without Inno Setup can still publish the portable and stack archives.
    [switch] $SkipInstaller
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Resolve-MissumRepositoryPath -RelativePath '.'
[xml] $props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
$defaultVersion = if ($node) { $node.InnerText.Trim() } else { '' }
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $defaultVersion }
$imageVersion = ($Version -split '-', 2)[0]
if ($imageVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version" }

$portableDirectory = Resolve-MissumRepositoryPath -RelativePath 'artifacts\portable\win-x64'
$portableManifest = $portableDirectory + '.manifest.json'
if (-not (Test-Path -LiteralPath $portableManifest -PathType Leaf)) { throw "Portable build missing. Run windows\build.ps1 -Version $Version first." }
$manifest = Get-Content -LiteralPath $portableManifest -Raw | ConvertFrom-Json
# A build without -Version carries the Directory.Build.props version; any mismatch means a stale build.
$builtVersion = if ($manifest.PSObject.Properties['version'] -and $manifest.version) { [string]$manifest.version } else { $defaultVersion }
if ($builtVersion -ne $Version) { throw "Portable build is version $builtVersion, expected $Version. Run windows\build.ps1 -Version $Version first." }

$releaseDirectory = Assert-MissumArtifactPath -Path (Join-Path $repositoryRoot ("artifacts\release\{0}" -f $Version))
if (Test-Path -LiteralPath $releaseDirectory) { Remove-Item -LiteralPath $releaseDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

& (Join-Path $PSScriptRoot 'package-ai-stack.ps1') -Version $imageVersion
$stackZip = Resolve-MissumRepositoryPath -RelativePath ("artifacts\aistack\Missum-AiStack-{0}.zip" -f $imageVersion)
Copy-Item -LiteralPath $stackZip -Destination $releaseDirectory

Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableZip = Join-Path $releaseDirectory ("Missum-{0}-win-x64-portable.zip" -f $Version)
[IO.Compression.ZipFile]::CreateFromDirectory($portableDirectory, $portableZip, [IO.Compression.CompressionLevel]::Optimal, $true)

if (-not $SkipInstaller) {
    $installerArguments = @{ Version = $Version }
    if ($InnoSetupCompiler) { $installerArguments.InnoSetupCompiler = $InnoSetupCompiler }
    & (Join-Path $PSScriptRoot 'build-installer.ps1') @installerArguments
    Copy-Item -LiteralPath (Resolve-MissumRepositoryPath -RelativePath ("artifacts\installer\Missum-Setup-{0}.exe" -f $Version)) -Destination $releaseDirectory
}

$sums = Get-ChildItem -LiteralPath $releaseDirectory -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name |
    ForEach-Object { '{0}  {1}' -f (Get-MissumFileSha256 -LiteralPath $_.FullName), $_.Name }
[IO.File]::WriteAllText((Join-Path $releaseDirectory 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))

Write-Host ("Release {0}: {1}" -f $Version, $releaseDirectory) -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseDirectory -File | ForEach-Object { Write-Host ("  {0} ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }

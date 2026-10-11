#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipPublish,

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $PortableOutputDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Resolve-MissumRepositoryPath -RelativePath 'src\Missum.App\Missum.App.csproj'
if ([string]::IsNullOrWhiteSpace($PortableOutputDirectory)) {
    $PortableOutputDirectory = Resolve-MissumRepositoryPath -RelativePath ("artifacts\portable\{0}" -f $RuntimeIdentifier)
}
else {
    $PortableOutputDirectory = Assert-MissumArtifactPath -Path $PortableOutputDirectory
}

Invoke-MissumDotNet -CommandArguments @(
    'restore', $appProject,
    '--runtime', $RuntimeIdentifier,
    ("-p:Configuration={0}" -f $Configuration),
    '-p:Platform=x64',
    '--nologo'
)
Invoke-MissumDotNet -CommandArguments @(
    'build', $appProject,
    '--configuration', $Configuration,
    '--no-restore',
    '-p:Platform=x64',
    ("-p:RuntimeIdentifier={0}" -f $RuntimeIdentifier),
    '--nologo'
)
if (-not $SkipPublish) {
    # Build and publish only. Validation belongs to the separate test scripts.
    & (Join-Path $PSScriptRoot 'publish.ps1') `
        -Mode SingleFile `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputDirectory $PortableOutputDirectory `
        -SkipSmoke

    $portableExecutable = Join-Path $PortableOutputDirectory 'Missum.exe'
    $portableManifest = Assert-MissumArtifactPath -Path ($PortableOutputDirectory + '.manifest.json')
    if (-not (Test-Path -LiteralPath $portableExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath $portableManifest -PathType Leaf)) {
        throw 'Portable build did not produce Missum.exe and its publish manifest.'
    }
    $manifest = Get-Content -LiteralPath $portableManifest -Raw | ConvertFrom-Json
    $actualExecutableHash = (Get-FileHash -LiteralPath $portableExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($manifest.mode -ne 'SingleFile' -or $manifest.runtimeIdentifier -ne $RuntimeIdentifier -or
        $manifest.executableSha256 -ne $actualExecutableHash) {
        throw 'Portable build result does not match its verified SingleFile manifest.'
    }
    Write-Host ("Portable Missum: {0}" -f $portableExecutable) -ForegroundColor Green
    Write-Host ("SHA-256: {0}" -f $actualExecutableHash) -ForegroundColor Green

}
Write-Host 'Missum build completed.' -ForegroundColor Green


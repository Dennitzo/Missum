#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $SkipTests,

    [switch] $SkipSmoke,

    [switch] $SkipPublish,

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $PortableOutputDirectory,

    [switch] $RunLiveToolAcceptance,

    [string] $AiServerUrl = 'http://127.0.0.1:8080',

    [string] $DeepSeekModelId,

    [string] $DeepSeekVisionModelId,

    [string] $ToolAcceptanceEvidenceDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

if ($RunLiveToolAcceptance -and $SkipPublish) {
    throw '-RunLiveToolAcceptance requires a freshly verified portable publish; remove -SkipPublish.'
}

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
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration
    # Includes DeepSeek integrated-vision routing and native projector/preset tests.
    & (Join-Path $PSScriptRoot 'test-agent-context.ps1') -Configuration $Configuration -SkipClientTests
}
if (-not $SkipPublish) {
    # Publish smoke verifies the bundled native catalog against current sources,
    # including DeepSeek vision support; stale runtime assets fail the build.
    & (Join-Path $PSScriptRoot 'publish.ps1') `
        -Mode SingleFile `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputDirectory $PortableOutputDirectory `
        -SkipSmoke:$SkipSmoke

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

    if ($RunLiveToolAcceptance) {
        $acceptanceArguments = @{
            Configuration = $Configuration
            ServerUrl = $AiServerUrl
            PortableExecutable = $portableExecutable
        }
        if (-not [string]::IsNullOrWhiteSpace($DeepSeekModelId)) {
            $acceptanceArguments['DeepSeekModelId'] = $DeepSeekModelId
        }
        if (-not [string]::IsNullOrWhiteSpace($DeepSeekVisionModelId)) {
            $acceptanceArguments['DeepSeekVisionModelId'] = $DeepSeekVisionModelId
        }
        if (-not [string]::IsNullOrWhiteSpace($ToolAcceptanceEvidenceDirectory)) {
            $acceptanceArguments['EvidenceDirectory'] = $ToolAcceptanceEvidenceDirectory
        }
        & (Join-Path $PSScriptRoot 'test-portable-tools.ps1') @acceptanceArguments
    }
}
Write-Host 'Missum build completed.' -ForegroundColor Green


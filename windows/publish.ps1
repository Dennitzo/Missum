#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Folder', 'SingleFile')]
    [string] $Mode = 'SingleFile',

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $OutputDirectory,

    [switch] $SkipSmoke
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$appProject = Resolve-MissumRepositoryPath -RelativePath 'src\Missum.App\Missum.App.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    if ($Mode -eq 'SingleFile') {
        $OutputDirectory = Resolve-MissumRepositoryPath -RelativePath ("artifacts\portable\{0}" -f $RuntimeIdentifier)
    }
    else {
        $OutputDirectory = Resolve-MissumRepositoryPath -RelativePath ("artifacts\windows\app-{0}-folder" -f $RuntimeIdentifier)
    }
}
$OutputDirectory = Reset-MissumArtifactDirectory -Path $OutputDirectory
$manifestPath = Assert-MissumArtifactPath -Path ($OutputDirectory + '.manifest.json')
if (Test-Path -LiteralPath $manifestPath) {
    Remove-Item -LiteralPath $manifestPath -Force
}

$configuration = if ($Mode -eq 'SingleFile') { 'Portable' } else { 'Release' }
$singleFile = if ($Mode -eq 'SingleFile') { 'true' } else { 'false' }
$extractAll = if ($Mode -eq 'SingleFile') { 'true' } else { 'false' }

Invoke-MissumDotNet -CommandArguments @(
    'restore', $appProject,
    '--runtime', $RuntimeIdentifier,
    ("-p:Configuration={0}" -f $configuration),
    '-p:Platform=x64',
    '-p:EnableMsixTooling=true',
    '--nologo'
)
Invoke-MissumDotNet -CommandArguments @(
    'publish', $appProject,
    '--configuration', $configuration,
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--no-restore',
    '--output', $OutputDirectory,
    '-p:Platform=x64',
    '-p:EnableMsixTooling=true',
    '-p:WindowsAppSDKSelfContained=true',
    ("-p:PublishSingleFile={0}" -f $singleFile),
    ("-p:IncludeAllContentForSelfExtract={0}" -f $extractAll),
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '--nologo'
)

$executable = Join-Path $OutputDirectory 'Missum.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published Missum executable is missing: $executable"
}

$manifest = [ordered]@{
    schema = 'missum.windows.publish.v1'
    mode = $Mode
    runtimeIdentifier = $RuntimeIdentifier
    selfContained = $true
    windowsAppSdkSelfContained = $true
    buildId = Get-MissumBuildId
    builtAtUtc = Get-MissumBuiltAt
    executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    files = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = ($_.FullName.Substring($OutputDirectory.TrimEnd('\', '/').Length).TrimStart('\', '/') -replace '\\', '/')
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

if (-not $SkipSmoke) {
    & (Join-Path $PSScriptRoot 'smoke.ps1') `
        -PublishDirectory $OutputDirectory `
        -ManifestPath $manifestPath `
        -Mode $Mode
}

Write-Host "Missum publish artifact: $OutputDirectory" -ForegroundColor Green


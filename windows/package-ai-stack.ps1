#requires -Version 5.1
<#
.SYNOPSIS
Packages the sources the local Missum AI stack is built from on a user's PC.

.DESCRIPTION
The installer ships these files to %LOCALAPPDATA%\Missum\AiStack\<version>; the app's in-app setup
then runs "docker compose build/up" there (no registry, no repository checkout, no .NET SDK on the
host). The repository layout is kept, so deploy\missum-ai\compose.yaml builds unchanged with its
contexts ../.. (gateway) and ../../workers (speech, media, image).

Only tracked files are packaged (git ls-files), so local secrets such as deploy\missum-ai\.env,
build output and caches never enter a release. Output:
  artifacts\aistack\<version>\                 the file tree
  artifacts\aistack\<version>.manifest.json    path, length and SHA-256 of every file
  artifacts\aistack\Missum-AiStack-<version>.zip
#>
[CmdletBinding()]
param(
    # Image/app version (x.y.z); default: Version from Directory.Build.props.
    [ValidatePattern('^$|^\d+\.\d+\.\d+$')]
    [string] $Version
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Resolve-MissumRepositoryPath -RelativePath '.'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    $Version = if ($node) { $node.InnerText.Trim() } else { '' }
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Directory.Build.props has no x.y.z Version: $Version" }
}

# Everything the compose build contexts reference, nothing else.
$sources = @(
    'Directory.Build.props',
    'Directory.Packages.props',
    '.dockerignore',
    'src/Missum.Ai.Contracts',
    'src/Missum.Ai.Server.Core',
    'src/Missum.Ai.Gateway',
    'workers/.dockerignore',
    'workers/common',
    'workers/speech',
    'workers/media',
    'workers/image',
    'deploy/missum-ai'
)

$aistackRoot = Assert-MissumArtifactPath -Path (Join-Path $repositoryRoot 'artifacts\aistack')
$outputDirectory = Assert-MissumArtifactPath -Path (Join-Path $aistackRoot $Version)
$manifestPath = Assert-MissumArtifactPath -Path ($outputDirectory + '.manifest.json')
$zipPath = Assert-MissumArtifactPath -Path (Join-Path $aistackRoot ("Missum-AiStack-{0}.zip" -f $Version))
foreach ($existing in @($outputDirectory, $manifestPath, $zipPath)) {
    if (Test-Path -LiteralPath $existing) { Remove-Item -LiteralPath $existing -Recurse -Force }
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$git = Get-Command git.exe -ErrorAction SilentlyContinue
if (-not $git) { throw 'git is required to package only tracked stack sources.' }
$files = @(& $git.Source -C $repositoryRoot ls-files -- @sources)
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'git ls-files returned no stack sources.' }
foreach ($required in @('deploy/missum-ai/compose.yaml', 'src/Missum.Ai.Gateway/Dockerfile', 'workers/speech/Dockerfile',
        'workers/media/Dockerfile', 'workers/image/Dockerfile', 'deploy/missum-ai/caddy/Caddyfile')) {
    if ($files -notcontains $required) { throw "Required stack source is not tracked: $required" }
}

$entries = foreach ($relative in ($files | Sort-Object)) {
    # Defense in depth: never package a local environment file or anything outside the tree.
    if ($relative -match '(^|/)\.env$' -or $relative -match '(^|/)(bin|obj)/') { continue }
    $source = Join-Path $repositoryRoot ($relative -replace '/', '\')
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    $target = Join-Path $outputDirectory ($relative -replace '/', '\')
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    [ordered]@{ path = $relative; length = (Get-Item -LiteralPath $target).Length; sha256 = Get-MissumFileSha256 -LiteralPath $target }
}
$null = Assert-MissumArtifactTree -Path $outputDirectory

$manifest = [ordered]@{
    schema = 'missum.aistack.sources.v1'
    version = $Version
    buildId = Get-MissumBuildId
    builtAtUtc = Get-MissumBuiltAt
    compose = 'deploy/missum-ai/compose.yaml'
    files = @($entries)
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $outputDirectory 'aistack.manifest.json')

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($outputDirectory, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host ("Missum AI stack sources {0}: {1} files" -f $Version, @($entries).Count) -ForegroundColor Green
Write-Host ("Folder: {0}" -f $outputDirectory) -ForegroundColor Green
Write-Host ("Zip:    {0}" -f $zipPath) -ForegroundColor Green

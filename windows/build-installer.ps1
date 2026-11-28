#requires -Version 5.1
<#
.SYNOPSIS
Compiles the Missum installer (Inno Setup 6) from the portable build and the AI stack sources.

.DESCRIPTION
Inputs: artifacts\portable\win-x64 (windows\build.ps1) and artifacts\aistack\<x.y.z>
(windows\package-ai-stack.ps1). Output: artifacts\installer\Missum-Setup-<Version>.exe.
Inno Setup is found via -InnoSetupCompiler, PATH (iscc) or its default install folders;
in CI it is installed with "choco install innosetup".
#>
[CmdletBinding()]
param(
    [ValidatePattern('^$|^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string] $Version,

    [string] $InnoSetupCompiler
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$repositoryRoot = Resolve-MissumRepositoryPath -RelativePath '.'
if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $node = $props.SelectSingleNode('/Project/PropertyGroup/Version')
    $Version = if ($node) { $node.InnerText.Trim() } else { '' }
}
# The image tag and the AiStack folder use the numeric part (the app's assembly version).
$imageVersion = ($Version -split '-', 2)[0]
if ($imageVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $Version" }

$appDirectory = Resolve-MissumRepositoryPath -RelativePath 'artifacts\portable\win-x64'
$stackDirectory = Resolve-MissumRepositoryPath -RelativePath ("artifacts\aistack\{0}" -f $imageVersion)
$outputDirectory = Assert-MissumArtifactPath -Path (Join-Path $repositoryRoot 'artifacts\installer')
if (-not (Test-Path -LiteralPath (Join-Path $appDirectory 'Missum Labs.exe') -PathType Leaf)) {
    throw "Portable build missing: $appDirectory. Run windows\build.ps1 -Version $Version first."
}
if (-not (Test-Path -LiteralPath (Join-Path $stackDirectory 'deploy\missum-ai\compose.yaml') -PathType Leaf)) {
    throw "AI stack sources missing: $stackDirectory. Run windows\package-ai-stack.ps1 -Version $imageVersion first."
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    $InnoSetupCompiler = @($candidates)[0]
}
if (-not $InnoSetupCompiler) { throw 'Inno Setup 6 (ISCC.exe) was not found. Install it (winget install JRSoftware.InnoSetup) or pass -InnoSetupCompiler.' }

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$script = Resolve-MissumRepositoryPath -RelativePath 'windows\installer\Missum.iss'
& $InnoSetupCompiler /Q `
    "/DAppVersion=$Version" "/DImageVersion=$imageVersion" `
    "/DAppDir=$appDirectory" "/DStackDir=$stackDirectory" "/DOutputDir=$outputDirectory" `
    $script
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }

$installer = Join-Path $outputDirectory ("Missum-Setup-{0}.exe" -f $Version)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw "Installer was not produced: $installer" }
Write-Host ("Installer: {0}" -f $installer) -ForegroundColor Green
Write-Host ("SHA-256:   {0}" -f (Get-MissumFileSha256 -LiteralPath $installer)) -ForegroundColor Green

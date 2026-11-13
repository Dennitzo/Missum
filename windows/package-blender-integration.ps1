#requires -Version 5.1
<#
    Builds the Blender program integration into its own artifact folder while a Missum instance may
    still be running. Use -Install to stage the signed extension for the next Missum start.

    .\windows\package-blender-integration.ps1
    .\windows\package-blender-integration.ps1 -PortableBuild
    .\windows\package-blender-integration.ps1 -Install -DataDirectory 'C:\Path\To\Missum\Data'

    Outputs (default under artifacts\blender-integration):
      adapter\                 published adapter used as package content
      staging\                 exact package content before packing
      keys\                    signing key (private) and trusted key (public)
      package\                 com.missum.blender-<version>.aext
      win-x64\                 separate portable Missum build (only with -PortableBuild)
#>
[CmdletBinding()]
param(
    [string] $RepositoryRoot,
    [string] $ArtifactDirectory,
    [string] $KeyId = 'missum-blender-2026',
    [string] $ExtensionVersion,
    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',
    [switch] $FrameworkDependent,
    [switch] $SkipAdapterPublish,
    [switch] $PortableBuild,
    [switch] $Install,
    [string] $DataDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string] $Message)
    Write-Host ('== {0}' -f $Message)
}

function Invoke-Checked {
    param(
        [string] $Description,
        [scriptblock] $Action
    )

    Write-Step $Description
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw ('{0} fehlgeschlagen (Exitcode {1}).' -f $Description, $LASTEXITCODE)
    }
}

function Get-Sha256 {
    param([string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path

if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $RepositoryRoot 'artifacts\blender-integration'
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)

# The instance that is currently in use must stay untouched, so refuse an artifact directory that
# overlaps with a running Missum installation.
$RunningPaths = @(
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -like 'Missum*' } |
        ForEach-Object { try { $_.Path } catch { $null } } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
foreach ($RunningPath in $RunningPaths) {
    $RunningDirectory = Split-Path -Parent $RunningPath
    $Candidate = $ArtifactDirectory.TrimEnd('\')
    if ($RunningDirectory.TrimEnd('\') -eq $Candidate -or $Candidate.StartsWith(($RunningDirectory.TrimEnd('\') + '\'),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Artefaktordner ueberschneidet die laufende Instanz unter {0}. Andere -ArtifactDirectory waehlen.' -f $RunningDirectory)
    }
}

$AdapterProject = Join-Path $RepositoryRoot 'src\Missum.BlenderAdapter\Missum.BlenderAdapter.csproj'
$PackagerProject = Join-Path $RepositoryRoot 'tools\Missum.ExtensionPackager\Missum.ExtensionPackager.csproj'
$ManifestFile = Join-Path $RepositoryRoot 'extensions\blender\manifest\extension.json'
$RuntimeSource = Join-Path $RepositoryRoot 'extensions\blender\runtime'
$RuntimeFiles = @('bootstrap.py', 'scene_ops.py')

foreach ($Required in @($AdapterProject, $PackagerProject, $ManifestFile) +
        @($RuntimeFiles | ForEach-Object { Join-Path $RuntimeSource $_ })) {
    if (-not (Test-Path -LiteralPath $Required -PathType Leaf)) {
        throw ('Erwartete Datei fehlt: {0}' -f $Required)
    }
}

$ManifestVersion = (Get-Content -LiteralPath $ManifestFile -Raw | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($ExtensionVersion)) {
    $ExtensionVersion = $ManifestVersion
}
elseif ($ExtensionVersion -ne $ManifestVersion) {
    throw ('-ExtensionVersion muss der Manifest-Version {0} entsprechen.' -f $ManifestVersion)
}

$AdapterOutput = Join-Path $ArtifactDirectory 'adapter'
$StagingDirectory = Join-Path $ArtifactDirectory 'staging'
$KeysDirectory = Join-Path $ArtifactDirectory 'keys'
$PackageDirectory = Join-Path $ArtifactDirectory 'package'
$PackageFile = Join-Path $PackageDirectory ('com.missum.blender-{0}.aext' -f $ExtensionVersion)
$PortableOutput = Join-Path $ArtifactDirectory 'win-x64'

New-Item -ItemType Directory -Force -Path $ArtifactDirectory, $KeysDirectory, $PackageDirectory |
    Out-Null

if ($SkipAdapterPublish -and -not (Test-Path -LiteralPath (Join-Path $AdapterOutput 'Missum.BlenderAdapter.exe'))) {
    throw ('-SkipAdapterPublish gesetzt, aber der Adapter liegt nicht unter {0}.' -f $AdapterOutput)
}

if ($SkipAdapterPublish) {
    Write-Step 'Adapter veroeffentlichen (uebersprungen)'
}
else {
Invoke-Checked 'Adapter veroeffentlichen' {
    if (Test-Path -LiteralPath $AdapterOutput) {
        Remove-Item -LiteralPath $AdapterOutput -Recurse -Force
    }
    $PublishArguments = @(
        'publish', $AdapterProject,
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--nologo', '-v', 'quiet',
        ('--output', $AdapterOutput)
    )
    if ($FrameworkDependent) {
        $PublishArguments += '--self-contained', 'false'
    }
    else {
        $PublishArguments += '--self-contained', 'true', '-p:PublishSingleFile=false'
    }
    dotnet @PublishArguments
}
}

Write-Step ' Paketinhalt zusammenstellen'
if (Test-Path -LiteralPath $StagingDirectory) {
    Remove-Item -LiteralPath $StagingDirectory -Recurse -Force
}
$StagedAdapter = Join-Path $StagingDirectory 'adapter'
$StagedRuntime = Join-Path $StagingDirectory 'runtime'
New-Item -ItemType Directory -Force -Path $StagedAdapter, $StagedRuntime | Out-Null
# -LiteralPath does not expand wildcards, so the files are walked explicitly. Debug symbols stay out
# of a distributed package.
Get-ChildItem -LiteralPath $AdapterOutput -File -Recurse |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object {
        $Relative = $_.FullName.Substring($AdapterOutput.TrimEnd('\').Length + 1)
        $Target = Join-Path $StagedAdapter $Relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $Target -Force
    }
foreach ($RuntimeFile in $RuntimeFiles) {
    Copy-Item -LiteralPath (Join-Path $RuntimeSource $RuntimeFile) -Destination $StagedRuntime -Force
}

if (-not (Test-Path -LiteralPath (Join-Path $StagedAdapter 'Missum.BlenderAdapter.exe') -PathType Leaf)) {
    throw 'Das Adapter-Programm fehlt im Paketinhalt.'
}

Invoke-Checked 'Packager bauen' {
    dotnet build $PackagerProject --configuration Release --nologo -v quiet
}

Invoke-Checked 'Signierschluessel bereitstellen' {
    dotnet run --project $PackagerProject --configuration Release --no-build -- key `
        --keys $KeysDirectory --key-id $KeyId
}

Invoke-Checked '.aext-Paket bauen' {
    dotnet run --project $PackagerProject --configuration Release -- pack `
        --manifest $ManifestFile `
        --content $StagingDirectory `
        --out $PackageFile `
        --private-key (Join-Path $KeysDirectory ($KeyId + '.private.pem')) `
        --key-id $KeyId
}

Invoke-Checked '.aext-Paket mit Produktregeln pruefen' {
    dotnet run --project $PackagerProject --configuration Release -- verify `
        --package $PackageFile --trusted-keys $KeysDirectory
}

if ($PortableBuild) {
    Write-Step ' Separaten Portable-Build von Missum erstellen'
    & (Join-Path $RepositoryRoot 'windows\build.ps1') -PortableOutputDirectory $PortableOutput
    if ($LASTEXITCODE -ne 0) { throw 'Portable-Build fehlgeschlagen.' }
}

if ($Install) {
    if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
        throw 'Fuer -Install ist -DataDirectory erforderlich (Datenverzeichnis der neuen Version).'
    }
    $ExtensionsRoot = Join-Path $DataDirectory 'Extensions'
    $TrustedKeys = Join-Path $ExtensionsRoot 'trusted-keys'
    # A package placed in pending is validated and activated by the app on its next start.
    $PendingDirectory = Join-Path $ExtensionsRoot ('pending\com.missum.blender\{0}' -f $ExtensionVersion)
    New-Item -ItemType Directory -Force -Path $PendingDirectory, $TrustedKeys | Out-Null
    Copy-Item -LiteralPath $PackageFile `
        -Destination (Join-Path $PendingDirectory ((Get-Sha256 $PackageFile) + '.aext')) -Force
    Copy-Item -LiteralPath (Join-Path $KeysDirectory ($KeyId + '.json')) -Destination $TrustedKeys -Force
    Write-Host ('Paket vorgemerkt: {0}' -f $PendingDirectory)
    Write-Host ('Vertrauensschluessel: {0}' -f (Join-Path $TrustedKeys ($KeyId + '.json')))
}

Write-Host ''
Write-Host 'Fertig. Erzeugte Pfade:'
Write-Host ('  Adapter           : {0}' -f $AdapterOutput)
Write-Host ('  Paketinhalt       : {0}' -f $StagingDirectory)
Write-Host ('  Schluessel        : {0}' -f $KeysDirectory)
Write-Host ('  Erweiterungspaket : {0}' -f $PackageFile)
if ($PortableBuild) {
    Write-Host ('  Portable-Build    : {0}' -f $PortableOutput)
}
Write-Host ''
Write-Host 'Paketinhalt umfasst den Adapter und die Startskripte unter runtime.'

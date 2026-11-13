#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $ArtifactDirectory,
    [switch] $PortableBuild,
    [switch] $Install,
    [string] $DataDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'common.ps1')
if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    $ArtifactDirectory = Join-Path $repository 'artifacts\unreal-integration'
}
$ArtifactDirectory = Assert-MissumArtifactTree -Path $ArtifactDirectory
$buildId = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $ArtifactDirectory ('staging-' + $buildId)
$adapter = Join-Path $stage 'adapter'
$runtime = Join-Path $stage 'runtime'
$keys = Join-Path $ArtifactDirectory 'keys'
$packages = Join-Path $ArtifactDirectory 'package'
$manifest = Join-Path $repository 'extensions\unreal\manifest\extension.json'
$plugin = Join-Path $repository 'extensions\unreal\editor\MissumBridge'
$adapterProject = Join-Path $repository 'src\Missum.UnrealAdapter\Missum.UnrealAdapter.csproj'
$packager = Join-Path $repository 'tools\Missum.ExtensionPackager\Missum.ExtensionPackager.csproj'
$keyId = 'missum-unreal-2026'
$version = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).version
$package = Join-Path $packages ("com.missum.unreal-{0}.aext" -f $version)
New-Item -ItemType Directory -Force -Path $adapter, $runtime, $keys, $packages | Out-Null

dotnet publish $adapterProject --configuration Release --runtime win-x64 --self-contained true --output $adapter --nologo
if ($LASTEXITCODE -ne 0) { throw 'Unreal-Adapter konnte nicht veröffentlicht werden.' }
Copy-Item -LiteralPath $plugin -Destination $runtime -Recurse
dotnet build $packager --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Extension-Packager konnte nicht gebaut werden.' }
dotnet run --project $packager --configuration Release --no-build -- key --keys $keys --key-id $keyId
if ($LASTEXITCODE -ne 0) { throw 'Signierschlüssel konnte nicht bereitgestellt werden.' }
dotnet run --project $packager --configuration Release --no-build -- pack --manifest $manifest --content $stage --out $package --private-key (Join-Path $keys ($keyId + '.private.pem')) --key-id $keyId
if ($LASTEXITCODE -ne 0) { throw 'Unreal-Paket konnte nicht signiert werden.' }
dotnet run --project $packager --configuration Release --no-build -- verify --package $package --trusted-keys $keys
if ($LASTEXITCODE -ne 0) { throw 'Unreal-Paketprüfung ist fehlgeschlagen.' }

if ($PortableBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -PortableOutputDirectory (Join-Path $ArtifactDirectory 'win-x64')
}
if ($Install) {
    if ([string]::IsNullOrWhiteSpace($DataDirectory)) { throw '-Install benötigt -DataDirectory.' }
    $extensions = Join-Path ([IO.Path]::GetFullPath($DataDirectory)) 'Extensions'
    $pending = Join-Path $extensions ("pending\com.missum.unreal\{0}" -f $version)
    $trusted = Join-Path $extensions 'trusted-keys'
    New-Item -ItemType Directory -Force -Path $pending, $trusted | Out-Null
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-Item -LiteralPath $package -Destination (Join-Path $pending ($hash + '.aext')) -Force
    Copy-Item -LiteralPath (Join-Path $keys ($keyId + '.json')) -Destination $trusted -Force
}
Write-Host ("Unreal-Erweiterungspaket: {0}" -f $package)

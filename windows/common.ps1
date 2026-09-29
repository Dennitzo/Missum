#requires -Version 5.1
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:MissumRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Get-MissumRepositoryRoot {
    return $script:MissumRepositoryRoot
}

function Resolve-MissumRepositoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $RelativePath
    )

    return [System.IO.Path]::GetFullPath((Join-Path $script:MissumRepositoryRoot $RelativePath))
}

function Assert-MissumCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command '$Name' was not found in PATH."
    }

    return $command
}

function Resolve-MissumDockerCommand {
    $dockerBin = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'Docker\Docker\resources\bin'
    if (Test-Path -LiteralPath $dockerBin -PathType Container) {
        $pathEntries = @($env:PATH -split ';')
        if (-not ($pathEntries | Where-Object { [string]::Equals($_.TrimEnd('\'), $dockerBin.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) })) {
            $env:PATH = $dockerBin + ';' + $env:PATH
        }
    }

    $command = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidate = Join-Path $dockerBin 'docker.exe'
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        return $candidate
    }

    throw 'Docker Desktop command was not found. Install or start Docker Desktop.'
}

function Read-MissumAiStackEnvironment {
    param([Parameter(Mandatory = $true)][string] $Path)
    $values = @{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        foreach ($line in [IO.File]::ReadAllLines($Path)) {
            if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') { $values[$Matches[1]] = $Matches[2].Trim().Trim('"').Trim("'") }
        }
    }
    return $values
}

function Get-MissumAiStackDefaults {
    param(
        [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
        [string] $ModelRoot,
        [string] $NativeModelRoot
    )

    $resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
    $configured = Read-MissumAiStackEnvironment -Path (Join-Path $resolvedDataRoot 'stack.env')
    if ([string]::IsNullOrWhiteSpace($ModelRoot)) { $ModelRoot = $configured['MISSUM_AI_MODEL_ROOT'] }
    if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { $NativeModelRoot = $configured['MISSUM_AI_NATIVE_MODEL_ROOT'] }
    $resolvedModelRoot = if ([string]::IsNullOrWhiteSpace($ModelRoot)) {
        Join-Path $resolvedDataRoot 'Models'
    }
    else {
        [IO.Path]::GetFullPath($ModelRoot)
    }
    $resolvedNativeModelRoot = if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) {
        Join-Path $env:USERPROFILE '.cache\huggingface\hub'
    }
    else {
        [IO.Path]::GetFullPath($NativeModelRoot)
    }
    return [pscustomobject]@{
        DataRoot = $resolvedDataRoot
        ModelRoot = $resolvedModelRoot
        NativeModelRoot = [IO.Path]::GetFullPath($resolvedNativeModelRoot)
        ComposeFile = Resolve-MissumRepositoryPath -RelativePath 'deploy\missum-ai\compose.yaml'
        EnvironmentFile = Join-Path $resolvedDataRoot 'stack.env'
    }
}

function Write-MissumAiStackEnvironment {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [string] $ServerIp = '192.168.0.67',
        [string] $ImageVersion = '2.0.0',
        [Alias('CodingModelRoot')][string] $NativeModelRoot,
        [string] $NativeBinaryPath,
        [string] $NativeStateDirectory
    )
    if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { $NativeModelRoot = $Paths.NativeModelRoot }
    New-Item -ItemType Directory -Path $Paths.DataRoot -Force | Out-Null
    $values = Read-MissumAiStackEnvironment -Path $Paths.EnvironmentFile
    $values['MISSUM_AI_DATA_ROOT'] = $Paths.DataRoot -replace '\\','/'
    $values['MISSUM_AI_MODEL_ROOT'] = $Paths.ModelRoot -replace '\\','/'
    $values['MISSUM_AI_NATIVE_MODEL_ROOT'] = $NativeModelRoot -replace '\\','/'
    $values['MISSUM_AI_CODING_MODEL_ROOT'] = $NativeModelRoot -replace '\\','/'
    if (-not $values.ContainsKey('MISSUM_AI_MODEL_RUNTIME_URL')) { $values['MISSUM_AI_MODEL_RUNTIME_URL'] = 'http://host.docker.internal:8081' }
    $values['MISSUM_AI_EXPECTED_LAN_IP'] = $ServerIp
    $values['MISSUM_AI_PUBLIC_URL'] = "http://${ServerIp}:8080"
    $values['MISSUM_AI_IMAGE_VERSION'] = $ImageVersion
    if ($NativeBinaryPath) { $values['MISSUM_AI_NATIVE_BINARY_PATH'] = [IO.Path]::GetFullPath($NativeBinaryPath) -replace '\\','/' }
    if ($NativeStateDirectory) { $values['MISSUM_AI_NATIVE_STATE_ROOT'] = [IO.Path]::GetFullPath($NativeStateDirectory) -replace '\\','/' }
    $content = ($values.Keys | Sort-Object | ForEach-Object { "$_=$($values[$_])" }) -join "`n"
    [IO.File]::WriteAllText($Paths.EnvironmentFile, $content + "`n", [Text.UTF8Encoding]::new($false))
}

function Copy-MissumSqliteSnapshot {
    param([Parameter(Mandatory = $true)][string] $Source,
        [Parameter(Mandatory = $true)][string] $Destination,
        [string] $PythonPath)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) { throw "Source database does not exist: $Source" }
    if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = (Assert-MissumCommand -Name 'python.exe').Source }
    # SQLite's backup API includes committed WAL pages without checkpointing or changing the source.
    $script = @'
import hashlib, json, sqlite3, sys, uuid
from pathlib import Path
source, destination = map(Path, sys.argv[1:3])
temporary = destination.with_name(destination.name + '.' + uuid.uuid4().hex + '.migration')
def check(connection):
    results = [row[0] for row in connection.execute('PRAGMA integrity_check')]
    if results != ['ok']: raise RuntimeError('SQLite integrity check failed: ' + repr(results))
def logical_hash(connection):
    digest = hashlib.sha256()
    for line in connection.iterdump(): digest.update(line.encode('utf-8') + b'\n')
    return digest.hexdigest()
try:
    with sqlite3.connect(source.resolve().as_uri() + '?mode=ro', uri=True, timeout=30) as src:
        src.execute('PRAGMA query_only=ON')
        check(src)
        dst = sqlite3.connect(str(temporary))
        try:
            src.backup(dst)
            dst.execute('PRAGMA journal_mode=DELETE')
            check(dst)
            tables = [row[0] for row in dst.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")]
            counts = {table: dst.execute('SELECT COUNT(*) FROM "' + table.replace('"', '""') + '"').fetchone()[0] for table in tables}
            digest = logical_hash(dst)
        finally: dst.close()
    if destination.exists():
        with sqlite3.connect(destination.resolve().as_uri() + '?mode=ro', uri=True) as existing:
            check(existing)
            if logical_hash(existing) != digest: raise RuntimeError('Destination database differs; refusing to overwrite it: ' + str(destination))
        temporary.unlink()
    else: temporary.rename(destination)
    print(json.dumps({'source':str(source), 'destination':str(destination), 'logicalSha256':digest, 'tableCounts':counts}))
finally:
    if temporary.exists(): temporary.unlink()
'@
    $output = $script | & $PythonPath - $Source $Destination
    if ($LASTEXITCODE -ne 0) { throw "Consistent SQLite snapshot failed: $Source" }
    return ($output | ConvertFrom-Json)
}

function Resolve-MissumNativeModelFile {
    param([Parameter(Mandatory = $true)][string] $NativeModelRoot,
        [Parameter(Mandatory = $true)][string] $Repository,
        [Parameter(Mandatory = $true)][string] $Revision,
        [Parameter(Mandatory = $true)][string] $FileName)
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Revision -notmatch '^[a-fA-F0-9]{40}$') {
        throw 'A native model cache entry requires a pinned Hugging Face repository and revision.'
    }
    $root = [IO.Path]::GetFullPath($NativeModelRoot).TrimEnd('\', '/')
    $snapshot = Join-Path $root ('models--' + $Repository.Replace('/', '--') + '\snapshots\' + $Revision)
    $destination = [IO.Path]::GetFullPath((Join-Path $snapshot $FileName))
    if (-not $destination.StartsWith($snapshot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The model filename escapes its pinned native snapshot directory.'
    }
    return $destination
}

function Get-MissumNativeModelCatalog {
    param([Parameter(Mandatory = $true)][string] $NativeModelRoot)
    $python = Join-Path $env:USERPROFILE '.unsloth\studio\unsloth_studio\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { $python = (Assert-MissumCommand -Name 'python.exe').Source }
    $json = & $python (Resolve-MissumRepositoryPath -RelativePath 'workers\coding\catalog.py') --model-root $NativeModelRoot --list-models
    if ($LASTEXITCODE -ne 0) { throw 'The native model catalog could not be read.' }
    return @($json | ConvertFrom-Json)
}

function Invoke-MissumAiCompose {
    param(
        [Parameter(Mandatory = $true)] $Paths,
        [Parameter(Mandatory = $true)] [string[]] $Arguments
    )

    $docker = Resolve-MissumDockerCommand
    & $docker compose --env-file $Paths.EnvironmentFile -f $Paths.ComposeFile @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed with exit code $LASTEXITCODE."
    }
}

function Invoke-MissumDotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $CommandArguments
    )

    Assert-MissumCommand -Name 'dotnet' | Out-Null
    Write-Host ("dotnet " + ($CommandArguments -join ' ')) -ForegroundColor DarkGray
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-MissumBuildId {
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($null -ne $git) {
        $commit = $null
        try {
            $commit = (& git -C $script:MissumRepositoryRoot rev-parse --short=12 HEAD 2>$null)
        }
        catch {
            $commit = $null
        }
        if ($null -ne $commit -and -not [string]::IsNullOrWhiteSpace($commit)) {
            return ([string]$commit).Trim()
        }
    }

    return [DateTime]::UtcNow.ToString('yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
}

function Get-MissumBuiltAt {
    return [DateTime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
}

function Assert-MissumArtifactPath {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $artifactRoot = Resolve-MissumRepositoryPath -RelativePath 'artifacts'
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $prefix = $artifactRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path must stay below '$artifactRoot': $resolved"
    }

    return $resolved
}

function Reset-MissumArtifactDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $resolved = Assert-MissumArtifactPath -Path $Path
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}

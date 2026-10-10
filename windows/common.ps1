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

    # A lexical prefix cannot confine a junction or symbolic link to artifacts.
    $ancestor = $resolved.TrimEnd('\', '/')
    while ($ancestor.Length -ge $artifactRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Artifact paths must not traverse reparse points: $ancestor"
            }
        }
        if ([string]::Equals($ancestor, $artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }

    return $resolved
}

function Assert-MissumArtifactTree {
    param([Parameter(Mandatory = $true)][string] $Path)
    $resolved = Assert-MissumArtifactPath -Path $Path
    if (Test-Path -LiteralPath $resolved) {
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($resolved)
        while ($pending.Count -gt 0) {
            $entry = Get-Item -LiteralPath $pending.Pop() -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Artifact trees must not contain reparse points: $($entry.FullName)"
            }
            if ($entry.PSIsContainer) {
                foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) { $pending.Push($child.FullName) }
            }
        }
    }
    return $resolved
}

function Assert-MissumPublishDirectoryIdle {
    param([Parameter(Mandatory = $true)][string] $Path)
    $resolved = Assert-MissumArtifactPath -Path $Path
    $prefix = $resolved.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($candidate in @(Get-Process -Name Missum, ExtensionHost -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.HasExited) { continue }
            $executablePath = $candidate.Path
        }
        catch { throw "Cannot establish the executable path of process $($candidate.Id); publish has not replaced any artifact." }
        if ([string]::IsNullOrWhiteSpace($executablePath)) {
            throw "Cannot establish the executable path of process $($candidate.Id); publish has not replaced any artifact."
        }
        if ([IO.Path]::GetFullPath($executablePath).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish directory is in use by process $($candidate.Id): $executablePath. The running application will not be stopped."
        }
    }
}

function Get-MissumPublishSidecarFiles {
    param([Parameter(Mandatory = $true)][string] $Directory)
    $resolved = Assert-MissumArtifactPath -Path $Directory
    $parent = [IO.Path]::GetDirectoryName($resolved.TrimEnd('\', '/'))
    $prefix = [IO.Path]::GetFileName($resolved.TrimEnd('\', '/')) + '.'
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { return }
    foreach ($file in Get-ChildItem -LiteralPath $parent -File -Force) {
        if (-not $file.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $suffix = $file.Name.Substring($prefix.Length)
        if ($suffix -match '^(manifest\.json|native-[A-Za-z0-9._-]+\.(json|jsonl|png)|math-[A-Za-z0-9._-]+\.png|chat-streaming-validation\.json|simulation-view\.jsonl|publication-view\.jsonl|crash\.log)$') {
            $null = Assert-MissumArtifactPath -Path $file.FullName
            $file
        }
    }
}

function Install-MissumPublishStage {
    param([Parameter(Mandatory = $true)][string] $StageDirectory,
        [Parameter(Mandatory = $true)][string] $OutputDirectory)
    $stage = (Assert-MissumArtifactTree -Path $StageDirectory).TrimEnd('\', '/')
    $output = (Assert-MissumArtifactTree -Path $OutputDirectory).TrimEnd('\', '/')
    $parent = [IO.Path]::GetDirectoryName($output)
    $leaf = [IO.Path]::GetFileName($output)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($stage), $parent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($stage) -notmatch ('^' + [regex]::Escape($leaf) + '\.staging-[a-f0-9]{32}$')) {
        throw 'Publish staging must be a fresh sibling directory of the final artifact.'
    }
    if (-not (Test-Path -LiteralPath $stage -PathType Container) -or
        -not (Test-Path -LiteralPath ($stage + '.manifest.json') -PathType Leaf)) {
        throw 'Publish staging is incomplete; the previous artifact has not been replaced.'
    }
    Assert-MissumPublishDirectoryIdle -Path $output
    Assert-MissumPublishDirectoryIdle -Path $stage
    $backup = Assert-MissumArtifactPath -Path (Join-Path $parent ($leaf + '.previous-' + [Guid]::NewGuid().ToString('N')))
    $stageSidecars = @(Get-MissumPublishSidecarFiles -Directory $stage)
    $oldSidecars = @(Get-MissumPublishSidecarFiles -Directory $output)
    $moves = [Collections.Generic.List[object]]::new()
    function Move-PublishEntry {
        param([string] $Source, [string] $Destination)
        $null = Assert-MissumArtifactTree -Path $Source
        $null = Assert-MissumArtifactPath -Path $Destination
        if (Test-Path -LiteralPath $Destination) { throw "Publish move destination already exists: $Destination" }
        Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
        $moves.Add([pscustomobject]@{ Source = $Source; Destination = $Destination })
    }
    try {
        if (Test-Path -LiteralPath $output) { Move-PublishEntry $output $backup }
        foreach ($file in $oldSidecars) { Move-PublishEntry $file.FullName ($backup + $file.FullName.Substring($output.Length)) }
        Move-PublishEntry $stage $output
        foreach ($file in $stageSidecars) { Move-PublishEntry $file.FullName ($output + $file.FullName.Substring($stage.Length)) }
    }
    catch {
        $publishError = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        for ($index = $moves.Count - 1; $index -ge 0; $index--) {
            $move = $moves[$index]
            try {
                $null = Assert-MissumArtifactTree -Path $move.Destination
                $null = Assert-MissumArtifactPath -Path $move.Source
                if (Test-Path -LiteralPath $move.Source) { throw "Rollback destination already exists: $($move.Source)" }
                if (Test-Path -LiteralPath $move.Destination -PathType Container) { Assert-MissumPublishDirectoryIdle -Path $move.Destination }
                Move-Item -LiteralPath $move.Destination -Destination $move.Source -ErrorAction Stop
            }
            catch { $rollbackErrors.Add($_.Exception.Message) }
        }
        if ($rollbackErrors.Count -gt 0) {
            throw "Publish failed: $($publishError.Exception.Message). Rollback could not finish: $($rollbackErrors -join ' '). Preserved backup: $backup"
        }
        throw $publishError
    }
    # A cleanup failure may retain an old backup; it must not undo a verified publish.
    try {
        if (Test-Path -LiteralPath $backup) {
            $null = Assert-MissumArtifactTree -Path $backup
            Assert-MissumPublishDirectoryIdle -Path $backup
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop
        }
        foreach ($file in @(Get-MissumPublishSidecarFiles -Directory $backup)) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop }
    }
    catch { Write-Warning "Verified publish is installed; previous backup cleanup was deferred: $($_.Exception.Message)" }
    return $output
}

function Reset-MissumArtifactDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    $resolved = Assert-MissumArtifactTree -Path $Path
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    return $resolved
}

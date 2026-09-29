#requires -Version 5.1
<##
.SYNOPSIS
Copies a stopped legacy stack into Missum without deleting or stopping its sources.
.DESCRIPTION
Use -CheckOnly before cutover. Then stop the source Compose project and its owned
native runtime and run the same command without -CheckOnly. Source roots, database
name, environment prefix and Compose project are deliberately explicit inputs.
Existing unrelated destination data is rejected. An interrupted copy can resume;
different destination files are never overwritten. A completed migration is a no-op.
Client chat databases are outside this script's scope. No Docker builds, starts,
stops, removals, volume changes, model downloads or native binary updates occur.
##>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $SourceDataRoot,
    [Parameter(Mandatory = $true)][string] $SourceNativeStateRoot,
    [Parameter(Mandatory = $true)][string] $SourceDatabaseName,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Z][A-Z0-9_]*_$')][string] $SourceEnvironmentPrefix,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-z0-9][a-z0-9_-]*$')][string] $SourceComposeProject,
    [Parameter(Mandatory = $true)][string] $NativeBinaryPath,
    [string] $SourceModelRoot,
    [string] $SourceEnvironmentFile,
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeStateRoot = (Join-Path $env:USERPROFILE '.missum\native-runtime'),
    [string] $NativeModelRoot,
    [string] $ServerIp,
    [string] $ImageVersion = '2.0.0',
    [string] $PythonPath,
    [switch] $CheckOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

function Resolve-MigrationDirectory {
    param([string] $Path, [switch] $MustExist)
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/')) { throw "A drive root is not a migration directory: $resolved" }
    if ($MustExist -and -not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "Source directory does not exist: $resolved" }
    return $resolved
}

function Test-MigrationOverlap {
    param([string] $First, [string] $Second)
    return $First.Equals($Second, [StringComparison]::OrdinalIgnoreCase) -or
        $First.StartsWith($Second + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $Second.StartsWith($First + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoMigrationLinks {
    param([string] $Root)
    $ancestor = $Root
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Migration directory has a reparse-point ancestor: $ancestor"
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $items = @((Get-Item -LiteralPath $Root -Force)) + @(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    foreach ($item in $items) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse points require a separately reviewed migration: $($item.FullName)" }
    }
}

function Write-MigrationJson {
    param([string] $Path, $Value)
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $null) }
    else { [IO.File]::Move($temporary, $Path) }
}

$script:MigrationFiles = New-Object 'System.Collections.Generic.List[object]'
function Copy-VerifiedMigrationFile {
    param([string] $Source, [string] $Destination)
    $sourceInfo = Get-Item -LiteralPath $Source -Force
    $hash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash -ne $hash) { throw "Destination differs; nothing overwritten: $Destination" }
    }
    else {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Destination)) -Force | Out-Null
        $temporary = $Destination + '.' + [Guid]::NewGuid().ToString('N') + '.migration'
        try {
            Copy-Item -LiteralPath $Source -Destination $temporary
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $hash) { throw "Copy verification failed: $Source" }
            [IO.File]::Move($temporary, $Destination)
        }
        finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    }
    $after = Get-Item -LiteralPath $Source -Force
    if ($after.Length -ne $sourceInfo.Length -or $after.LastWriteTimeUtc -ne $sourceInfo.LastWriteTimeUtc) { throw "Source changed during migration: $Source" }
    $script:MigrationFiles.Add([pscustomobject]@{ Source = $Source; Destination = $Destination; Bytes = $sourceInfo.Length; Sha256 = $hash })
}

function Copy-VerifiedMigrationTree {
    param([string] $Source, [string] $Destination, [string[]] $Exclude = @())
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Force -Recurse) {
        $relative = $file.FullName.Substring($Source.Length).TrimStart('\', '/')
        $skip = $false
        foreach ($entry in $Exclude) {
            if ($relative.Equals($entry, [StringComparison]::OrdinalIgnoreCase) -or
                $relative.StartsWith($entry.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        if ($skip) { continue }
        $target = [IO.Path]::GetFullPath((Join-Path $Destination $relative))
        if (-not $target.StartsWith($Destination.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Copy target escaped its destination root.' }
        Copy-VerifiedMigrationFile -Source $file.FullName -Destination $target
    }
}

$SourceDataRoot = Resolve-MigrationDirectory $SourceDataRoot -MustExist
$SourceNativeStateRoot = Resolve-MigrationDirectory $SourceNativeStateRoot -MustExist
$DataRoot = Resolve-MigrationDirectory $DataRoot
$NativeStateRoot = Resolve-MigrationDirectory $NativeStateRoot
if ([string]::IsNullOrWhiteSpace($SourceEnvironmentFile)) { $SourceEnvironmentFile = Join-Path $SourceDataRoot 'stack.env' }
$SourceEnvironmentFile = [IO.Path]::GetFullPath($SourceEnvironmentFile)
if (-not (Test-Path -LiteralPath $SourceEnvironmentFile -PathType Leaf)) { throw "Source environment is missing: $SourceEnvironmentFile" }
$sourceEnvironment = Read-MissumAiStackEnvironment -Path $SourceEnvironmentFile
if ([string]::IsNullOrWhiteSpace($SourceModelRoot)) { $SourceModelRoot = $sourceEnvironment[$SourceEnvironmentPrefix + 'MODEL_ROOT'] }
if ([string]::IsNullOrWhiteSpace($SourceModelRoot)) { $SourceModelRoot = Join-Path $SourceDataRoot 'Models' }
$SourceModelRoot = Resolve-MigrationDirectory $SourceModelRoot -MustExist
if ([string]::IsNullOrWhiteSpace($ModelRoot)) { $ModelRoot = Join-Path $DataRoot 'Models' }
$ModelRoot = Resolve-MigrationDirectory $ModelRoot
if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { $NativeModelRoot = $sourceEnvironment[$SourceEnvironmentPrefix + 'NATIVE_MODEL_ROOT'] }
if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { $NativeModelRoot = $sourceEnvironment[$SourceEnvironmentPrefix + 'CODING_MODEL_ROOT'] }
if ([string]::IsNullOrWhiteSpace($NativeModelRoot)) { throw 'Provide -NativeModelRoot; the source environment does not identify the native model cache.' }
$NativeModelRoot = Resolve-MigrationDirectory $NativeModelRoot -MustExist
if ([string]::IsNullOrWhiteSpace($ServerIp)) { $ServerIp = $sourceEnvironment[$SourceEnvironmentPrefix + 'EXPECTED_LAN_IP'] }
if ([string]::IsNullOrWhiteSpace($ServerIp)) { $ServerIp = '192.168.0.67' }
if ([IO.Path]::GetFileName($SourceDatabaseName) -ne $SourceDatabaseName) { throw '-SourceDatabaseName must be a filename without directories.' }
$sourceDatabase = Join-Path $SourceDataRoot ('data\database\' + $SourceDatabaseName)
if (-not (Test-Path -LiteralPath $sourceDatabase -PathType Leaf)) { throw "Source database is missing: $sourceDatabase" }
$NativeBinaryPath = [IO.Path]::GetFullPath($NativeBinaryPath)
if (-not (Test-Path -LiteralPath $NativeBinaryPath -PathType Leaf) -or [IO.Path]::GetFileName($NativeBinaryPath) -ne 'llama-server.exe') { throw 'Provide the existing tested llama-server.exe path.' }
$nativeBinarySha256 = (Get-FileHash -LiteralPath $NativeBinaryPath -Algorithm SHA256).Hash
foreach ($sourceRoot in @($SourceDataRoot, $SourceModelRoot, $SourceNativeStateRoot, $NativeModelRoot)) {
    foreach ($targetRoot in @($DataRoot, $ModelRoot, $NativeStateRoot)) {
        if (Test-MigrationOverlap $sourceRoot $targetRoot) { throw "Source and destination roots overlap: $sourceRoot / $targetRoot" }
    }
}
foreach ($targetRoot in @($DataRoot, $ModelRoot, $NativeStateRoot)) {
    foreach ($sourceFile in @($SourceEnvironmentFile, $NativeBinaryPath)) {
        if ($sourceFile.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Source file is inside a destination tree: $sourceFile" }
    }
}
if ((Test-MigrationOverlap $DataRoot $NativeStateRoot) -or (Test-MigrationOverlap $ModelRoot $NativeStateRoot) -or $ModelRoot -eq $DataRoot -or
    $DataRoot.StartsWith($ModelRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination roots must be distinct; only ModelRoot may be beneath DataRoot.' }

$request = [ordered]@{ SourceDataRoot = $SourceDataRoot; SourceNativeStateRoot = $SourceNativeStateRoot; SourceModelRoot = $SourceModelRoot;
    SourceEnvironmentFile = $SourceEnvironmentFile; SourceDatabaseName = $SourceDatabaseName; SourceEnvironmentPrefix = $SourceEnvironmentPrefix;
    SourceComposeProject = $SourceComposeProject; DataRoot = $DataRoot; ModelRoot = $ModelRoot; NativeStateRoot = $NativeStateRoot;
    NativeModelRoot = $NativeModelRoot; NativeBinaryPath = $NativeBinaryPath; NativeBinarySha256 = $nativeBinarySha256; ServerIp = $ServerIp; ImageVersion = $ImageVersion }
$identity = $request | ConvertTo-Json -Compress
$receiptPath = Join-Path $DataRoot 'missum-migration.json'
$progressPath = Join-Path $DataRoot 'missum-migration.pending.json'
$destinationDatabase = Join-Path $DataRoot 'data\database\missum-ai-server.db'
if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    if ($receipt.Identity -ne $identity) { throw 'This target already belongs to a different completed migration.' }
    if (-not (Test-Path -LiteralPath $destinationDatabase -PathType Leaf)) { throw 'Migration receipt exists, but the destination database is missing.' }
    Write-Output ([pscustomobject]@{ AlreadyMigrated = $true; Receipt = $receiptPath; SourceRetained = $true })
    return
}
if (Test-Path -LiteralPath $progressPath -PathType Leaf) {
    $pending = Get-Content -LiteralPath $progressPath -Raw | ConvertFrom-Json
    if ($pending.Identity -ne $identity) { throw 'The interrupted migration used different parameters; no destination files were changed.' }
}
else {
    foreach ($destination in @($DataRoot, $ModelRoot, $NativeStateRoot) | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath $destination -PathType Container)) { continue }
        $existing = @(Get-ChildItem -LiteralPath $destination -Force)
        if ($destination -eq $DataRoot) { $existing = @($existing | Where-Object { $_.Name -ne 'stack.env' }) }
        if ($existing.Count -gt 0) { throw "Destination already contains unrelated files: $destination" }
    }
}
foreach ($root in @($SourceDataRoot, $SourceModelRoot, $SourceNativeStateRoot, $DataRoot, $ModelRoot, $NativeStateRoot) | Select-Object -Unique) { Assert-NoMigrationLinks $root }

$docker = Resolve-MissumDockerCommand
$blocking = New-Object 'System.Collections.Generic.List[string]'
foreach ($project in @($SourceComposeProject, 'missum-ai-stack') | Select-Object -Unique) {
    $running = @(& $docker ps --filter "label=com.docker.compose.project=$project" --format '{{.Names}}')
    if ($LASTEXITCODE -ne 0) { throw 'Docker inventory failed; cannot establish a safe cutover state.' }
    if ($running.Count -gt 0) { $blocking.Add("Stop Compose project $project before copying: " + ($running -join ', ')) }
}
foreach ($port in @(8080, 8081, 8082)) {
    if (@(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0) { $blocking.Add("Port $port is still listening; stop its verified owner before copying.") }
}
foreach ($process in Get-CimInstance Win32_Process) {
    if ($process.Name -match '^python(w)?\.exe$' -and $process.CommandLine -and
        ($process.CommandLine.IndexOf($SourceNativeStateRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
         $process.CommandLine.IndexOf($NativeStateRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0)) {
        $blocking.Add("Native supervisor process $($process.ProcessId) is still using a migration state directory.")
    }
}
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = (Assert-MissumCommand -Name 'python.exe').Source }
$databaseProbe = @'
import json, pathlib, sqlite3, sys
with sqlite3.connect(pathlib.Path(sys.argv[1]).resolve().as_uri()+'?mode=ro',uri=True) as db:
    db.execute('PRAGMA query_only=ON')
    print(json.dumps(dict(db.execute('SELECT state,COUNT(*) FROM runs GROUP BY state'))))
'@
$states = ($databaseProbe | & $PythonPath - $sourceDatabase) | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not read source run states.' }
foreach ($state in @('Queued', 'Running')) {
    $property = $states.PSObject.Properties[$state]
    if ($null -ne $property -and [long]$property.Value -gt 0) { $blocking.Add("Source database still records $($property.Value) $state runs; resolve them before copying.") }
}
$copyBytes = 0L
foreach ($root in @((Join-Path $SourceDataRoot 'data'), $SourceModelRoot, $SourceNativeStateRoot)) {
    $copyBytes += [long](Get-ChildItem -LiteralPath $root -File -Force -Recurse | Measure-Object Length -Sum).Sum
}
$plan = [pscustomobject]@{ ReadyToCopy = $blocking.Count -eq 0; BlockingReasons = @($blocking.ToArray()); SourceRunStates = $states;
    EstimatedCopyBytes = $copyBytes; SourceRetained = $true; Parameters = $request; Receipt = $receiptPath }
if ($CheckOnly) { Write-Output $plan; return }
if ($blocking.Count -gt 0) { throw ($blocking -join [Environment]::NewLine) }

New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
Write-MigrationJson -Path $progressPath -Value ([ordered]@{ Schema = 'missum.stack.migration.pending.v1'; Identity = $identity; StartedUtc = [DateTime]::UtcNow.ToString('o') })
$databaseRelative = 'database\' + $SourceDatabaseName
Copy-VerifiedMigrationTree -Source (Join-Path $SourceDataRoot 'data') -Destination (Join-Path $DataRoot 'data') `
    -Exclude @($databaseRelative, ($databaseRelative + '-wal'), ($databaseRelative + '-shm'), ($databaseRelative + '-journal'))
Copy-VerifiedMigrationTree -Source $SourceModelRoot -Destination $ModelRoot
Copy-VerifiedMigrationTree -Source $SourceNativeStateRoot -Destination $NativeStateRoot `
    -Exclude @('supervisor.json', 'runtime.json', 'models.ini', 'stop.requested', 'official-llama-server.path', 'support')
if (Test-Path -LiteralPath (Join-Path $SourceDataRoot 'migration-backups') -PathType Container) {
    Copy-VerifiedMigrationTree -Source (Join-Path $SourceDataRoot 'migration-backups') -Destination (Join-Path $DataRoot 'migration-backups')
}
Copy-VerifiedMigrationFile -Source $SourceEnvironmentFile -Destination (Join-Path $DataRoot 'migration-backups\source-stack.env')
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationDatabase)) -Force | Out-Null
$databaseReceipt = Copy-MissumSqliteSnapshot -Source $sourceDatabase -Destination $destinationDatabase -PythonPath $PythonPath
$converted = @{}
foreach ($key in $sourceEnvironment.Keys) {
    $newKey = if ($key.StartsWith($SourceEnvironmentPrefix, [StringComparison]::Ordinal)) { 'MISSUM_AI_' + $key.Substring($SourceEnvironmentPrefix.Length) } else { $key }
    $converted[$newKey] = $sourceEnvironment[$key]
}
$converted['MISSUM_AI_MODEL_ROOT'] = $ModelRoot -replace '\\','/'
$converted['MISSUM_AI_NATIVE_MODEL_ROOT'] = $NativeModelRoot -replace '\\','/'
$environmentPath = Join-Path $DataRoot 'stack.env'
$environmentText = ($converted.Keys | Sort-Object | ForEach-Object { "$_=$($converted[$_])" }) -join "`n"
[IO.File]::WriteAllText($environmentPath, $environmentText + "`n", [Text.UTF8Encoding]::new($false))
$paths = Get-MissumAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
Write-MissumAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion -NativeBinaryPath $NativeBinaryPath -NativeStateDirectory $NativeStateRoot
[IO.File]::WriteAllText((Join-Path $DataRoot 'native-llama-server.path'), $NativeBinaryPath + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $NativeStateRoot 'official-llama-server.path'), $NativeBinaryPath + "`n", [Text.UTF8Encoding]::new($false))
if ((Get-FileHash -LiteralPath $NativeBinaryPath -Algorithm SHA256).Hash -ne $nativeBinarySha256) { throw 'The selected native binary changed during migration.' }
Write-MigrationJson -Path $receiptPath -Value ([ordered]@{ Schema = 'missum.stack.migration.v1'; Identity = $identity;
    CompletedUtc = [DateTime]::UtcNow.ToString('o'); Parameters = $request; Database = $databaseReceipt;
    Files = @($script:MigrationFiles.ToArray()); SourcesRetained = $true; ContainersChanged = $false })
[IO.File]::Delete($progressPath)
Write-Output ([pscustomobject]@{ Migrated = $true; Receipt = $receiptPath; Database = $destinationDatabase;
    NativeBinaryPath = $NativeBinaryPath; NativeStateRoot = $NativeStateRoot; ImageVersion = $ImageVersion; SourcesRetained = $true })

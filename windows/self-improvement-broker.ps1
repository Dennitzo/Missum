#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $RequestPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$request = Get-Content -LiteralPath $RequestPath -Encoding UTF8 -Raw | ConvertFrom-Json
$job = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($RequestPath))
if ($request.candidateId -cnotmatch '^[0-9A-F]{32}$' -or [IO.Path]::GetFileName($job) -cne $request.candidateId) {
    throw 'Ungültiger Build-Auftrag.'
}
$source = [IO.Path]::GetFullPath($request.workspace).TrimEnd('\')
$snapshot = Join-Path $job 'source'
$report = Join-Path $job 'report.json'
$steps = [Collections.Generic.List[object]]::new()
$phase = 'snapshot'
$lock = $null

function Publish-Report([string] $Status, [string] $Detail = '', $Artifacts = $null) {
    $result = [ordered]@{
        candidateId = $request.candidateId
        status = $Status
        phase = $phase
        detail = $Detail
        steps = $steps.ToArray()
        reportPath = $report
        artifacts = $Artifacts
        workerPid = $PID
        workerStartedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O')
        updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $pending = $report + '.pending'
    [IO.File]::WriteAllText($pending, ($result | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($report)) { [IO.File]::Replace($pending, $report, [NullString]::Value) }
    else { [IO.File]::Move($pending, $report) }
}

function Read-GitOutput([string] $Directory, [string] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command git.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($name in @('GIT_DIR', 'GIT_COMMON_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE')) {
        $start.EnvironmentVariables.Remove($name)
    }
    $git = [Diagnostics.Process]::Start($start)
    try {
        $output = $git.StandardOutput.ReadToEnd()
        $git.WaitForExit()
        if ($git.ExitCode -ne 0) { throw "Git fehlgeschlagen ($Arguments)." }
        return $output
    } finally { $git.Dispose() }
}

function Copy-Snapshot {
    if ($job.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Der Speicherort der Build-Aufträge liegt innerhalb des Quell-Workspace.'
    }
    $worktrees = (Read-GitOutput $request.repository 'worktree list --porcelain -z').Split([char]0)
    $registered = @($worktrees | Where-Object { $_.StartsWith('worktree ', [StringComparison]::Ordinal) } |
        ForEach-Object { [IO.Path]::GetFullPath($_.Substring(9)).TrimEnd('\') })
    if ($source -notin $registered) { throw 'Der Workspace ist im zugeordneten Repository nicht als Worktree registriert.' }
    $entries = [Collections.Generic.List[object]]::new()
    $paths = (Read-GitOutput $source 'ls-files --cached --others --exclude-standard -z').Split([char]0)
    foreach ($relative in ($paths | Where-Object { $_ } | Sort-Object -Unique)) {
        $file = [IO.Path]::GetFullPath((Join-Path $source $relative))
        if (-not $file.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Git-Pfad liegt außerhalb des Workspace: $relative"
        }
        $item = Get-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        if ($null -eq $item) { continue }
        $cursor = $item
        while ($null -ne $cursor) {
            if (($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Der Build-Workspace enthält einen Dateisystemverweis: $relative"
            }
            if ($cursor.FullName -eq $source) { break }
            if ($cursor -is [IO.FileInfo]) { $cursor = $cursor.Directory } else { $cursor = $cursor.Parent }
        }
        if ($item.PSIsContainer) { throw "Verschachteltes Git-Repository kann nicht isoliert kopiert werden: $relative" }
        $destination = Join-Path $snapshot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file, $destination, $false)
        $digest = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $digest) {
            throw "Quelldatei änderte sich während der Kopie: $relative"
        }
        $entries.Add([ordered]@{ path = $relative; sha256 = $digest })
    }
    $manifest = Join-Path $job 'source-manifest.json'
    [IO.File]::WriteAllText($manifest, (ConvertTo-Json -InputObject $entries.ToArray() -Depth 3), [Text.UTF8Encoding]::new($false))
    return $manifest
}

function Invoke-Step([string] $Name, [string] $Executable, [string[]] $Arguments) {
    $script:phase = $Name
    Publish-Report 'running'
    $log = Join-Path $job ($Name + '.log')
    $previous = $ErrorActionPreference
    try {
        $command = (Get-Command $Executable -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
        $ErrorActionPreference = 'Continue'
        & $command @Arguments *> $log
        $exit = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    $errors = @()
    if ($exit -ne 0) {
        $errors = @(Get-Content -LiteralPath $log -Tail 24 | ForEach-Object {
            $line = [string]$_
            $line.Substring(0, [Math]::Min(400, $line.Length))
        })
    }
    $steps.Add([ordered]@{ name = $Name; exitCode = $exit; logPath = $log; diagnostics = $errors })
    Publish-Report 'running'
    return $exit -eq 0
}

try {
    $lock = [IO.File]::Open((Join-Path ([IO.Path]::GetDirectoryName($job)) 'build.lock'),
        [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
    Publish-Report 'running'
    $manifest = Copy-Snapshot
    Set-Location -LiteralPath $snapshot
    $shell = Join-Path $PSHOME 'powershell.exe'
    $shellArgs = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File')
    $version = 'candidate-' + $request.candidateId.ToLowerInvariant()
    $runnerImage = 'missum-ai/research-runner:' + $version
    $portable = Join-Path $snapshot 'artifacts\portable\candidate'
    $stackRoot = Join-Path $job 'stack'
    $env:MISSUM_AI_IMAGE_VERSION = $version
    $env:MISSUM_AI_DATA_ROOT = $stackRoot
    $env:MISSUM_AI_MODEL_ROOT = Join-Path $stackRoot 'Models'
    $env:ASSISTANT_SHARED_DATA_ROOT = Join-Path $job 'shared'
    $env:ASSISTANT_STACK_DATA_ROOT = $stackRoot
    $env:ASSISTANT_NATIVE_STATE_ROOT = Join-Path $job 'native'
    $env:ASSISTANT_DISABLE_RUNTIME_AUTOSTART = '1'
    $app = Invoke-Step 'portable' $shell ($shellArgs + @('windows\build.ps1', '-PortableOutputDirectory', $portable))
    $appTests = Invoke-Step 'app-tests' 'dotnet' @('test', 'tests\Missum.Tests\Missum.Tests.csproj',
        '-c', 'Release', '-p:Platform=x64', '--nologo')
    $stack = Invoke-Step 'ai-stack' $shell ($shellArgs + @('windows\build-ai-stack.ps1', '-DataRoot', $stackRoot,
        '-ServerIp', '127.0.0.1', '-ImageVersion', $version, '-ResearchRunnerImage', $runnerImage))
    $portableSmoke = $false
    if ($app) {
        $portableSmoke = Invoke-Step 'portable-smoke' $shell ($shellArgs + @('windows\smoke.ps1',
            '-PublishDirectory', $portable, '-Mode', 'SingleFile'))
    }
    if (-not ($app -and $appTests -and $stack -and $portableSmoke)) {
        Publish-Report 'failed' 'Mindestens eine Pflichtprüfung ist fehlgeschlagen.'
        exit 1
    }

    $phase = 'gateway-smoke'
    Publish-Report 'running'
    $container = 'missum-' + $version
    $gateway = "missum-ai/gateway:$version"
    $runArgs = @('run', '--detach', '--rm', '--name', $container, '-p', '127.0.0.1::8080',
        '-e', 'MISSUM_AI_EXPECTED_LAN_IP=127.0.0.1', '-e', 'MISSUM_AI_PUBLIC_URL=http://127.0.0.1', $gateway)
    & docker @runArgs | Set-Content -LiteralPath (Join-Path $job 'gateway-smoke.log')
    if ($LASTEXITCODE -ne 0) { throw 'Kandidaten-Gateway konnte nicht gestartet werden.' }
    try {
        $address = (& docker port $container '8080/tcp' | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or $address -notmatch '^127\.0\.0\.1:[0-9]+$') { throw 'Ungültiger Prüfport.' }
        $live = $null
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try { $live = Invoke-RestMethod "http://$address/v1/health/live" -TimeoutSec 2; break }
            catch { Start-Sleep -Milliseconds 500 }
        }
        if ($null -eq $live -or $live.status -ne 'live' -or $live.protocolVersion -ne '1.0') {
            throw 'Kandidaten-Gateway hat die Start- und Protokollprüfung nicht bestanden.'
        }
        $steps.Add([ordered]@{ name = $phase; exitCode = 0; logPath = (Join-Path $job 'gateway-smoke.log') })
    } finally { & docker stop $container | Out-Null }

    $images = @{}
    foreach ($image in @($gateway, "missum-ai/speech:$version", "missum-ai/media:$version", "missum-ai/image:$version", $runnerImage)) {
        $digest = & docker image inspect --format '{{.Id}}' $image
        if ($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[0-9a-f]{64}$') { throw "Image-Digest fehlt: $image" }
        $images[$image] = $digest
    }
    $portableRoot = [IO.Path]::GetFullPath($portable).TrimEnd('\')
    $portableEntries = @(Get-ChildItem -LiteralPath $portableRoot -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($portableRoot.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    if ($portableEntries.Count -eq 0) { throw 'Der Portable-Kandidat enthält keine Dateien.' }
    $portableFiles = Join-Path $job 'portable-files.json'
    [IO.File]::WriteAllText($portableFiles, (ConvertTo-Json -InputObject $portableEntries -Depth 3), [Text.UTF8Encoding]::new($false))
    $phase = 'complete'
    Publish-Report 'verified' '' @{
        portableDirectory = $portableRoot
        portableFiles = $portableFiles
        sourceManifest = $manifest
        portableManifest = $portable + '.manifest.json'
        portableExecutable = Join-Path $portable 'Missum Labs.exe'
        images = $images
    }
} catch {
    Publish-Report 'failed' $_.Exception.Message
    exit 1
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
}

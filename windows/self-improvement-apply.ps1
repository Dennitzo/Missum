#requires -Version 5.1
# Applies one verified self-improvement candidate independently of the app and the gateway.
# Every phase is persisted in state.json before and after it runs, so a restarted broker
# continues from the recorded phase. Phases before 'backup' never change anything and end in
# 'rejected'; any failure after the backup exists ends in a rollback ('rolledBack' or 'failed').
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $OperationPath,
    [switch] $Worker,
    [string] $Abort = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$opPath = [IO.Path]::GetFullPath($OperationPath)
$opDir = [IO.Path]::GetDirectoryName($opPath)
$statePath = Join-Path $opDir 'state.json'
$reattachPath = Join-Path $opDir 'reattach.json'
$backupDir = Join-Path $opDir 'backup'
$abortPath = Join-Path $opDir 'abort.json'
$utf8 = [Text.UTF8Encoding]::new($false)
$op = [IO.File]::ReadAllText($opPath, $utf8) | ConvertFrom-Json
if ($op.opId -cnotmatch '^[0-9A-F]{32}$' -or [IO.Path]::GetFileName($opDir) -cne $op.opId) { throw 'Ungültiger Übernahmeauftrag.' }

$Phases = @('revalidate', 'await-app-exit', 'drain', 'ownership', 'backup', 'swap-app', 'swap-stack', 'verify-stack', 'start-app', 'await-reattach')
$Terminal = @('applied', 'rejected', 'rolledBack', 'failed')

function Get-Setting([string] $Name, $Default) {
    $timeouts = $op.PSObject.Properties['timeouts']
    if ($null -ne $timeouts -and $null -ne $timeouts.Value.PSObject.Properties[$Name]) { return $timeouts.Value.$Name }
    return $Default
}

function Read-State {
    if (-not [IO.File]::Exists($statePath)) { return $null }
    $raw = [IO.File]::ReadAllText($statePath, $utf8) | ConvertFrom-Json
    $result = [ordered]@{}
    foreach ($p in $raw.PSObject.Properties) { $result[$p.Name] = $p.Value }
    return $result
}

function Save-State {
    $state.updatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    $pending = $statePath + '.' + [Guid]::NewGuid().ToString('N') + '.pending'
    [IO.File]::WriteAllText($pending, ($state | ConvertTo-Json -Depth 12), $utf8)
    if ([IO.File]::Exists($statePath)) { [IO.File]::Replace($pending, $statePath, [NullString]::Value) }
    else { [IO.File]::Move($pending, $statePath) }
}

function Add-History([string] $Event, [string] $Detail = '') {
    $state.history = @($state.history) + @([ordered]@{ at = [DateTimeOffset]::UtcNow.ToString('O'); event = $Event; detail = $Detail })
}

# Fails with a stable code; the catch blocks turn it into a reject or a rollback.
function New-Failure([string] $Code, [string] $Detail, $Problems = $null) {
    $exception = [Management.Automation.RuntimeException]::new($Detail)
    $exception.Data['code'] = $Code
    if ($null -ne $Problems) { $exception.Data['problems'] = @($Problems) }
    return $exception
}

function Invoke-Fault([string] $Phase) {
    if ($env:MISSUM_SELF_IMPROVEMENT_TEST_FAULTS -ne '1' -or [string]::IsNullOrEmpty($env:MISSUM_SELF_IMPROVEMENT_FAULT)) { return }
    foreach ($fault in $env:MISSUM_SELF_IMPROVEMENT_FAULT.Split(',')) {
        $parts = $fault.Trim().Split(':')
        if ($parts.Count -ne 2 -or $parts[1] -ne $Phase) { continue }
        $marker = Join-Path $opDir ("fault-" + $parts[0] + "-" + $Phase + ".fired")
        if ($parts[0] -eq 'crash' -and -not [IO.File]::Exists($marker)) {
            [IO.File]::WriteAllText($marker, $PID.ToString())
            [Environment]::Exit(99)
        }
        if ($parts[0] -eq 'fail') { throw (New-Failure 'selfImprovement.test_fault' "Testfehler in Phase '$Phase'.") }
        # Parks the broker at a phase once, so a test can kill every broker process exactly there.
        if ($parts[0] -eq 'hold' -and -not [IO.File]::Exists($marker)) {
            [IO.File]::WriteAllText($marker, $PID.ToString())
            while (-not [IO.File]::Exists((Join-Path $opDir ("release-" + $Phase)))) { Start-Sleep -Milliseconds 100 }
        }
    }
}

function Test-ProcessIdentity($ProcessId, $StartedAt) {
    if ($null -eq $ProcessId -or [string]::IsNullOrEmpty([string]$StartedAt)) { return $false }
    try {
        $process = [Diagnostics.Process]::GetProcessById([int]$ProcessId)
        if ($process.HasExited) { return $false }
        $expected = [DateTimeOffset]::Parse([string]$StartedAt).UtcDateTime
        return [Math]::Abs(($process.StartTime.ToUniversalTime() - $expected).TotalSeconds) -lt 2
    } catch { return $false }
}

# Docker Desktop may report a Windows bind source as its Linux host path (/run/desktop/mnt/host/c/...).
function ConvertTo-HostPath([string] $Source) {
    $normalized = $Source.Replace('\', '/').TrimEnd('/')
    if ($normalized -cmatch '^/run/desktop/mnt/host/([A-Za-z])/(.*)$') { $normalized = $Matches[1] + ':/' + $Matches[2] }
    return [IO.Path]::GetFullPath($normalized.Replace('/', '\')).TrimEnd('\')
}

function Get-FileSha([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Read-Entries([string] $Path) {
    $entries = [ordered]@{}
    # Windows PowerShell 5.1 emits a parsed JSON array as one pipeline object; assigning it first enumerates its items.
    $parsed = [IO.File]::ReadAllText($Path, $utf8) | ConvertFrom-Json
    foreach ($item in $parsed) {
        $relative = ([string]$item.path).Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('\') -contains '..') { throw (New-Failure 'selfImprovement.portable_manifest_invalid' "Ungültiger Pfad im Portable-Manifest: $relative") }
        $entries[$relative] = [string]$item.sha256
    }
    return $entries
}

function Compare-Directory([string] $Directory, $Entries) {
    $changed = [Collections.Generic.List[string]]::new()
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    foreach ($relative in $Entries.Keys) {
        $file = Join-Path $Directory $relative
        if (-not [IO.File]::Exists($file) -or (Get-FileSha $file) -ne $Entries[$relative]) { $changed.Add($relative) }
    }
    if ([IO.Directory]::Exists($Directory)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($Directory, '*', [IO.SearchOption]::AllDirectories)) {
            $relative = [IO.Path]::GetFullPath($file).Substring($root.Length)
            if (-not $Entries.Contains($relative)) { $changed.Add($relative) }
        }
    }
    return , $changed.ToArray()
}

function Invoke-Docker([string[]] $Arguments, [switch] $AllowFailure) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & docker @Arguments 2>&1 | ForEach-Object { [string]$_ } }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw (New-Failure 'selfImprovement.docker_failed' ("docker " + ($Arguments -join ' ') + " ist fehlgeschlagen: " + (($output | Select-Object -Last 6) -join ' ')))
    }
    return @($output)
}

function Invoke-Compose([string[]] $Arguments) {
    $prefix = @('compose', '--env-file', $op.stack.envFile, '-p', $op.stack.project)
    foreach ($file in @($op.stack.composeFiles)) { $prefix += @('-f', $file) }
    return Invoke-Docker ($prefix + $Arguments)
}

function Get-ImageId([string] $Image) {
    $output = Invoke-Docker @('image', 'inspect', '--format', '{{.Id}}', $Image) -AllowFailure
    if ($LASTEXITCODE -ne 0) { return $null }
    return ([string]($output | Select-Object -First 1)).Trim()
}

# Maps every candidate image to the tag the running installation uses for it.
function Get-ImageTargets {
    $targets = @()
    foreach ($property in $op.candidate.images.PSObject.Properties) {
        $repository = $property.Name.Substring(0, $property.Name.LastIndexOf(':'))
        $live = if ($repository -eq 'missum-ai/research-runner') { $repository + ':3' } else { $repository + ':' + $op.stack.currentImageVersion }
        $targets += [pscustomobject]@{ candidate = $property.Name; digest = [string]$property.Value; repository = $repository; live = $live; runner = ($repository -eq 'missum-ai/research-runner') }
    }
    return $targets
}

function Invoke-Http([string] $Path, [int] $TimeoutSeconds = 5) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri ($op.stack.gatewayUrl.TrimEnd('/') + $Path) -TimeoutSec $TimeoutSeconds `
            -Headers @{ 'X-Missum-Client-ID' = 'missum-self-improvement-broker' }
        return [pscustomobject]@{ status = [int]$response.StatusCode; body = ($response.Content | ConvertFrom-Json) }
    } catch [Net.WebException] {
        $code = if ($null -ne $_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        return [pscustomobject]@{ status = $code; body = $null }
    }
}

function Get-Readiness {
    $ready = Invoke-Http '/v1/health/ready'
    if ($ready.status -eq 200 -and $null -ne $ready.body) { return [string]$ready.body.status }
    if ($ready.status -eq 503) { return 'notReady' }
    return 'unreachable'
}

function Get-OtherActiveRuns {
    $activity = Invoke-Http ('/v1/runs/activity?except=' + [Uri]::EscapeDataString($op.serverRunId))
    if ($activity.status -ne 200) { throw (New-Failure 'selfImprovement.gateway_unavailable' "Das Gateway meldet keine Laufaktivität (HTTP $($activity.status)).") }
    return @($activity.body.activeRuns)
}

function Start-App {
    $start = [Diagnostics.ProcessStartInfo]::new($op.app.executable)
    $start.WorkingDirectory = $op.app.installDir
    $start.UseShellExecute = $false
    if ($null -ne $op.PSObject.Properties['environment'] -and $null -ne $op.environment) {
        foreach ($p in $op.environment.PSObject.Properties) { $start.EnvironmentVariables[$p.Name] = [string]$p.Value }
    }
    $process = [Diagnostics.Process]::Start($start)
    return [pscustomobject]@{ pid = $process.Id; startedAt = $process.StartTime.ToUniversalTime().ToString('O') }
}

# The app was cancelled before handing over (abort.json); only possible while nothing was changed.
function Assert-NotAborted {
    if ([IO.File]::Exists($abortPath) -and $state.phaseIndex -lt [Array]::IndexOf($Phases, 'backup')) {
        throw (New-Failure 'selfImprovement.apply_cancelled' 'Die Übernahme wurde vor dem Neustart abgebrochen; es wurde nichts verändert.')
    }
}

# Ends the app with its child processes (extension hosts, native runtimes started by it).
function Stop-Tree($ProcessId, $StartedAt) {
    if (-not (Test-ProcessIdentity $ProcessId $StartedAt)) { return }
    $process = [Diagnostics.Process]::GetProcessById([int]$ProcessId)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & taskkill.exe /PID ([int]$ProcessId) /T /F 2>&1 | Out-Null } catch { }
    finally { $ErrorActionPreference = $previous }
    try { if (-not $process.HasExited) { $process.Kill() } } catch { }
    if (-not $process.WaitForExit(30000)) { throw (New-Failure 'selfImprovement.app_stop_failed' "Missum (PID $ProcessId) ließ sich nicht beenden.") }
}

function Copy-Tree([string] $From, [string] $To) {
    if ([IO.Directory]::Exists($To)) {
        foreach ($file in [IO.Directory]::GetFiles($To, '*', [IO.SearchOption]::AllDirectories)) { [IO.File]::SetAttributes($file, [IO.FileAttributes]::Normal) }
        [IO.Directory]::Delete($To, $true)
    }
    [IO.Directory]::CreateDirectory($To) | Out-Null
    $root = [IO.Path]::GetFullPath($From).TrimEnd('\') + '\'
    foreach ($file in [IO.Directory]::EnumerateFiles($From, '*', [IO.SearchOption]::AllDirectories)) {
        $target = Join-Path $To ([IO.Path]::GetFullPath($file).Substring($root.Length))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file, $target, $true)
    }
}

# Replaces the contents of a directory in place: the install path may be referenced by shortcuts.
function Set-DirectoryContents([string] $From, [string] $To) {
    [IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($file in [IO.Directory]::GetFiles($To, '*', [IO.SearchOption]::AllDirectories)) {
        [IO.File]::SetAttributes($file, [IO.FileAttributes]::Normal)
        [IO.File]::Delete($file)
    }
    foreach ($directory in [IO.Directory]::GetDirectories($To)) { [IO.Directory]::Delete($directory, $true) }
    $root = [IO.Path]::GetFullPath($From).TrimEnd('\') + '\'
    foreach ($file in [IO.Directory]::EnumerateFiles($From, '*', [IO.SearchOption]::AllDirectories)) {
        $target = Join-Path $To ([IO.Path]::GetFullPath($file).Substring($root.Length))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file, $target, $true)
    }
}

function Get-DatabaseSets {
    $sets = @()
    foreach ($name in @('Missum.db', 'project-memory.db')) { $sets += [pscustomobject]@{ key = 'app-' + $name; path = (Join-Path $op.app.dataDir $name) } }
    if ($state.changeStack) { $sets += [pscustomobject]@{ key = 'gateway'; path = (Join-Path $op.stack.dataRoot 'data\database\missum-ai-server.db') } }
    return $sets
}

# The owning processes are stopped, so the database, its WAL and shared memory are copied together.
function Backup-Database($Set) {
    $files = @()
    foreach ($suffix in @('', '-wal', '-shm')) {
        $source = $Set.path + $suffix
        if (-not [IO.File]::Exists($source)) { continue }
        $target = Join-Path $backupDir ('db\' + $Set.key + $suffix)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($source, $target, $true)
        if ((Get-FileSha $source) -ne (Get-FileSha $target)) { throw (New-Failure 'selfImprovement.backup_failed' "Datenbanksicherung ist inkonsistent: $source") }
        $files += [ordered]@{ suffix = $suffix; sha256 = (Get-FileSha $target) }
    }
    return [ordered]@{ key = $Set.key; path = $Set.path; files = $files }
}

function Restore-Database($Entry) {
    foreach ($suffix in @('', '-wal', '-shm')) {
        $current = $Entry.path + $suffix
        if ([IO.File]::Exists($current)) { [IO.File]::Delete($current) }
    }
    foreach ($file in @($Entry.files)) {
        $source = Join-Path $backupDir ('db\' + $Entry.key + $file.suffix)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Entry.path)) | Out-Null
        [IO.File]::Copy($source, $Entry.path + $file.suffix, $true)
        if ((Get-FileSha ($Entry.path + $file.suffix)) -ne $file.sha256) { throw (New-Failure 'selfImprovement.rollback_failed' "Datenbank konnte nicht wiederhergestellt werden: $($Entry.path)") }
    }
}

function Set-ImageVersion([string] $Version) {
    $lines = @([IO.File]::ReadAllLines($op.stack.envFile, $utf8))
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*MISSUM_AI_IMAGE_VERSION\s*=') { $lines[$i] = 'MISSUM_AI_IMAGE_VERSION=' + $Version; $found = $true }
    }
    if (-not $found) { $lines += 'MISSUM_AI_IMAGE_VERSION=' + $Version }
    $pending = $op.stack.envFile + '.pending'
    [IO.File]::WriteAllLines($pending, [string[]]$lines, $utf8)
    [IO.File]::Copy($pending, $op.stack.envFile, $true)
    [IO.File]::Delete($pending)
}

function Get-ProjectContainers {
    return @(Invoke-Docker @('ps', '-a', '--filter', ('label=com.docker.compose.project=' + $op.stack.project), '--format', '{{.Names}}') | Where-Object { $_ })
}

function Wait-Gateway([int] $Seconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        $live = Invoke-Http '/v1/health/live' 3
        if ($live.status -eq 200 -and $null -ne $live.body -and $live.body.status -eq 'live') { return $live.body }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Invoke-Phase([string] $Phase) {
    switch ($Phase) {
        'revalidate' {
            $problems = @()
            $entries = Read-Entries $op.candidate.portableFiles
            $changed = Compare-Directory $op.candidate.portableDirectory $entries
            if ($changed.Count -gt 0) { $problems += [ordered]@{ code = 'selfImprovement.portable_modified'; detail = 'Portable-Dateien wurden nach der Prüfung verändert.'; paths = @($changed | Select-Object -First 20) } }
            $changeStack = $false
            foreach ($target in Get-ImageTargets) {
                $actual = Get-ImageId $target.candidate
                if ($actual -cne $target.digest) {
                    $problems += [ordered]@{ code = 'selfImprovement.image_digest_mismatch'; detail = "Image '$($target.candidate)' hat den Digest '$actual' statt '$($target.digest)'." }
                }
                if ((Get-ImageId $target.live) -cne $target.digest) { $changeStack = $true }
            }
            if ($problems.Count -gt 0) { throw (New-Failure $problems[0].code (($problems | ForEach-Object { $_.detail }) -join ' ') $problems) }
            $state.changeApp = (Compare-Directory $op.app.installDir $entries).Count -gt 0
            $state.changeStack = $changeStack
            if (-not $state.changeApp -and -not $state.changeStack) {
                throw (New-Failure 'selfImprovement.no_changes' 'Der Kandidat entspricht bereits der laufenden Installation.')
            }
        }
        'await-app-exit' {
            $deadline = [DateTime]::UtcNow.AddSeconds([int](Get-Setting 'appExitSeconds' 120))
            while ((Test-ProcessIdentity $op.app.pid $op.app.startedAt) -and [DateTime]::UtcNow -lt $deadline) {
                Assert-NotAborted
                Start-Sleep -Milliseconds 250
            }
            Assert-NotAborted
            if (Test-ProcessIdentity $op.app.pid $op.app.startedAt) {
                Add-History 'app-exit-timeout' "Missum (PID $($op.app.pid)) wird beendet."
                Stop-Tree $op.app.pid $op.app.startedAt
            }
            $state.appStopped = $true
        }
        'drain' {
            $deadline = [DateTime]::UtcNow.AddSeconds([int](Get-Setting 'drainSeconds' 60))
            $state.readinessBefore = Get-Readiness
            do {
                $active = @(Get-OtherActiveRuns)
                if ($active.Count -eq 0) { return }
                Start-Sleep -Seconds 1
            } while ([DateTime]::UtcNow -lt $deadline)
            throw (New-Failure 'selfImprovement.drain_pending' ("Andere Läufe sind noch aktiv: " + (($active | ForEach-Object { "$($_.runId) ($($_.state))" }) -join ', ')) @($active | ForEach-Object { [ordered]@{ code = 'selfImprovement.drain_pending'; detail = "$($_.runId) $($_.state) $($_.mode)" } }))
        }
        'ownership' {
            if (-not [IO.File]::Exists($op.app.executable) -or -not ([IO.Path]::GetFullPath($op.app.executable)).StartsWith(([IO.Path]::GetFullPath($op.app.installDir).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw (New-Failure 'selfImprovement.ownership_mismatch' 'Die Programmdatei liegt nicht im verwalteten Installationsverzeichnis.')
            }
            if (-not $state.changeStack) { return }
            $ids = @(Invoke-Docker @('ps', '-a', '-q', '--filter', ('label=com.docker.compose.project=' + $op.stack.project), '--filter', 'label=com.docker.compose.service=gateway') | Where-Object { $_ })
            if ($ids.Count -ne 1) { throw (New-Failure 'selfImprovement.ownership_mismatch' "Für das Projekt '$($op.stack.project)' wurde kein eindeutiger Gateway-Container gefunden.") }
            $mounts = (Invoke-Docker @('inspect', '--format', '{{json .Mounts}}', $ids[0])) -join '' | ConvertFrom-Json
            $data = @($mounts | Where-Object { $_.Destination -eq '/data' } | Select-Object -First 1)
            $expected = [IO.Path]::GetFullPath((Join-Path $op.stack.dataRoot 'data')).TrimEnd('\')
            if ($data.Count -eq 0 -or (ConvertTo-HostPath ([string]$data[0].Source)) -ne $expected) {
                throw (New-Failure 'selfImprovement.ownership_mismatch' "Der Gateway-Container verwendet nicht das Datenverzeichnis dieser Missum-Installation ($expected).")
            }
            if (-not [IO.File]::Exists($op.stack.envFile)) { throw (New-Failure 'selfImprovement.ownership_mismatch' 'Die Stack-Konfiguration fehlt.') }
        }
        'backup' {
            if ([IO.File]::Exists((Join-Path $backupDir 'manifest.json'))) { return }
            if ([IO.Directory]::Exists($backupDir)) { [IO.Directory]::Delete($backupDir, $true) }
            [IO.Directory]::CreateDirectory($backupDir) | Out-Null
            if ($state.changeStack) { Invoke-Compose @('stop') | Out-Null }
            $manifest = [ordered]@{ createdAt = [DateTimeOffset]::UtcNow.ToString('O'); app = $null; envFile = $null; databases = @(); images = @() }
            if ($state.changeApp) {
                Copy-Tree $op.app.installDir (Join-Path $backupDir 'app')
                $manifest.app = @(Get-ChildItem -LiteralPath (Join-Path $backupDir 'app') -Recurse -File -Force | ForEach-Object {
                    [ordered]@{ path = $_.FullName.Substring((Join-Path $backupDir 'app').Length + 1); sha256 = (Get-FileSha $_.FullName) } })
            }
            if ($state.changeStack) {
                [IO.File]::Copy($op.stack.envFile, (Join-Path $backupDir 'stack.env'), $true)
                $manifest.envFile = Get-FileSha (Join-Path $backupDir 'stack.env')
                foreach ($target in @(Get-ImageTargets | Where-Object { $_.runner })) {
                    $previous = Get-ImageId $target.live
                    if ($null -ne $previous) {
                        $tag = $target.repository + ':rollback-' + $op.opId.ToLowerInvariant()
                        Invoke-Docker @('tag', $previous, $tag) | Out-Null
                        $manifest.images += [ordered]@{ live = $target.live; rollbackTag = $tag; id = $previous }
                    }
                }
            }
            foreach ($set in Get-DatabaseSets) { $manifest.databases += Backup-Database $set }
            $pending = Join-Path $backupDir 'manifest.json.pending'
            [IO.File]::WriteAllText($pending, ($manifest | ConvertTo-Json -Depth 8), $utf8)
            [IO.File]::Move($pending, (Join-Path $backupDir 'manifest.json'))
        }
        'swap-app' {
            if (-not $state.changeApp) { return }
            Set-DirectoryContents $op.candidate.portableDirectory $op.app.installDir
            $changed = Compare-Directory $op.app.installDir (Read-Entries $op.candidate.portableFiles)
            if ($changed.Count -gt 0) { throw (New-Failure 'selfImprovement.app_swap_failed' ("Die installierte App weicht vom Kandidaten ab: " + (($changed | Select-Object -First 5) -join ', '))) }
        }
        'swap-stack' {
            if (-not $state.changeStack) { return }
            foreach ($target in @(Get-ImageTargets | Where-Object { $_.runner })) { Invoke-Docker @('tag', $target.digest, $target.live) | Out-Null }
            Set-ImageVersion $op.candidate.imageVersion
            Invoke-Compose @('up', '-d', '--no-build', '--pull', 'never') | Out-Null
            $images = @{}
            foreach ($target in Get-ImageTargets) { $images[$target.repository + ':' + $op.candidate.imageVersion] = $target.digest }
            foreach ($name in Get-ProjectContainers) {
                $info = (Invoke-Docker @('inspect', '--format', '{{.Config.Image}} {{.Image}}', $name) | Select-Object -First 1).Split(' ')
                if ($images.ContainsKey($info[0]) -and $images[$info[0]] -cne $info[1]) {
                    throw (New-Failure 'selfImprovement.stack_swap_failed' "Container '$name' läuft mit '$($info[1])' statt dem geprüften Digest.")
                }
            }
        }
        'verify-stack' {
            $live = Wait-Gateway ([int](Get-Setting 'gatewaySeconds' 120))
            if ($null -eq $live) { throw (New-Failure 'selfImprovement.gateway_start_failed' 'Das Gateway ist nach der Übernahme nicht erreichbar.') }
            if ($live.protocolVersion -ne '1.0') { throw (New-Failure 'selfImprovement.protocol_mismatch' "Das Gateway meldet Protokoll '$($live.protocolVersion)' statt '1.0'.") }
            $readiness = Get-Readiness
            if ($readiness -eq 'notReady' -and $state.readinessBefore -ne 'notReady') {
                throw (New-Failure 'selfImprovement.gateway_not_ready' 'Das Gateway ist nach der Übernahme nicht betriebsbereit.')
            }
            $run = Invoke-Http ('/v1/runs/' + [Uri]::EscapeDataString($op.serverRunId))
            if ($run.status -ne 200) { throw (New-Failure 'selfImprovement.migration_failed' "Der laufende Auftrag $($op.serverRunId) ist nach der Übernahme nicht lesbar (HTTP $($run.status)).") }
            if ($run.body.state -notin @('WaitingForClient', 'Running', 'Queued')) {
                throw (New-Failure 'selfImprovement.run_not_resumable' "Der Auftrag $($op.serverRunId) ist im Zustand '$($run.body.state)' und kann nicht fortgesetzt werden.")
            }
        }
        'start-app' {
            if (Test-ProcessIdentity $state.newAppPid $state.newAppStartedAt) { return }
            if ([IO.File]::Exists($reattachPath)) { [IO.File]::Delete($reattachPath) }
            try { $started = Start-App } catch { throw (New-Failure 'selfImprovement.app_start_failed' ("Missum konnte nicht gestartet werden: " + $_.Exception.Message)) }
            $state.newAppPid = $started.pid
            $state.newAppStartedAt = $started.startedAt
            # Persist at once: a restarted worker must find this process instead of starting a second app.
            Save-State
        }
        'await-reattach' {
            $deadline = [DateTime]::UtcNow.AddSeconds([int](Get-Setting 'reattachSeconds' 300))
            do {
                if ([IO.File]::Exists($reattachPath)) {
                    $reattach = [IO.File]::ReadAllText($reattachPath, $utf8) | ConvertFrom-Json
                    if ($reattach.opId -cne $op.opId -or $reattach.serverRunId -cne $op.serverRunId -or $reattach.sessionId -ne $op.sessionId) {
                        throw (New-Failure 'selfImprovement.reattach_failed' 'Die Wiederaufnahme meldet einen anderen Auftrag.')
                    }
                    if ($reattach.status -ne 'attached') {
                        throw (New-Failure 'selfImprovement.reattach_failed' ("Die Wiederaufnahme ist fehlgeschlagen: " + [string]$reattach.detail))
                    }
                    return
                }
                if (-not (Test-ProcessIdentity $state.newAppPid $state.newAppStartedAt)) {
                    throw (New-Failure 'selfImprovement.app_exited' 'Missum wurde nach dem Start beendet, bevor der Lauf wieder aufgenommen wurde.')
                }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            throw (New-Failure 'selfImprovement.reattach_timeout' 'Missum hat den Lauf nicht rechtzeitig wieder aufgenommen.')
        }
    }
}

function Complete-Terminal([string] $Status, [string] $Code, [string] $Detail, $Problems) {
    $state.status = $Status
    $state.code = $Code
    $state.detail = $Detail
    if ($null -ne $Problems) { $state.problems = @($Problems) }
    $state.completedAt = [DateTimeOffset]::UtcNow.ToString('O')
    Add-History $Status $Detail
    Save-State
}

# Nothing was changed yet; only an app that already exited is started again.
function Invoke-Reject($Failure) {
    if ($state.appStopped -and -not (Test-ProcessIdentity $state.rollbackAppPid $state.rollbackAppStartedAt)) {
        $started = Start-App
        $state.rollbackAppPid = $started.pid
        $state.rollbackAppStartedAt = $started.startedAt
        Save-State
    }
    Complete-Terminal 'rejected' $Failure.code $Failure.detail $Failure.problems
}

function Invoke-Rollback {
    Invoke-Fault 'rollback'
    Stop-Tree $state.newAppPid $state.newAppStartedAt
    $manifestPath = Join-Path $backupDir 'manifest.json'
    if (-not [IO.File]::Exists($manifestPath)) {
        # The backup never completed, so nothing was swapped: restart what was stopped.
        if ($state.changeStack) { Invoke-Compose @('up', '-d', '--no-build', '--pull', 'never') | Out-Null }
        return 'rejected'
    }
    $manifest = [IO.File]::ReadAllText($manifestPath, $utf8) | ConvertFrom-Json
    if ($state.changeStack) {
        Invoke-Compose @('stop') | Out-Null
        [IO.File]::Copy((Join-Path $backupDir 'stack.env'), $op.stack.envFile, $true)
        foreach ($image in @($manifest.images)) { Invoke-Docker @('tag', $image.id, $image.live) | Out-Null }
    }
    foreach ($entry in @($manifest.databases)) { Restore-Database $entry }
    if ($state.changeApp) {
        Set-DirectoryContents (Join-Path $backupDir 'app') $op.app.installDir
        foreach ($file in @($manifest.app)) {
            if ((Get-FileSha (Join-Path $op.app.installDir $file.path)) -ne $file.sha256) { throw (New-Failure 'selfImprovement.rollback_failed' "App-Datei konnte nicht wiederhergestellt werden: $($file.path)") }
        }
    }
    if ($state.changeStack) {
        Invoke-Compose @('up', '-d', '--no-build', '--pull', 'never') | Out-Null
        if ($null -eq (Wait-Gateway ([int](Get-Setting 'gatewaySeconds' 120)))) { throw (New-Failure 'selfImprovement.rollback_failed' 'Das vorherige Gateway ist nach dem Rollback nicht erreichbar.') }
    }
    return 'rolledBack'
}

function Invoke-Failure($Failure) {
    $state.failure = $Failure
    if ($state.phaseIndex -lt [Array]::IndexOf($Phases, 'backup')) { Invoke-Reject $Failure; return }
    $state.phase = 'rollback'
    Add-History 'rollback' $Failure.detail
    Save-State
    try { $outcome = Invoke-Rollback }
    catch {
        $detail = "Rollback fehlgeschlagen: " + $_.Exception.Message + " Ursache: " + $Failure.detail
        Complete-Terminal 'failed' 'selfImprovement.rollback_failed' $detail (@([ordered]@{ code = $Failure.code; detail = $Failure.detail }, [ordered]@{ code = 'selfImprovement.rollback_failed'; detail = $_.Exception.Message }))
        return
    }
    if (-not (Test-ProcessIdentity $state.rollbackAppPid $state.rollbackAppStartedAt)) {
        try {
            $started = Start-App
            $state.rollbackAppPid = $started.pid
            $state.rollbackAppStartedAt = $started.startedAt
        } catch { Add-History 'rollback-app-start-failed' $_.Exception.Message }
    }
    $problems = if ($null -ne $Failure.problems) { $Failure.problems } else { @([ordered]@{ code = $Failure.code; detail = $Failure.detail }) }
    if ($outcome -eq 'rejected') { Complete-Terminal 'rejected' $Failure.code $Failure.detail $problems }
    else { Complete-Terminal 'rolledBack' $Failure.code ("Übernahme zurückgerollt: " + $Failure.detail) $problems }
}

function ConvertTo-Failure($ErrorRecord) {
    $exception = $ErrorRecord.Exception
    $code = if ($exception.Data.Contains('code')) { [string]$exception.Data['code'] } else { 'selfImprovement.broker_error' }
    $problems = if ($exception.Data.Contains('problems')) { @($exception.Data['problems']) } else { $null }
    return [pscustomobject]@{ code = $code; detail = $exception.Message; problems = $problems }
}

function Invoke-Worker {
    $lockStream = $null
    try { $lockStream = [IO.File]::Open((Join-Path $opDir 'broker.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch { exit 3 }
    try {
        $script:state = Read-State
        if ($null -eq $state) {
            $script:state = [ordered]@{
                opId = $op.opId; candidateId = $op.candidateId; status = 'running'; phase = $Phases[0]; phaseIndex = 0
                code = $null; detail = ''; problems = @(); changeApp = $false; changeStack = $false; appStopped = $false
                readinessBefore = $null; newAppPid = $null; newAppStartedAt = $null; rollbackAppPid = $null; rollbackAppStartedAt = $null
                failure = $null; history = @(); brokerPid = $null; brokerStartedAt = $null; createdAt = [DateTimeOffset]::UtcNow.ToString('O')
                updatedAt = $null; completedAt = $null
            }
        }
        if ($state.status -in $Terminal) { exit 0 }
        $state.brokerPid = $PID
        $state.brokerStartedAt = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('O')
        Add-History 'broker-start' $state.phase
        Save-State

        if ($state.phase -eq 'rollback') { Invoke-Failure $state.failure; exit 0 }
        if ($Abort -ne '') { Invoke-Failure ([pscustomobject]@{ code = 'selfImprovement.broker_crashed'; detail = $Abort; problems = $null }); exit 0 }

        for ($index = [int]$state.phaseIndex; $index -lt $Phases.Count; $index++) {
            $state.phase = $Phases[$index]
            $state.phaseIndex = $index
            Save-State
            try {
                Invoke-Fault $Phases[$index]
                Assert-NotAborted
                Invoke-Phase $Phases[$index]
                Add-History ('done:' + $Phases[$index])
                # A phase's results (for example the started app's identity) are durable before anything else happens.
                Save-State
                Invoke-Fault ('after-' + $Phases[$index])
            }
            catch { Invoke-Failure (ConvertTo-Failure $_); exit 0 }
        }
        $state.phase = 'complete'
        $state.phaseIndex = $Phases.Count
        Complete-Terminal 'applied' $null 'Kandidat übernommen; Missum hat den Lauf wieder aufgenommen.' @()
        exit 0
    } finally {
        if ($null -ne $lockStream) { $lockStream.Dispose() }
    }
}

function Invoke-Supervisor {
    $lockStream = $null
    try { $lockStream = [IO.File]::Open((Join-Path $opDir 'supervisor.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch { exit 0 }
    try {
        $shell = Join-Path $PSHOME 'powershell.exe'
        $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -OperationPath "' + $opPath + '" -Worker'
        $restarts = 0
        while ($true) {
            $current = Read-State
            if ($null -ne $current -and $current.status -in $Terminal) { exit 0 }
            $start = [Diagnostics.ProcessStartInfo]::new($shell, $(if ($restarts -ge 5) { $arguments + ' -Abort "Der Übernahme-Broker ist wiederholt abgestürzt."' } else { $arguments }))
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $worker = [Diagnostics.Process]::Start($start)
            $worker.WaitForExit()
            $code = $worker.ExitCode
            $worker.Dispose()
            if ($code -eq 3) { Start-Sleep -Seconds 1; continue }
            $current = Read-State
            if ($null -ne $current -and $current.status -in $Terminal) { exit 0 }
            if ($restarts -ge 6) { exit 1 }
            $restarts++
        }
    } finally {
        if ($null -ne $lockStream) { $lockStream.Dispose() }
    }
}

$state = $null
if ($Worker) { Invoke-Worker } else { Invoke-Supervisor }

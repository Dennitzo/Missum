# Live check of the SHIPPED integration: adapter and add-on are taken from the
# activated extension store, Blender runs as an ordinary windowed application.
#
#   stage 1 (setup)  powershell -NoProfile -ExecutionPolicy Bypass -File tmp\run-blender-live-test.ps1
#   stage 2 (report) powershell -NoProfile -ExecutionPolicy Bypass -File tmp\run-blender-live-test.ps1 -Stage report -Verdict verified -AnalyzedSha256 <hash> -Summary "<what vision saw>"
#
# Stage 1 leaves Blender open on purpose: the vision verdict has to arrive while
# the very same scene is still bound, exactly like in a real coding run.

param(
    [ValidateSet('setup', 'report')] [string] $Stage = 'setup',
    [ValidateSet('activated', 'repository')] [string] $AddOnFrom = 'activated',
    [string] $Verdict = '',
    [string] $AnalyzedSha256 = '',
    [string] $Summary = ''
)

$ErrorActionPreference = 'Stop'

$repo = 'C:\Users\AMD\Documents\GitHub\Missum'
$blenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$actionId = 'com.missum.blender/edit'
$port = 8765

$live = Join-Path $repo 'tmp\live-test'
$profile = Join-Path $repo 'tmp\live-profile'
$addons = Join-Path $profile 'scripts\addons'
$stateFile = Join-Path $live 'live.json'
$reportFile = Join-Path $live 'ergebnis.json'
$quitFlag = Join-Path $live 'quit.flag'
$guiLog = Join-Path $live 'gui.log'
$blendPath = Join-Path $live 'MissumLiveSzene.blend'
$stateDir = Join-Path $env:TEMP 'missum-program-integrations'

$checks = New-Object System.Collections.ArrayList
function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $mark = if ($Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host ("CHECK {0} {1} :: {2}" -f $mark, $Name, $Detail)
}

function Test-Port {
    param([int]$Port)
    try { $c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $Port); $c.Close(); return $true }
    catch { return $false }
}

function Get-Sha256 {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try { ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '' }
    finally { $stream.Dispose(); $sha.Dispose() }
}

function Invoke-Adapter {
    param([string]$Label, [string]$Tool, [string]$ArgumentsJson)
    $envelope = [ordered]@{
        protocolVersion = 1
        type            = 'action.invoke'
        requestId       = "live-$Label-$(Get-Random)"
        payload         = [ordered]@{
            actionId      = $actionId
            modelToolName = $Tool
            arguments     = ($ArgumentsJson | ConvertFrom-Json)
        }
    }
    $payload = $envelope | ConvertTo-Json -Depth 40 -Compress
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $adapterExe
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload + "`n")
    $stream = $process.StandardInput.BaseStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $stream.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit(300000)) { try { $process.Kill($true) } catch { } }
    $line = ($stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }) | Select-Object -Last 1
    $parsed = $null
    if ($line) { try { $parsed = $line | ConvertFrom-Json } catch { Write-Host "[$Label] unparsable: $line" } }
    if (-not $parsed) { Write-Host "[$Label] no answer. stdout=$stdout stderr=$stderr" }
    else {
        Write-Host "[$Label] type=$($parsed.type) code=$($parsed.payload.code)"
        Write-Host ($line.Substring(0, [Math]::Min(700, $line.Length)))
    }
    return $parsed
}

function Read-StatusFile {
    $p = Join-Path $stateDir 'status.json'
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    $json = Get-Content -LiteralPath $p -Raw
    @($json | ConvertFrom-Json) | Where-Object { $_.actionId -eq $actionId } | Select-Object -First 1
}

# --- activated content from the extension store, not from the build folder ---
$activeRoot = Join-Path $env:LOCALAPPDATA 'Missum\Extensions\active\com.missum.blender'
$content = Get-ChildItem -LiteralPath $activeRoot -Recurse -Directory -Filter 'content' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $content) { Write-Host "kein aktiviertes Paket unter $activeRoot"; exit 2 }
$adapterExe = Join-Path $content.FullName 'adapter\Missum.BlenderAdapter.exe'
# 'activated' proves the shipped store content, 'repository' proves a fix before it is packaged.
if ($AddOnFrom -eq 'repository') {
    $addOnSource = Join-Path $repo 'extensions\blender\missum_blender_bridge'
} else {
    $addOnSource = Join-Path $content.FullName 'addon\missum_blender_bridge'
}
Write-Host ("Add-on-Quelle ($AddOnFrom): " + $addOnSource)
$startedBy = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { 'host' }
Write-Host ("aktivierter Paketinhalt: " + $content.FullName)
Write-Host ("Adapter: " + $adapterExe + ' vorhanden=' + (Test-Path -LiteralPath $adapterExe))
if (-not (Test-Path -LiteralPath $adapterExe) -or -not (Test-Path -LiteralPath $addOnSource)) { exit 2 }

if ($Stage -eq 'setup') {
    Write-Host '=== Stufe 1: echtes Blender-Fenster mit dem Add-on aus dem aktivierten Paket ==='
    Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains('blender-gui-startup.py')
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 600

    if (Test-Path $profile) { Remove-Item -Recurse -Force $profile }
    New-Item -ItemType Directory -Force -Path $addons | Out-Null
    Copy-Item -Recurse -Force $addOnSource (Join-Path $addons 'missum_blender_bridge')
    $copiedFiles = @(Get-ChildItem -LiteralPath (Join-Path $addons 'missum_blender_bridge') -File -Recurse)
    Add-Check 'setup.addon-aus-aktiviertem-paket' ($copiedFiles.Count -ge 2) "dateien=$($copiedFiles.Count) quelle=$addOnSource"

    if (-not (Test-Path $live)) { New-Item -ItemType Directory -Force -Path $live | Out-Null }
    Get-ChildItem -LiteralPath $live -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # keep whatever the previous session left behind, then start from a clean state
    if (Test-Path $stateDir) {
        $before = Join-Path $live ('state-before-' + (Get-Date).ToString('yyyyMMdd-HHmmss'))
        New-Item -ItemType Directory -Force -Path $before | Out-Null
        foreach ($name in @('status.json', 'blender.json')) {
            $p = Join-Path $stateDir $name
            if (Test-Path -LiteralPath $p) { Move-Item -LiteralPath $p -Destination $before -Force }
        }
        Write-Host ("alter Zustand gesichert nach: " + $before)
    }

    $env:BLENDER_USER_SCRIPTS = (Join-Path $profile 'scripts')
    $env:MISSUM_GUI_LOG = $guiLog
    $env:MISSUM_GUI_FLAG = $quitFlag
    $env:MISSUM_GUI_SECONDS = '900'
    $env:MISSUM_BLENDER_PORT = "$port"

    $blenderArgs = @('--factory-startup', '--python', (Join-Path $repo 'tmp\blender-gui-startup.py'))
    $blender = Start-Process -FilePath $blenderExe -ArgumentList $blenderArgs -PassThru `
        -RedirectStandardOutput (Join-Path $live 'blender-out.log') `
        -RedirectStandardError (Join-Path $live 'blender-err.log')

    $ready = $false
    $deadline = (Get-Date).AddSeconds(150)
    while ((Get-Date) -lt $deadline) {
        if (Test-Port $port) { $ready = $true; break }
        if ($blender.HasExited) { break }
        Start-Sleep -Milliseconds 500
    }
    Add-Check 'bridge.port-erreichbar' $ready "port=$port blenderPid=$($blender.Id) beendet=$($blender.HasExited)"
    if (-not $ready) {
        Get-Content (Join-Path $live 'blender-out.log') -ErrorAction SilentlyContinue
        Get-Content (Join-Path $live 'blender-err.log') -ErrorAction SilentlyContinue
        try { $blender.Kill($true) } catch { }
        exit 1
    }

    Write-Host ''
    Write-Host '=== 1) Blender-Bindestatus ==='
    $status = Invoke-Adapter -Label 'status' -Tool 'blender.status' -ArgumentsJson '{}'
    $connected = [bool]$status.payload.result.connected
    $sceneName = $status.payload.result.selected.sceneName
    $instanceId = $status.payload.result.selected.instanceId
    Add-Check 'status.verbunden' ($connected) "connected=$connected szene=$sceneName instanz=$instanceId"

    Write-Host ''
    Write-Host '=== 2) Szene lesen (Werkszene) ==='
    $read = Invoke-Adapter -Label 'read-before' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"summary"}'
    $namesBefore = @($read.payload.result.objects | ForEach-Object { $_.name })
    Add-Check 'scene.read.werkszene-liest' ($namesBefore.Count -gt 0) "objekte=$($namesBefore -join ',')"

    Write-Host ''
    Write-Host '=== 3) echte Aenderungsgruppe, noch ohne Speichern ==='
    $operations = @'
{
  "operationId": "live-pruefung-1",
  "save": false,
  "stopOnError": true,
  "operations": [
    { "op": "object.delete", "name": "Cube" },
    { "op": "object.create", "primitive": "cube", "name": "LiveWuerfel", "location": [-1.1, 0, 0.55] },
    { "op": "object.create", "primitive": "icosphere", "name": "LiveKugel", "location": [1.1, 0, 0.55] },
    { "op": "material.create", "name": "LiveRot", "baseColor": [0.85, 0.07, 0.06, 1], "metallic": 0.1, "roughness": 0.35, "assignTo": ["LiveWuerfel"] },
    { "op": "material.create", "name": "LiveBlau", "baseColor": [0.05, 0.28, 0.85, 1], "metallic": 0.6, "roughness": 0.2, "assignTo": ["LiveKugel"] },
    { "op": "camera.create", "name": "LiveKamera", "location": [5.2, -5.2, 3.2], "lookAt": [0, 0, 0.55], "lens": 42 },
    { "op": "world.set", "color": [0.06, 0.07, 0.09, 1], "strength": 1.15 }
  ]
}
'@
    $edit = Invoke-Adapter -Label 'edit' -Tool 'blender.scene.edit' -ArgumentsJson $operations
    $applied = [int]$edit.payload.result.appliedCount
    Add-Check 'scene.edit.schritte-angewendet' ($applied -eq 7) "appliedCount=$applied code=$($edit.payload.code)"

    Write-Host ''
    Write-Host '=== 4) dieselbe operationId noch einmal (Ledger) ==='
    $again = Invoke-Adapter -Label 'edit-dup' -Tool 'blender.scene.edit' -ArgumentsJson $operations
    $outcome = $again.payload.result.outcome
    $readAfterDup = Invoke-Adapter -Label 'read-after-dup' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"summary"}'
    $countAfterDup = @($readAfterDup.payload.result.objects).Count
    Add-Check 'ledger.doppeltes-angewendet' ($outcome -eq 'already_applied') "outcome=$outcome dedupliert=$($again.payload.result.deduplicated)"
    $expectedNames = @($readAfterDup.payload.result.objects | ForEach-Object { $_.name })
    Add-Check 'ledger.keine-zusatzlichen-objekte' (($countAfterDup -eq ($namesBefore.Count + 2)) -and ($expectedNames -contains 'LiveWuerfel') -and (-not ($expectedNames -contains 'Cube'))) "objekte=$countAfterDup (erwartet $($namesBefore.Count + 2): Wuerfel, Kugel und Kamera dazu, Cube weg) liste=$($expectedNames -join ',')"

    Write-Host ''
    Write-Host '=== 5) Aufnahme aus dem echten Fenster ==='
    $capture = Invoke-Adapter -Label 'capture' -Tool 'blender.view.capture' -ArgumentsJson '{"view":"viewport","width":900,"height":600}'
    $imagePath = $capture.payload.result.imagePath
    $imageOk = $false
    $hash = ''
    if ($imagePath -and (Test-Path $imagePath)) {
        $bytesRead = [System.IO.File]::ReadAllBytes($imagePath)
        $signature = ($bytesRead[0..7] | ForEach-Object { $_.ToString('x2') }) -join ''
        $imageOk = $bytesRead.Length -gt 5000 -and $signature.StartsWith('89504e47')
        $hash = Get-Sha256 -Path $imagePath
    }
    Add-Check 'capture.png-aus-viewport' $imageOk "pfad=$imagePath byte=$($capture.payload.result.bytes) methode=$($capture.payload.result.method)"
    Add-Check 'capture.aus-echter-fensteransicht' ($capture.payload.result.method -eq 'viewport.opengl') "methode=$($capture.payload.result.method) versuch=$($capture.payload.result.viewportAttempt)"
    Add-Check 'capture.kennung-geliefert' ($hash -eq $capture.payload.result.sha256 -and $hash.Length -eq 64) "sha256=$hash"
    if ($imageOk) {
        $pngBytes = [System.IO.File]::ReadAllBytes($imagePath)
        $realWidth = [int]$pngBytes[16] * 16777216 + [int]$pngBytes[17] * 65536 + [int]$pngBytes[18] * 256 + [int]$pngBytes[19]
        $realHeight = [int]$pngBytes[20] * 16777216 + [int]$pngBytes[21] * 65536 + [int]$pngBytes[22] * 256 + [int]$pngBytes[23]
        Add-Check 'capture.groesse-stimmt-mit-bild-ueberein' (([int]$capture.payload.result.width -eq $realWidth) -and ([int]$capture.payload.result.height -eq $realHeight)) "gemeldet=$($capture.payload.result.width)x$($capture.payload.result.height) bild=$realWidth`x$realHeight angefragt=$($capture.payload.result.requestedWidth)x$($capture.payload.result.requestedHeight)"
    }

    $chip = Read-StatusFile
    Add-Check 'chip.sichtpruefung-ausstehend' ($chip.visionState -eq 'pending') "visionState=$($chip.visionState) text=$($chip.visionDetail)"

    $live_state = [ordered]@{
        stage         = 'setup'
        finishedAt    = (Get-Date).ToString('s')
        computer      = $startedBy
        blenderPid    = $blender.Id
        adapterPath   = $adapterExe
        contentPath   = $content.FullName
        port          = $port
        instanceId    = $instanceId
        sceneName     = $sceneName
        operationId   = 'live-pruefung-1'
        capturePath   = $imagePath
        captureSha256 = $hash
        blendPath     = $blendPath
        objects       = $namesBefore
    }
    $live_state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $stateFile -Encoding UTF8

    Write-Host ''
    Write-Host ('Stufe 1: {0} Pruefungen, {1} fehlgeschlagen. Blender bleibt offen (pid {2}).' -f $checks.Count, @($checks | Where-Object { -not $_.passed }).Count, $blender.Id)
    Write-Host ("Vision-Pruefung jetzt mit: -Stage report -Verdict verified -AnalyzedSha256 " + $hash + ' -Summary "..."')
    $checks | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $live 'stufe1.json') -Encoding UTF8
    if (@($checks | Where-Object { -not $_.passed }).Count -gt 0) { exit 1 } else { exit 0 }
}

Write-Host '=== Stufe 2: Sichtpruefung melden und Zustand schliessen ==='
if (-not (Test-Path -LiteralPath $stateFile)) { Write-Host "kein $stateFile - zuerst Stufe 1 ausfuehren"; exit 2 }
$s = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
$opId = $s.operationId
$expectedHash = $s.captureSha256

try {
    Write-Host '--- 1) Urteil mit falscher Bildkennung muss abgewiesen werden ---'
    $wrong = Invoke-Adapter -Label 'report-wrong' -Tool 'blender.status' -ArgumentsJson (@{
        visionReview = @{ operationId = $opId; analyzedSha256 = ('f' * 64); verdict = 'verified'; summary = 'Absichtlich falsche Kennung im Test.' }
    } | ConvertTo-Json -Depth 8 -Compress)
    $wrongMatches = $wrong.payload.result.visionReview.matchesCapture
    $chipAfterWrong = Read-StatusFile
    Add-Check 'vision.falsche-kennung-abgewiesen' ($wrongMatches -eq $false) "matchesCapture=$wrongMatches code=$($wrong.payload.code)"
    Add-Check 'vision.pruefung-bleibt-offen' ($chipAfterWrong.visionState -eq 'pending') "visionState=$($chipAfterWrong.visionState)"

    Write-Host '--- 2) echtes Vision-Urteil mit der richtigen Kennung ---'
    if ($Verdict -notin @('verified', 'deviation', 'unavailable')) {
        Add-Check 'vision.urteil-uebergeben' $false "-Verdict fehlt oder ist ungueltig: '$Verdict'"
        exit 1
    }
    $reviewArgs = @{ operationId = $opId; verdict = $Verdict; summary = $Summary }
    if ($Verdict -ne 'unavailable') { $reviewArgs.analyzedSha256 = $AnalyzedSha256 }
    $report = Invoke-Adapter -Label 'report' -Tool 'blender.status' -ArgumentsJson (@{ visionReview = $reviewArgs } | ConvertTo-Json -Depth 8 -Compress)
    $matches = $report.payload.result.visionReview.matchesCapture
    $expectedReported = if ($Verdict -eq 'unavailable') { $true } else { $AnalyzedSha256 -eq $expectedHash }
    Add-Check 'vision.urteil-zugeordnet' ($matches -eq $true -and $expectedReported) "matchesCapture=$matches erwartet=$expectedReported"

    $chip = Read-StatusFile
    $chipOk = ($chip.visionState -eq $Verdict) -and ($chip.visionDetail -like '*' + $Summary.Substring(0, [Math]::Min(24, $Summary.Length)) + '*')
    Add-Check 'chip.urteil-sichtbar' $chipOk "visionState=$($chip.visionState) text=$($chip.visionDetail)"

    Write-Host '--- 3) Speichern auf dieselbe Datei, keine Sicherung daneben ---'
    $saveJson = @{
        operationId = 'live-pruefung-2'
        save        = $true
        operations  = @(@{ op = 'project.saveAs'; projectFile = $blendPath })
    } | ConvertTo-Json -Depth 8 -Compress
    $save = Invoke-Adapter -Label 'save' -Tool 'blender.scene.edit' -ArgumentsJson $saveJson
    $savedTo = $save.payload.result.saved.savedTo
    $savedOk = ($savedTo -eq $blendPath) -and (Test-Path -LiteralPath $blendPath) -and
        (@($save.payload.result.saved.newBackupFiles).Count -eq 0) -and
        ($save.payload.result.saved.backupPreference.used -eq 0)
    Add-Check 'speichern.auf-dieselbe-blend' $savedOk "savedTo=$savedTo neueSicherungen=$($save.payload.result.saved.newBackupFiles -join ',') used=$($save.payload.result.saved.backupPreference.used) vor=$($save.payload.result.saved.backupPreference.before)"

    $onDisk = @(Get-ChildItem -LiteralPath $live -Filter '*.blend1' -ErrorAction SilentlyContinue)
    Add-Check 'speichern.keine-blend1-auf-der-platte' ($onDisk.Count -eq 0) "blend1-Dateien=$($onDisk.Count)"

    $readFinal = Invoke-Adapter -Label 'read-final' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"summary"}'
    $finalNames = @($readFinal.payload.result.objects | ForEach-Object { $_.name })
    Add-Check 'scene.read.nach-dem-speichern' ((($finalNames -contains 'LiveWuerfel') -and ($finalNames -contains 'LiveKugel') -and (-not ($finalNames -contains 'Cube')))) "objekte=$($finalNames -join ',')"
}
finally {
    Set-Content -LiteralPath $quitFlag -Value 'go'
    $deadline = (Get-Date).AddSeconds(120)
    $closed = $false
    while ((Get-Date) -lt $deadline) {
        $proc = Get-Process -Id $s.blenderPid -ErrorAction SilentlyContinue
        if (-not $proc) { $closed = $true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $closed) { try { (Get-Process -Id $s.blenderPid).Kill($true) } catch { } }
    Add-Check 'fenster.gechlossen' $closed "pid=$($s.blenderPid)"
}

Write-Host ''
Write-Host '--- Protokoll des Add-ons ---'
Get-Content -LiteralPath $guiLog -ErrorAction SilentlyContinue

$failed = @($checks | Where-Object { -not $_.passed })
[ordered]@{ finishedAt = (Get-Date).ToString('s'); stage = 'report'; verdict = $Verdict; checks = $checks } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportFile -Encoding UTF8
Write-Host ''
Write-Host ('Stufe 2: {0} Pruefungen, {1} fehlgeschlagen.' -f $checks.Count, $failed.Count)
if ($failed.Count -gt 0) { exit 1 } else { exit 0 }

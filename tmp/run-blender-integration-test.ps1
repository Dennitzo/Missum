# End-to-end check for the Blender integration: one real Blender process plus
# the real adapter entrypoint, driven call by call.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tmp\run-blender-integration-test.ps1
#
# Proof points: connection, scene read, one real change group saved to the same
# .blend file without a new .blend1, a preview image, duplicate protection,
# scene switch, lost connection. Kept ASCII-only so Windows PowerShell reads it
# with its default encoding.

$ErrorActionPreference = 'Stop'

$repo = 'C:\Users\AMD\Documents\GitHub\Missum'
$exe = Join-Path $repo 'artifacts\blender-integration\adapter\Missum.BlenderAdapter.exe'
$blenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$test = Join-Path $repo 'tmp\test'
$flags = Join-Path $test 'flags'
$blend = Join-Path $test 'MissumTestScene.blend'
$other = Join-Path $test 'MissumOtherScene.blend'
$port = 8765
$actionId = 'com.missum.blender/edit'

# Only harness processes from an earlier aborted run, never a user's Blender.
Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -ErrorAction SilentlyContinue | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains('blender-harness.py')
} | ForEach-Object {
    Write-Host ("closing leftover harness pid {0}" -f $_.ProcessId)
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

if (Test-Path $test) { Remove-Item -Recurse -Force $test }
New-Item -ItemType Directory -Force -Path $flags | Out-Null

$stateDirectory = Join-Path $env:TEMP 'missum-program-integrations'
if (Test-Path $stateDirectory) {
    Remove-Item -Force (Join-Path $stateDirectory 'blender.json') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $stateDirectory 'status.json') -ErrorAction SilentlyContinue
    $captures = Join-Path $stateDirectory 'captures'
    if (Test-Path $captures) { Remove-Item -Recurse -Force $captures }
}

$env:MISSUM_TEST_BLEND = $blend
$env:MISSUM_TEST_BLEND_OTHER = $other
$env:MISSUM_TEST_FLAGS = $flags
$env:MISSUM_TEST_SECONDS = '420'
$env:MISSUM_BLENDER_PORT = "$port"

$checks = New-Object System.Collections.ArrayList

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $mark = if ($Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host ("CHECK {0} {1} :: {2}" -f $mark, $Name, $Detail)
}

function Wait-ForFlag {
    param([string]$Name, [int]$TimeoutSeconds = 120)
    $path = Join-Path $flags $Name
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Invoke-BlenderAdapter {
    param([string]$Label, [string]$Tool, [string]$ArgumentsJson)
    $envelope = [ordered]@{
        protocolVersion = 1
        type            = 'action.invoke'
        requestId       = "test-$Label"
        payload         = [ordered]@{
            actionId      = $actionId
            modelToolName = $Tool
            arguments     = ($ArgumentsJson | ConvertFrom-Json)
        }
    }
    $payload = $envelope | ConvertTo-Json -Depth 40 -Compress

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # Windows PowerShell has no StandardInputEncoding; write UTF-8 bytes directly.
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($psi)
    $requestBytes = [System.Text.Encoding]::UTF8.GetBytes($payload + "`n")
    $inputStream = $process.StandardInput.BaseStream
    $inputStream.Write($requestBytes, 0, $requestBytes.Length)
    $inputStream.Flush()
    $inputStream.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    if (-not $process.WaitForExit(300000)) {
        try { $process.Kill($true) } catch { }
    }

    $line = ($stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }) | Select-Object -Last 1
    if (-not $line) {
        Write-Host "[$Label] no adapter output; stderr=$stderr"
        return $null
    }
    $parsed = $null
    try { $parsed = $line | ConvertFrom-Json } catch { Write-Host "[$Label] unparsable answer: $line"; return $null }
    $code = $parsed.payload.code
    Write-Host "[$Label] type=$($parsed.type) code=$code"
    Write-Host ($line.Substring(0, [Math]::Min(1200, $line.Length)))
    return $parsed
}

Write-Host '=== start Blender (real 5.2, background) ==='
$blenderArgs = @('--background', '--python', (Join-Path $repo 'tmp\blender-harness.py'))
$blender = Start-Process -FilePath $blenderExe -ArgumentList $blenderArgs -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $test 'blender.log') `
    -RedirectStandardError (Join-Path $test 'blender.err.log')

if (-not (Wait-ForFlag 'ready' 180)) {
    Write-Host 'Blender harness never became ready.'
    Get-Content (Join-Path $test 'blender.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $test 'blender.err.log') -ErrorAction SilentlyContinue
    try { $blender.Kill($true) } catch { }
    exit 1
}
Write-Host ("Blender running, pid {0}" -f $blender.Id)

Write-Host ''
Write-Host '=== 1) blender.status (connection) ==='
$status = Invoke-BlenderAdapter -Label 'status' -Tool 'blender.status' -ArgumentsJson '{"port":8765,"portSpan":12}'
$connected = [bool]$status.payload.result.connected
$instanceId = $status.payload.result.selected.instanceId
Add-Check 'status.connected' ($connected -and $status.type -eq 'action.result') "connected=$connected instances=$($status.payload.result.instanceCount)"
Add-Check 'status.instance-data' ($null -ne $instanceId -and $status.payload.result.selected.projectPath -eq $blend) "instance=$instanceId project=$($status.payload.result.selected.projectPath)"

Write-Host ''
Write-Host '=== 2) blender.scene.read ==='
$read = Invoke-BlenderAdapter -Label 'scene-read' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"full","limit":40}'
$objectCount = [int]$read.payload.result.objectCount
Add-Check 'scene.read.objects' ($objectCount -ge 1) "objectCount=$objectCount scene=$($read.payload.result.sceneName)"

Write-Host ''
Write-Host '=== 3) blender.scene.edit (real change group plus save) ==='
$operations = @'
{
  "operationId": "missum-test-aendere-1",
  "save": true,
  "stopOnError": true,
  "operations": [
    { "op": "object.create", "primitive": "cube", "name": "PruefWuerfel", "location": [0, 0, 1], "scale": [2, 1, 0.5] },
    { "op": "object.create", "primitive": "sphere", "name": "PruefKugel", "location": [3, 1, 1] },
    { "op": "object.transform", "name": "PruefWuerfel", "translate": [1, 0, 0], "rotateDegrees": [0, 0, 30] },
    { "op": "material.create", "name": "PruefMaterial", "baseColor": [0.8, 0.2, 0.1, 1], "metallic": 0.4, "roughness": 0.3, "assignTo": ["PruefWuerfel"] },
    { "op": "camera.create", "name": "PruefKamera", "location": [7, -7, 4], "lookAt": [0, 0, 1], "lens": 45 },
    { "op": "world.set", "color": [0.05, 0.06, 0.08, 1], "strength": 1.0 }
  ]
}
'@
$edit = Invoke-BlenderAdapter -Label 'scene-edit' -Tool 'blender.scene.edit' -ArgumentsJson $operations
$applied = [int]$edit.payload.result.appliedCount
$savedTo = $edit.payload.result.saved.savedTo
Add-Check 'scene.edit.steps' ($edit.type -eq 'action.result' -and $applied -eq 6) "appliedCount=$applied"
Add-Check 'scene.edit.same-blend-file' ($savedTo -eq $blend) "savedTo=$savedTo"
Add-Check 'scene.edit.no-new-backup' (@($edit.payload.result.saved.newBackupFiles).Count -eq 0) "newBackupFiles=$($edit.payload.result.saved.newBackupFiles -join ',')"
Add-Check 'scene.edit.backup-setting-restored' ($edit.payload.result.saved.backupPreference.before -eq 1 -and $edit.payload.result.saved.backupPreference.used -eq 0) "before=$($edit.payload.result.saved.backupPreference.before) used=$($edit.payload.result.saved.backupPreference.used)"

$backupFiles = @(Get-ChildItem -Path $test -Filter '*.blend1' -ErrorAction SilentlyContinue)
Add-Check 'files.no-blend1-on-disk' ($backupFiles.Count -eq 0) "blend1 files in test folder: $($backupFiles.Count)"

Write-Host ''
Write-Host '=== 4) blender.custom.run (read the backup setting back inside Blender) ==='
$customCode = @'
{
  "code": "import bpy, os\nd = os.path.dirname(bpy.data.filepath)\nresult = {'saveVersion': int(bpy.context.preferences.filepaths.save_version), 'objects': len(bpy.data.objects), 'backups': sorted(n for n in os.listdir(d) if n.endswith('.blend1'))}"
}
'@
$custom = Invoke-BlenderAdapter -Label 'custom-run' -Tool 'blender.custom.run' -ArgumentsJson $customCode
$saveVersion = $custom.payload.result.pythonResult.saveVersion
Add-Check 'custom.run.backup-value-is-1-again' ($saveVersion -eq 1) "saveVersion=$saveVersion backups=$($custom.payload.result.pythonResult.backups -join ',')"

Write-Host ''
Write-Host '=== 5) blender.view.capture (image output) ==='
$capture = Invoke-BlenderAdapter -Label 'capture' -Tool 'blender.view.capture' -ArgumentsJson '{"view":"viewport","width":640,"height":480}'
$imagePath = $capture.payload.result.imagePath
$imageOk = $false
$signature = ''
if ($imagePath -and (Test-Path $imagePath)) {
    $bytes = [System.IO.File]::ReadAllBytes($imagePath)
    $signature = ($bytes[0..7] | ForEach-Object { $_.ToString('x2') }) -join ''
    $imageOk = $bytes.Length -gt 5000 -and $signature.StartsWith('89504e47')
}
Add-Check 'capture.png' ($imageOk -and $capture.type -eq 'action.result') "path=$imagePath bytes=$($capture.payload.result.bytes) signature=$signature method=$($capture.payload.result.method)"

Write-Host ''
Write-Host '=== 6) same operationId again (duplicate protection) ==='
$duplicate = Invoke-BlenderAdapter -Label 'edit-dup' -Tool 'blender.scene.edit' -ArgumentsJson $operations
$deduplicated = [bool]$duplicate.payload.result.deduplicated
Add-Check 'scene.edit.duplicate-protection' ($duplicate.type -eq 'action.result' -and $deduplicated) "outcome=$($duplicate.payload.result.outcome) applied=$($duplicate.payload.result.appliedCount)"

Write-Host ''
Write-Host '=== 7) scene switch inside Blender (other .blend) ==='
Set-Content -Path (Join-Path $flags 'switch') -Value 'go'
if (-not (Wait-ForFlag 'switched' 120)) { Write-Host 'Scene switch was not confirmed.' }
$statusAfterSwitch = Invoke-BlenderAdapter -Label 'status-after-switch' -Tool 'blender.status' -ArgumentsJson '{}'
$rebindRequired = [bool]$statusAfterSwitch.payload.result.rebindRequired
Add-Check 'switch.rebind-required' ($rebindRequired -and -not [bool]$statusAfterSwitch.payload.result.connected) "rebindRequired=$rebindRequired connected=$($statusAfterSwitch.payload.result.connected)"

$editAfterSwitch = Invoke-BlenderAdapter -Label 'edit-after-switch' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"missum-test-aendere-2","operations":[{"op":"object.create","primitive":"cube","name":"InAndereSzene"}]}'
Add-Check 'switch.edit-blocked' ($editAfterSwitch.type -eq 'host.error' -and $editAfterSwitch.payload.code -eq 'blender.rebind_required') "code=$($editAfterSwitch.payload.code)"

Write-Host ''
Write-Host '=== 8) rebind explicitly and work in the other file ==='
$rebindArguments = '{"instanceId":"' + $instanceId + '"}'
$rebind = Invoke-BlenderAdapter -Label 'rebind' -Tool 'blender.status' -ArgumentsJson $rebindArguments
$reboundPath = $rebind.payload.result.selected.projectPath
Add-Check 'rebind.other-project-path' ([bool]$rebind.payload.result.connected -and $reboundPath -eq $other) "projectPath=$reboundPath"

$editOther = Invoke-BlenderAdapter -Label 'edit-other' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"missum-test-aendere-3","save":true,"operations":[{"op":"object.create","primitive":"cone","name":"KegelInAndererSzene","location":[0,0,2]}]}'
Add-Check 'rebind.edit-went-to-other-file' ($editOther.payload.result.saved.savedTo -eq $other -and [int]$editOther.payload.result.appliedCount -eq 1) "savedTo=$($editOther.payload.result.saved.savedTo)"

$readOther = Invoke-BlenderAdapter -Label 'read-other' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"summary"}'
$namesInOther = @($readOther.payload.result.objects | ForEach-Object { $_.name })
Add-Check 'rebind.old-objects-not-in-new-scene' (-not ($namesInOther -contains 'PruefWuerfel') -and ($namesInOther -contains 'KegelInAndererSzene')) "objects=$($namesInOther -join ',')"

Write-Host ''
Write-Host '=== 9) lost connection ==='
Set-Content -Path (Join-Path $flags 'stop') -Value 'go'
if (-not (Wait-ForFlag 'stopped' 60)) { Write-Host 'Stop was not confirmed.' }
Start-Sleep -Seconds 2
$statusLost = Invoke-BlenderAdapter -Label 'status-lost' -Tool 'blender.status' -ArgumentsJson '{}'
Add-Check 'lost.status-reports-disconnected' (-not [bool]$statusLost.payload.result.connected) "connected=$($statusLost.payload.result.connected) instances=$($statusLost.payload.result.instanceCount)"

$editLost = Invoke-BlenderAdapter -Label 'edit-lost' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"missum-test-aendere-4","operations":[{"op":"object.create","primitive":"cube","name":"NachTrennung"}]}'
# Either code is correct: nothing may be changed without a live, confirmed target.
$lostCode = $editLost.payload.code
Add-Check 'lost.edit-refused' ($editLost.type -eq 'host.error' -and @('blender.not_connected', 'blender.rebind_required') -contains $lostCode) "code=$lostCode"

$captureLost = Invoke-BlenderAdapter -Label 'capture-lost' -Tool 'blender.view.capture' -ArgumentsJson '{}'
Add-Check 'lost.capture-refused' ($captureLost.type -eq 'host.error') "code=$($captureLost.payload.code)"

Write-Host ''
Write-Host '=== finish ==='
Set-Content -Path (Join-Path $flags 'quit') -Value 'go'
if (-not $blender.WaitForExit(60000)) { try { $blender.Kill($true) } catch { } }

$report = [ordered]@{
    finishedAt = (Get-Date).ToString('s')
    blender    = $blenderExe
    adapter    = $exe
    project    = $blend
    other      = $other
    checks     = $checks
}
$reportPath = Join-Path $repo 'tmp\test\ergebnis.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8

$failed = @($checks | Where-Object { -not $_.passed })
Write-Host ''
Write-Host ("{0} checks, {1} failed." -f $checks.Count, $failed.Count)
Write-Host "report: $reportPath"
Write-Host '--- blender log (last lines) ---'
Get-Content (Join-Path $test 'blender.log') -Tail 20 -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) { exit 1 } else { exit 0 }

# Shipping-path check: Blender as a normal windowed app with the add-on enabled
# the ordinary way. No test pump here - only bpy.app.timers serves the bridge.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tmp\run-blender-gui-test.ps1
#
# Blender opens for a moment in a private user-scripts profile so the user's own
# configuration is untouched.

$ErrorActionPreference = 'Stop'

$repo = 'C:\Users\AMD\Documents\GitHub\Missum'
$exe = Join-Path $repo 'artifacts\blender-integration\adapter\Missum.BlenderAdapter.exe'
$blenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$profile = Join-Path $repo 'tmp\gui-profile'
$scripts = Join-Path $profile 'scripts'
$addons = Join-Path $scripts 'addons'
$test = Join-Path $repo 'tmp\gui-test'
$guiBlend = Join-Path $test 'MissumGuiScene.blend'
$actionId = 'com.missum.blender/edit'

Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -ErrorAction SilentlyContinue | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains('blender-gui-startup.py')
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

if (Test-Path $profile) { Remove-Item -Recurse -Force $profile }
New-Item -ItemType Directory -Force -Path $addons | Out-Null
Copy-Item -Recurse -Force (Join-Path $repo 'extensions\blender\missum_blender_bridge') (Join-Path $addons 'missum_blender_bridge')
if (Test-Path $test) { Remove-Item -Recurse -Force $test }
New-Item -ItemType Directory -Force -Path $test | Out-Null

$stateDirectory = Join-Path $env:TEMP 'missum-program-integrations'
if (Test-Path $stateDirectory) {
    Remove-Item -Force (Join-Path $stateDirectory 'blender.json') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $stateDirectory 'status.json') -ErrorAction SilentlyContinue
}

$env:BLENDER_USER_SCRIPTS = $scripts
$env:MISSUM_GUI_LOG = Join-Path $test 'gui.log'
$env:MISSUM_GUI_FLAG = Join-Path $test 'quit.flag'
$env:MISSUM_GUI_SECONDS = '150'
$env:MISSUM_BLENDER_PORT = '8765'

$checks = New-Object System.Collections.ArrayList

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $mark = if ($Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host ("CHECK {0} {1} :: {2}" -f $mark, $Name, $Detail)
}

function Test-Port {
    param([int]$Port)
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $Port)
        $client.Close()
        return $true
    } catch { return $false }
}

function Invoke-BlenderAdapter {
    param([string]$Label, [string]$Tool, [string]$ArgumentsJson)
    $envelope = [ordered]@{
        protocolVersion = 1
        type            = 'action.invoke'
        requestId       = "gui-$Label"
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
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $requestBytes = [System.Text.Encoding]::UTF8.GetBytes($payload + "`n")
    $stream = $process.StandardInput.BaseStream
    $stream.Write($requestBytes, 0, $requestBytes.Length)
    $stream.Flush()
    $stream.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    if (-not $process.WaitForExit(300000)) { try { $process.Kill($true) } catch { } }
    $line = ($stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }) | Select-Object -Last 1
    $parsed = $null
    if ($line) { try { $parsed = $line | ConvertFrom-Json } catch { Write-Host "[$Label] unparsable: $line" } }
    Write-Host "[$Label] type=$($parsed.type) code=$($parsed.payload.code)"
    if ($line) { Write-Host ($line.Substring(0, [Math]::Min(900, $line.Length))) }
    return $parsed
}

Write-Host '=== Blender windowed, add-on enabled normally, only timers drive the bridge ==='
$blenderArgs = @('--factory-startup', '--python', (Join-Path $repo 'tmp\blender-gui-startup.py'))
$blender = Start-Process -FilePath $blenderExe -ArgumentList $blenderArgs -PassThru `
    -RedirectStandardOutput (Join-Path $test 'blender-gui-out.log') `
    -RedirectStandardError (Join-Path $test 'blender-gui-err.log')

$ready = $false
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    if (Test-Port 8765) { $ready = $true; break }
    if ($blender.HasExited) { break }
    Start-Sleep -Milliseconds 500
}
Add-Check 'gui.bridge-laeuft-ueber-timer' $ready "port 8765 erreichbar=$ready blenderExited=$($blender.HasExited)"
if (-not $ready) {
    Get-Content (Join-Path $test 'blender-gui-out.log') -ErrorAction SilentlyContinue
    Get-Content (Join-Path $test 'blender-gui-err.log') -ErrorAction SilentlyContinue
    try { $blender.Kill($true) } catch { }
    exit 1
}

Write-Host ''
Write-Host '=== 1) status over the timer-driven bridge ==='
$status = Invoke-BlenderAdapter -Label 'status' -Tool 'blender.status' -ArgumentsJson '{}'
Add-Check 'gui.status-verbunden' ([bool]$status.payload.result.connected) "connected=$($status.payload.result.connected) szene=$($status.payload.result.selected.sceneName)"

Write-Host ''
Write-Host '=== 2) change the windowed scene ==='
$edit = Invoke-BlenderAdapter -Label 'edit' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"missum-gui-1","operations":[{"op":"object.create","primitive":"torus","name":"GuiRing","location":[0,0,1.2]},{"op":"material.create","name":"GuiMaterial","baseColor":[0.1,0.5,0.9,1],"metallic":0.2,"roughness":0.4,"assignTo":["GuiRing"]}]}'
Add-Check 'gui.aenderung-angewendet' ([int]$edit.payload.result.appliedCount -eq 2) "appliedCount=$($edit.payload.result.appliedCount) neue=$($edit.payload.result.newObjects -join ',')"

Write-Host ''
Write-Host '=== 3) viewport capture in the real window ==='
$capture = Invoke-BlenderAdapter -Label 'capture' -Tool 'blender.view.capture' -ArgumentsJson '{"view":"viewport","width":800,"height":600}'
$imagePath = $capture.payload.result.imagePath
$imageOk = $false
$signature = ''
if ($imagePath -and (Test-Path $imagePath)) {
    $bytes = [System.IO.File]::ReadAllBytes($imagePath)
    $signature = ($bytes[0..7] | ForEach-Object { $_.ToString('x2') }) -join ''
    $imageOk = $bytes.Length -gt 5000 -and $signature.StartsWith('89504e47')
}
Add-Check 'gui.aufnahme-aus-viewport' ($imageOk) "pfad=$imagePath byte=$($capture.payload.result.bytes) methode=$($capture.payload.result.method) signatur=$signature"

Write-Host ''
Write-Host '=== 4) save an unsaved scene must be refused, changes stay applied ==='
$saveWithoutPath = Invoke-BlenderAdapter -Label 'save-no-path' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"missum-gui-2","save":true,"operations":[{"op":"object.create","primitive":"plane","name":"GuiBoden"}]}'
$refused = $saveWithoutPath.payload.result.saved.saved -eq $false
Add-Check 'gui.speichern-ohne-pfad-hinweis' ($refused -and [int]$saveWithoutPath.payload.result.appliedCount -eq 1) "saved=$($saveWithoutPath.payload.result.saved.saved) code=$($saveWithoutPath.payload.result.saved.errorCode) text=$($saveWithoutPath.payload.result.saved.error)"

Write-Host ''
Write-Host '=== 5) set the project path and store on that file (nested save verdict) ==='
$saveAsJson = '{"operationId":"missum-gui-3","operations":[{"op":"project.saveAs","projectFile":"C:\\Users\\AMD\\Documents\\GitHub\\Missum\\tmp\\gui-test\\MissumGuiScene.blend"}]}'
$saveAs = Invoke-BlenderAdapter -Label 'save-as' -Tool 'blender.scene.edit' -ArgumentsJson $saveAsJson
Add-Check 'gui.projektpfad-gesetzt' ($saveAs.payload.result.saved.savedTo -eq $guiBlend -and (Test-Path $guiBlend)) "savedTo=$($saveAs.payload.result.saved.savedTo)"

$backups = @(Get-ChildItem -Path $test -Filter '*.blend1' -ErrorAction SilentlyContinue)
Add-Check 'gui.keine-blend1' ($backups.Count -eq 0) "blend1-Dateien: $($backups.Count)"

$readAfter = Invoke-BlenderAdapter -Label 'read-after' -Tool 'blender.scene.read' -ArgumentsJson '{"detail":"summary"}'
$names = @($readAfter.payload.result.objects | ForEach-Object { $_.name })
Add-Check 'gui.objekte-in-der-offenen-szene' (($names -contains 'GuiRing') -and ($names -contains 'GuiBoden')) "objekte=$($names -join ',')"

Write-Host ''
Write-Host '=== finish ==='
Set-Content -Path (Join-Path $test 'quit.flag') -Value 'go'
if (-not $blender.WaitForExit(90000)) { try { $blender.Kill($true) } catch { } }

$report = [ordered]@{ finishedAt = (Get-Date).ToString('s'); mode = 'windowed blender, timer driven'; checks = $checks }
$reportPath = Join-Path $test 'ergebnis.json'
$report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8

$failed = @($checks | Where-Object { -not $_.passed })
Write-Host ''
Write-Host ("{0} checks, {1} failed." -f $checks.Count, $failed.Count)
Write-Host '--- gui log ---'
Get-Content $env:MISSUM_GUI_LOG -ErrorAction SilentlyContinue
if ($failed.Count -gt 0) { exit 1 } else { exit 0 }

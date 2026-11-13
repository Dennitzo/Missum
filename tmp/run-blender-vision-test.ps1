# Vision loop check for the Blender integration, driven against the PACKAGED adapter
# (artifacts\blender-integration\adapter) and one real Blender instance.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tmp\run-blender-vision-test.ps1
#
# Proves: a capture stays "pending" until a verdict for exactly that picture arrives,
# a verdict about another picture does not close it, an unavailable verdict keeps the
# state honest, and a missing finding is refused. Nothing retries by itself.
# Kept ASCII-only so Windows PowerShell reads it with its default encoding.

$ErrorActionPreference = 'Stop'

$repo = 'C:\Users\AMD\Documents\GitHub\Missum'
$exe = Join-Path $repo 'artifacts\blender-integration\adapter\Missum.BlenderAdapter.exe'
$blenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$test = Join-Path $repo 'tmp\test-vision'
$flags = Join-Path $test 'flags'
$blend = Join-Path $test 'MissumVisionScene.blend'
$other = Join-Path $test 'MissumVisionOther.blend'
$actionId = 'com.missum.blender/edit'

if (-not (Test-Path $exe)) { throw "Gepackter Adapter fehlt: $exe" }

Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -ErrorAction SilentlyContinue | Where-Object {
    $_.CommandLine -and $_.CommandLine.Contains('blender-harness.py')
} | ForEach-Object {
    Write-Host ("closing leftover harness pid {0}" -f $_.ProcessId)
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

if (Test-Path $test) { Remove-Item -Recurse -Force $test }
New-Item -ItemType Directory -Force -Path $flags | Out-Null

$stateDirectory = Join-Path $env:TEMP 'missum-program-integrations'
$statusFile = Join-Path $stateDirectory 'status.json'
$bindingFile = Join-Path $stateDirectory 'blender.json'
if (Test-Path $stateDirectory) {
    Remove-Item -Force $statusFile -ErrorAction SilentlyContinue
    Remove-Item -Force $bindingFile -ErrorAction SilentlyContinue
    $captures = Join-Path $stateDirectory 'captures'
    if (Test-Path $captures) { Remove-Item -Recurse -Force $captures }
}

$env:MISSUM_TEST_BLEND = $blend
$env:MISSUM_TEST_BLEND_OTHER = $other
$env:MISSUM_TEST_FLAGS = $flags
$env:MISSUM_TEST_SECONDS = '300'
$env:MISSUM_BLENDER_PORT = '8765'

$checks = New-Object System.Collections.ArrayList

function Add-Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$checks.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $mark = if ($Passed) { 'OK  ' } else { 'FAIL' }
    Write-Host ("CHECK {0} {1} :: {2}" -f $mark, $Name, $Detail)
}

function Wait-ForFlag {
    param([string]$Name, [int]$TimeoutSeconds = 180)
    $path = Join-Path $flags $Name
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $path) { return $true }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
}

function BlenderStatus {
    $status = Read-JsonFile $statusFile
    if ($null -eq $status) { return $null }
    return $status | Where-Object { $_.actionId -eq $actionId } | Select-Object -First 1
}

function Invoke-BlenderAdapter {
    param([string]$Label, [string]$Tool, [string]$ArgumentsJson)
    $envelope = [ordered]@{
        protocolVersion = 1
        type            = 'action.invoke'
        requestId       = "vision-$Label"
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
    Write-Host ("[{0}] type={1} code={2}" -f $Label, $parsed.type, $parsed.payload.code)
    return $parsed
}

Write-Host '=== start Blender (real 5.2, background) ==='
$blender = Start-Process -FilePath $blenderExe -ArgumentList @('--background', '--python', (Join-Path $repo 'tmp\blender-harness.py')) -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $test 'blender.log') `
    -RedirectStandardError (Join-Path $test 'blender.err.log')

if (-not (Wait-ForFlag 'ready' 180)) {
    Write-Host 'Blender harness never became ready.'
    Get-Content (Join-Path $test 'blender.log') -ErrorAction SilentlyContinue
    try { $blender.Kill($true) } catch { }
    exit 1
}

try {
    Write-Host ''
    Write-Host '=== 1) connect ==='
    $status = Invoke-BlenderAdapter -Label 'connect' -Tool 'blender.status' -ArgumentsJson '{}'
    $connected = [bool]$status.payload.result.connected
    Add-Check 'vision.verbunden' ($connected -and $status.type -eq 'action.result') "connected=$connected"

    Write-Host ''
    Write-Host '=== 2) capture leaves the review open ==='
    $capture = Invoke-BlenderAdapter -Label 'capture' -Tool 'blender.view.capture' -ArgumentsJson '{"view":"viewport","engine":"workbench","width":640,"height":360}'
    $imagePath = [string]$capture.payload.result.imagePath
    $sha = [string]$capture.payload.result.sha256
    $imageExists = (Test-Path $imagePath) -and ((Get-Item $imagePath).Length -gt 1000)
    $shaLooksRight = $sha -match '^[0-9a-f]{64}$'
    $imageBytes = 0
    if ($imageExists) { $imageBytes = (Get-Item $imagePath).Length }
    Add-Check 'vision.aufnahme-vorhanden' ($imageExists -and $imageBytes -gt 0) "image=$imagePath bytes=$imageBytes"
    Add-Check 'vision.bild-Hash' $shaLooksRight "sha256=$sha"

    $state = Read-JsonFile $bindingFile
    $pendingPath = [string]$state.visionPending.capturePath
    $pendingSha = [string]$state.visionPending.sha256
    Add-Check 'vision.offen-im-protokoll' ($pendingPath -eq $imagePath -and $pendingSha -eq $sha) "pending=$pendingPath"

    $chip = BlenderStatus
    Add-Check 'vision.chip-ausstehend' ($chip.visionState -eq 'pending') "visionState=$($chip.visionState) detail=$($chip.visionDetail)"

    Write-Host ''
    Write-Host '=== 3) verdict about another picture does not close the review ==='
    $wrong = Invoke-BlenderAdapter -Label 'review-falsch' -Tool 'blender.status' -ArgumentsJson ('{"visionReview":{"analyzedSha256":"' + ('a' * 64) + '","verdict":"verified","summary":"anderes Bild"}}')
    $matches = [bool]$wrong.payload.result.visionReview.matchesCapture
    $chip = BlenderStatus
    Add-Check 'vision fremdes-bild-abgewiesen' (-not $matches -and $chip.visionState -eq 'pending') "matchesCapture=$matches visionState=$($chip.visionState)"

    Write-Host ''
    Write-Host '=== 4) verdict for this picture closes the review ==='
    $right = Invoke-BlenderAdapter -Label 'review-richtig' -Tool 'blender.status' -ArgumentsJson ('{"visionReview":{"analyzedSha256":"' + $sha + '","verdict":"verified","summary":"Wuerfel und Kugel sichtbar, Kamera passt"}}')
    $matchesRight = [bool]$right.payload.result.visionReview.matchesCapture
    $verdict = [string]$right.payload.result.visionReview.verdict
    $chip = BlenderStatus
    $state = Read-JsonFile $bindingFile
    Add-Check 'vision.zugeordnet' ($matchesRight -and $verdict -eq 'verified') "matchesCapture=$matchesRight verdict=$verdict"
    Add-Check 'vision.chip-geprueft' ($chip.visionState -eq 'verified' -and $chip.visionDetail -like '*Wuerfel*') "visionState=$($chip.visionState) detail=$($chip.visionDetail)"
    Add-Check 'vision.nichts-offen' ($null -eq $state.visionPending) "visionPending=$($state.visionPending)"

    Write-Host ''
    Write-Host '=== 5) second capture plus unavailable verdict stays honest ==='
    $capture2 = Invoke-BlenderAdapter -Label 'capture2' -Tool 'blender.view.capture' -ArgumentsJson '{"view":"viewport","width":480,"height":270}'
    $sha2 = [string]$capture2.payload.result.sha256
    $chip = BlenderStatus
    Add-Check 'vision.wieder-offen' ($chip.visionState -eq 'pending') "visionState=$($chip.visionState)"

    # A change while a review is open is reported as a hint, but the open review is not retried here.
    $edit = Invoke-BlenderAdapter -Label 'edit-mit-offener-pruefung' -Tool 'blender.scene.edit' -ArgumentsJson '{"operationId":"vision-test-aenderung","operations":[{"op":"object.create","primitive":"cylinder","name":"VisionZylinder","location":[0,3,0]}]}'
    $hintState = [string]$edit.payload.result.pendingVisionReview.state
    Add-Check 'vision.hinweis-bleibt' ($hintState -eq 'pending') "pendingVisionReview.state=$hintState"

    $unavailable = Invoke-BlenderAdapter -Label 'review-unverfuegbar' -Tool 'blender.status' -ArgumentsJson ('{"visionReview":{"analyzedSha256":"' + $sha2 + '","verdict":"unavailable","summary":"Vision war in diesem Lauf nicht erreichbar"}}')
    $chip = BlenderStatus
    Add-Check 'vision.ausstehend-nach-ausfall' ($chip.visionState -eq 'unavailable' -and $chip.visionDetail -like '*ausstehend*') "visionState=$($chip.visionState) detail=$($chip.visionDetail)"
    Add-Check 'vision.keine-wiederholung' ($unavailable.type -eq 'action.result') "type=$($unavailable.type)"

    Write-Host ''
    Write-Host '=== 6) unusable reports are refused ==='
    $noSummary = Invoke-BlenderAdapter -Label 'ohne-befund' -Tool 'blender.status' -ArgumentsJson '{"visionReview":{"verdict":"verified"}}'
    Add-Check 'vision.befund-pflicht' ($noSummary.type -eq 'host.error' -and $noSummary.payload.code -eq 'blender.vision_summary_missing') "code=$($noSummary.payload.code)"

    $badVerdict = Invoke-BlenderAdapter -Label 'falsches-urteil' -Tool 'blender.status' -ArgumentsJson '{"visionReview":{"verdict":"vielleicht","summary":"unklar"}}'
    Add-Check 'vision.urteil-werte' ($badVerdict.type -eq 'host.error' -and $badVerdict.payload.code -eq 'blender.vision_verdict_invalid') "code=$($badVerdict.payload.code)"
}
finally {
    try { if (-not $blender.HasExited) { $blender.Kill($true) } } catch { }
}

Write-Host ''
$failed = @($checks | Where-Object { -not $_.passed })
Write-Host ("{0} of {1} checks passed" -f (@($checks).Count - $failed.Count), @($checks).Count)
foreach ($failure in $failed) { Write-Host ("OFFEN {0} :: {1}" -f $failure.name, $failure.detail) }
if ($failed.Count -eq 0) { exit 0 } else { exit 1 }

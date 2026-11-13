<# Realfall A: Blender wurde von Hand ohne Add-on geöffnet. Missum muss es finden, zeigen und
   Änderungen sauber ablehnen, ohne die geöffnete Datei anzufassen. #>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = 'C:\Users\AMD\Documents\GitHub\Missum'
$adapter = Join-Path $repoRoot 'src\Missum.BlenderAdapter\bin\Debug\net10.0\Missum.BlenderAdapter.exe'
$blender = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$workspace = 'C:\Users\AMD\Documents\GitHub\Blender'
$blendFile = Join-Path $workspace 'Untitled.blend'

$failures = new-object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$extra = '') {
  $mark = if ($ok) { 'PASS' } else { 'FAIL' }
  Write-Host "$mark  $name $extra"
  if (-not $ok) { $failures.Add($name) }
}

function Get-BlendHash([string]$path) {
  (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

function Invoke-Adapter([string]$tool, [hashtable]$arguments) {
  $payload = @{
    protocolVersion = 1
    type = 'action.invoke'
    requestId = [guid]::NewGuid().ToString('N')
    payload = @{
      modelToolName = $tool
      actionId = 'com.missum.blender/edit'
      arguments = $arguments
    }
  } | ConvertTo-Json -Depth 10 -Compress
  $env:ASSISTANT_EXTENSION_WORKSPACE_ROOT = $workspace
  $payload | & $adapter 2>&1
}

function Parse-Adapter([string]$raw, [ref]$parsed) {
  $parsed.Value = $raw | ConvertFrom-Json
}

function Is-Error([string]$raw, [string]$code) {
  $parsed = $raw | ConvertFrom-Json
  if ($parsed.type -ne 'host.error') { return $false }
  return $parsed.payload.code -eq $code
}

Write-Host "=== Realfall A: von Hand geöffnetes Blender ohne Add-on ==="
Write-Host "Adapter: $adapter"
Write-Host "Workspace: $workspace"

if (-not (Test-Path $adapter)) { Write-Host "FAIL  adapter nicht gefunden"; exit 1 }
if (-not (Test-Path $blender)) { Write-Host "FAIL  blender.exe nicht gefunden"; exit 1 }
if (-not (Test-Path $blendFile)) { Write-Host "FAIL  Testdatei nicht gefunden"; exit 1 }

$beforeHash = Get-BlendHash $blendFile
Write-Host "Hash vor dem Lauf: $beforeHash"

# 1) Blender von Hand öffnen, ohne jedes Missum-Environment und ohne --python.
$proc = [System.Diagnostics.Process]::Start($blender, $blendFile)
$opened = $false
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Milliseconds 500
  try {
    if (-not $proc.HasExited -and $proc.MainWindowHandle -ne 0 -and $proc.MainWindowTitle -match 'blend') {
      $opened = $true
      break
    }
  } catch {}
}
Check "Blender-Fenster öffnet sich innerhalb von 30 Sekunden" $opened "PID=$($proc.Id) Titel='$($proc.MainWindowTitle)'"
if (-not $opened) { $proc.Kill(); exit 1 }

try {
  # 2) blender.status darf nichts starten, sondern nur beobachten.
  $raw = Invoke-Adapter 'blender.status' @{ connect = $false }
  $parsed = $raw | ConvertFrom-Json
  $result = $parsed.payload.result
  Check "Status ist kein Fehler" ($parsed.type -eq 'action.result') $raw.Substring(0, [Math]::Min(200, $raw.Length))
  Check "Gefundene Instanzen >= 1" ($result.instanceCount -ge 1) "instanceCount=$($result.instanceCount)"
  Check "Keine steuerbare Instanz" ($result.controllableCount -eq 0) "controllableCount=$($result.controllableCount)"
  $windowInstance = $result.instances | Where-Object { $_.detection -eq 'window' -and $_.controllable -eq $false } | Select-Object -First 1
  Check "Gefundenes Fenster trägt die Prozesskennung" ($null -ne $windowInstance -and $windowInstance.processId -eq $proc.Id) "instanceId=$($windowInstance.instanceId) windowTitle=$($windowInstance.windowTitle)"
  Check "Fenstertitel nennt Blender" ($windowInstance.windowTitle -match 'Blender') "windowTitle='$($windowInstance.windowTitle)'"
  Check "Kein erfundener Projektpfad" ($windowInstance.projectName -eq 'ungespeicherte Szene') "projectName=$($windowInstance.projectName)"

  $instanceId = $windowInstance.instanceId

  # 3) Szenenänderung muss sauber abgelehnt werden, ohne die Datei anzufassen.
  $editRaw = Invoke-Adapter 'blender.scene.edit' @{
    operationId = 'check-edit-rejected'
    operations = @(@{ op = 'object.create'; primitive = 'cube'; name = 'Prüfwürfel' })
    save = $false
  }
  $editRejected = Is-Error $editRaw 'blender.not_controllable'
  Check "Szenenänderung wird abgelehnt (blender.not_controllable)" $editRejected $editRaw.Substring(0, [Math]::Min(200, $editRaw.Length))

  # 4) Fensterbild des offenen Fensters.
  $capRaw = Invoke-Adapter 'blender.view.capture' @{ instanceId = $instanceId }
  $capParsed = $capRaw | ConvertFrom-Json
  $capOk = $capParsed.type -eq 'action.result'
  Check "Fensterbild-Aufnahme liefert Ergebnis" $capOk $capRaw.Substring(0, [Math]::Min(200, $capRaw.Length))
  if ($capOk) {
    $cap = $capParsed.payload.result
    Check "Bilddatei existiert und ist nicht leer" ((Test-Path $cap.imagePath) -and (Get-Item $cap.imagePath).Length -gt 0) "imagePath=$($cap.imagePath) bytes=$($cap.bytes)"
    Check "Prüfsumme liegt vor" ([string]::IsNullOrWhiteSpace($cap.sha256) -eq $false) "sha256=$($cap.sha256)"
    Check "Aufnahme kommt vom Fenster, nicht aus einer Szene" ($cap.detection -eq 'window' -and $cap.liveScene -eq $false) "detection=$($cap.detection) liveScene=$($cap.liveScene)"
    Check "Bild hat eine brauchbare Groesse" ($cap.width -ge 800 -and $cap.height -ge 500) "width=$($cap.width) height=$($cap.height)"

    # Ein echtes Fensterbild zeigt im Mittelbereich viele verschiedene Farbwerte, ein leeres nicht.
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap($cap.imagePath)
    $seen = @{}
    $x0 = [int]($bmp.Width / 4); $x1 = [int]($bmp.Width * 3 / 4)
    $y0 = [int]($bmp.Height / 4); $y1 = [int]($bmp.Height * 3 / 4)
    for ($y = $y0; $y -lt $y1; $y += 3) {
      for ($x = $x0; $x -lt $x1; $x += 3) {
        $c = $bmp.GetPixel($x, $y)
        $seen[[int64]$c.ToArgb()] = 1
      }
    }
    $bmp.Dispose()
    Check "Bildinhalt ist kein Leerbild" ($seen.Count -ge 12) "gemessen=$($seen.Count) contentColors=$($cap.contentColors)"
    Check "Aufnahme meldet ihren Inhalt ehrlich" ($cap.observation -notmatch 'praktisch keinen Inhalt') "observation=$($cap.observation)"
    $captureSha = $cap.sha256
    $captureOp = $cap.operationId

    # 5) Pflicht-Vision: Urteil zum Fensterbild muss zugeordnet werden können.
    $visRaw = Invoke-Adapter 'blender.status' @{
      visionReview = @{
        operationId = $captureOp
        analyzedSha256 = $captureSha
        verdict = 'verified'
        summary = 'Prüfbild des offenen Fensters zeigt den Viewport von Untitled.blend.'
      }
    }
    $visParsed = $visRaw | ConvertFrom-Json
    $visOk = $visParsed.type -eq 'action.result'
    Check "Vision-Urteil zum Fensterbild wird zugeordnet" $visOk $visRaw.Substring(0, [Math]::Min(200, $visRaw.Length))
    if ($visOk) {
      $vis = $visParsed.payload.result
      Check "Urteil passt zur Aufnahme (matchesCapture)" ($vis.visionReview.matchesCapture -eq $true) "matchesCapture=$($vis.visionReview.matchesCapture)"
      $visionPending = $visParsed.payload | Select-Object -ExpandProperty visionPending -ErrorAction SilentlyContinue
      Check "Sichtprüfung ist abgeschlossen (kein visionPending mehr)" ($null -eq $visionPending) "visionPending=$visionPending"
    }
  }

  # 6) Die geöffnete Datei darf nicht verändert worden sein.
  $afterHash = Get-BlendHash $blendFile
  Check "Geöffnete .blend ist unverändert" ($afterHash -eq $beforeHash) "vor=$beforeHash nach=$afterHash"
}
finally {
  try { $proc.Kill() } catch {}
  try { $proc.Dispose() } catch {}
}

if ($failures.Count -gt 0) {
  Write-Host "=== ERGEBNIS: $($failures.Count) Prüfung(en) fehlgeschlagen ==="
  exit 1
}
Write-Host "=== ERGEBNIS: alle Prüfungen durchgelaufen ==="
exit 0

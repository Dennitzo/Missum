# Does the bridge stay reachable, and what happens right after the launching
# script is gone? Phase 'inline' starts Blender and watches while its own parent
# process is still alive; phase 'after' only watches, from a new process.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tmp\blender-lifetime-probe.ps1 -Phase inline -Seconds 90
#   powershell -NoProfile -ExecutionPolicy Bypass -File tmp\blender-lifetime-probe.ps1 -Phase after   -Seconds 90

param(
    [ValidateSet('inline', 'after')] [string] $Phase = 'inline',
    [int] $Seconds = 90,
    [switch] $WithCapture
)

$ErrorActionPreference = 'Stop'

$repo = 'C:\Users\AMD\Documents\GitHub\Missum'
$blenderExe = 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
$probe = Join-Path $repo 'tmp\lifetime-probe'
$profile = Join-Path $repo 'tmp\lifetime-profile'
$addons = Join-Path $profile 'scripts\addons'
$pidFile = Join-Path $probe 'blender-pid.txt'
$quitFlag = Join-Path $probe 'quit.flag'
$stateDir = Join-Path $env:TEMP 'missum-program-integrations'
$activeRoot = Join-Path $env:LOCALAPPDATA 'Missum\Extensions\active\com.missum.blender'
$content = Get-ChildItem -LiteralPath $activeRoot -Recurse -Directory -Filter 'content' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$adapterExe = Join-Path $content.FullName 'adapter\Missum.BlenderAdapter.exe'

function Test-Port {
    try { $c = New-Object System.Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 8765); $c.Close(); return $true }
    catch { return $false }
}

function Invoke-Status {
    $envelope = [ordered]@{
        protocolVersion = 1
        type            = 'action.invoke'
        requestId       = "probe-$(Get-Random)"
        payload         = [ordered]@{
            actionId      = 'com.missum.blender/edit'
            modelToolName = 'blender.status'
            arguments     = ('{}' | ConvertFrom-Json)
        }
    }
    $payload = $envelope | ConvertTo-Json -Depth 30 -Compress
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $adapterExe
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($psi)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload + "`n")
    $stream = $process.StandardInput.BaseStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    $stream.Close()
    $stdout = $process.StandardOutput.ReadToEnd()
    if (-not $process.WaitForExit(60000)) { try { $process.Kill($true) } catch { } }
    $line = ($stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 }) | Select-Object -Last 1
    if (-not $line) { return [pscustomobject]@{ kind = 'keine Antwort'; code = '' } }
    try {
        $parsed = $line | ConvertFrom-Json
        return [pscustomobject]@{
            kind = $parsed.type
            code = [string]$parsed.payload.code
            connected = $parsed.payload.result.connected
            instances = $parsed.payload.result.instanceCount
        }
    } catch { return [pscustomobject]@{ kind = 'unparsbar'; code = '' } }
}

if (-not (Test-Path $probe)) { New-Item -ItemType Directory -Force -Path $probe | Out-Null }

if ($Phase -eq 'inline') {
    Get-CimInstance Win32_Process -Filter "Name = 'blender.exe'" -ErrorAction SilentlyContinue | Where-Object {
        $_.CommandLine -and $_.CommandLine.Contains('blender-gui-startup.py')
    } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500

    if (Test-Path $profile) { Remove-Item -Recurse -Force $profile }
    New-Item -ItemType Directory -Force -Path $addons | Out-Null
    Copy-Item -Recurse -Force (Join-Path $repo 'extensions\blender\missum_blender_bridge') (Join-Path $addons 'missum_blender_bridge')
    Get-ChildItem -LiteralPath $probe -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $backup = Join-Path $probe ('state-before-' + (Get-Date).ToString('HHmmss'))
    if (Test-Path $stateDir) {
        New-Item -ItemType Directory -Force -Path $backup | Out-Null
        foreach ($name in @('status.json', 'blender.json')) {
            $p = Join-Path $stateDir $name
            if (Test-Path -LiteralPath $p) { Move-Item -LiteralPath $p -Destination $backup -Force }
        }
    }

    $env:BLENDER_USER_SCRIPTS = (Join-Path $profile 'scripts')
    $env:MISSUM_GUI_LOG = Join-Path $probe 'gui.log'
    $env:MISSUM_GUI_FLAG = $quitFlag
    $env:MISSUM_GUI_SECONDS = '1800'
    $env:MISSUM_BLENDER_PORT = '8765'
    $blender = Start-Process -FilePath $blenderExe -ArgumentList @(
        '--factory-startup', '--python', (Join-Path $repo 'tmp\blender-gui-startup.py')) -PassThru `
        -RedirectStandardOutput (Join-Path $probe 'blender-out.log') `
        -RedirectStandardError (Join-Path $probe 'blender-err.log')
    Set-Content -LiteralPath $pidFile -Value $blender.Id
    Write-Host ("gestartet: pid=" + $blender.Id)
}

if (-not (Test-Path $pidFile)) { Write-Host 'keine pid-Datei, zuerst Phase inline'; exit 2 }
$targetPid = [int](Get-Content -LiteralPath $pidFile -Raw).Trim()
Write-Host ("--- beobachten als Phase $Phase, pid=$targetPid, $Seconds Sekunden ---")

$deadline = (Get-Date).AddSeconds($Seconds)
$tick = 0
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
    $alive = [bool]$proc
    $portOk = Test-Port
    $line = "t+" + $tick.ToString().PadLeft(3) + "s  prozess=" + $alive + "  port=" + $portOk
    if ($alive -and $portOk -and ($tick % 30 -eq 0)) {
        $status = Invoke-Status
        $line += "  status=" + $status.kind + " verbunden=" + $status.connected + " instanzen=" + $status.instances + " code=" + $status.code
    }
    Write-Host $line
    if (-not $alive) { Write-Host 'DER PROZESS IST WEG'; break }
    $tick += 15
    Start-Sleep -Seconds 15
}

$proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
Write-Host ("--- Ende: prozess=" + [bool]$proc + " port=" + (Test-Port) + " ---")
Write-Host '--- gui.log ---'
Get-Content -LiteralPath (Join-Path $probe 'gui.log') -ErrorAction SilentlyContinue
Write-Host '--- blender-out.log (letzte Zeilen) ---'
Get-Content -LiteralPath (Join-Path $probe 'blender-out.log') -Tail 12 -ErrorAction SilentlyContinue
if ($proc) { exit 0 } else { exit 3 }

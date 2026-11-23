#requires -Version 5.1
<#
.SYNOPSIS
    Startet den Unreal Editor mit der Missum-Python-Laufzeit und prüft den Kanal.
.DESCRIPTION
    Lese- und Pruefhilfe fuer Entwicklung und Abnahme. Sie startet keinen Plugin-Build
    und kein cmd.exe, sondern nur den Editor mit -ExecutePythonScript. Danach wartet
    sie auf die Bereitschaftsquittung, fragt Kommandos ueber das Adapterprotokoll ab und
    schliesst den Editor wieder. ASCII-only, weil PowerShell 5.1 Skripte ohne BOM als
    ANSI liest.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-unreal-channel.ps1 -Commands status,channel.probe
#>
[CmdletBinding()]
param(
    [string] $EngineRoot = 'E:\UnrealEngine\UE_5.8',
    [string] $Project = 'C:\Users\AMD\Documents\GitHub\GrandTheftCyberpunk\GrandTheftCyberpunk.uproject',
    [string] $Commands = 'status',
    [string] $CommandFile,
    [string] $Adapter,
    [string] $Bootstrap,
    [string] $Client,
    [string] $Workspace,
    [int] $ReadyTimeoutSeconds = 600,
    [switch] $LeaveEditorRunning
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Bootstrap)) { $Bootstrap = Join-Path $repository 'extensions\unreal\runtime\unreal_bootstrap.py' }
if ([string]::IsNullOrWhiteSpace($Client)) { $Client = Join-Path $PSScriptRoot 'unreal-channel-client.py' }
if ([string]::IsNullOrWhiteSpace($Workspace)) { $Workspace = Split-Path -Parent $Project }

foreach ($path in @($EngineRoot, $Project, $Bootstrap, $Client)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Benotigter Pfad fehlt: $path" }
}

$editor = Join-Path $EngineRoot 'Engine\Binaries\Win64\UnrealEditor.exe'
$python = Join-Path $EngineRoot 'Engine\Binaries\ThirdParty\Python3\Win64\python.exe'
if (-not (Test-Path -LiteralPath $editor)) { throw "Unreal Editor fehlt: $editor" }
if (-not (Test-Path -LiteralPath $python)) { throw "Engine-Python fehlt: $python" }

$checkDirectory = Join-Path $env:TEMP ('missum-unreal-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $checkDirectory | Out-Null
$readyPath = Join-Path $checkDirectory 'ready.json'
# Der Produktionsstarter nutzt dieses Verzeichnis; der Adapter schreibt dorthin die Aufnahme.
$captureRoot = Join-Path $env:TEMP 'missum-program-integrations\captures'
New-Item -ItemType Directory -Force -Path $captureRoot | Out-Null

$token = -join (1..64 | ForEach-Object { '0123456789abcdef'[[int](Get-Random -Maximum 16)] })

$env:MISSUM_UNREAL_TOKEN = $token
$env:MISSUM_UNREAL_READY_PATH = $readyPath
$env:MISSUM_UNREAL_WORKSPACE = $Workspace
$env:MISSUM_UNREAL_CAPTURE_ROOT = $captureRoot
$env:MISSUM_UNREAL_PROJECT_PATH = $Project

Write-Host ("Kanalpruefung Editor : {0}" -f $editor)
Write-Host ("Projekt              : {0}" -f $Project)
Write-Host ("Laufzeit             : {0}" -f $Bootstrap)
Write-Host ("Quittung             : {0}" -f $readyPath)

# Kein -NoShaderCompile: die Engine bricht sonst mit der Assertion AllowShaderCompiling().
# Kein -unattended: der Produktionsstarter uebergibt ebenfalls nur Projekt und Skript.
$arguments = @($Project, "-ExecutePythonScript=$Bootstrap")
$process = Start-Process -FilePath $editor -ArgumentList $arguments -PassThru
Write-Host ("Editor gestartet als PID {0}" -f $process.Id)

$exitCode = 0
try {
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    $receipt = $null
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) { throw ("Editor wurde vor der Bereitschaftsquittung beendet (Exitcode {0})." -f $process.ExitCode) }
        if (Test-Path -LiteralPath $readyPath) {
            # Die Quittung gilt erst, wenn sie ausdrücklich vom Python-Kanal stammt.
            try {
                $candidate = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                if ($candidate.channel -and $candidate.channel.type -eq 'python') { $receipt = $candidate; break }
            } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $receipt) { throw ("Keine Bereitschaftsquittung innerhalb von {0} Sekunden." -f $ReadyTimeoutSeconds) }

    Write-Host ''
    Write-Host '--- BEREITSCHAFTSQUITTUNG ---'
    $receipt | ConvertTo-Json -Depth 6

    if ($receipt.channel.type -ne 'python') { throw ("Kanal ist nicht python: {0}" -f $receipt.channel.type) }
    if ([int]$receipt.processId -ne [int]$process.Id) { throw 'Prozesskennung der Quittung passt nicht zum gestarteten Editor.' }

    if (-not [string]::IsNullOrWhiteSpace($Adapter)) {
        # Jeder Auftrag laeuft durch den echten Adapter: dasselbe Werkzeugrad, dieselben Grenzen.
        $runner = Join-Path $PSScriptRoot 'unreal-adapter-runner.py'
        if (-not (Test-Path -LiteralPath $runner)) { throw "Adapter-Laeufer fehlt: $runner" }
        if (-not (Test-Path -LiteralPath $Adapter)) { throw "Adapter fehlt: $Adapter" }
        if ([string]::IsNullOrWhiteSpace($CommandFile)) { throw '-Adapter bentigt -CommandFile.' }
        & $python $runner $Adapter $readyPath $Workspace $CommandFile
        if ($LASTEXITCODE -ne 0) { $exitCode = $LASTEXITCODE }
    }

    if (-not [string]::IsNullOrWhiteSpace($CommandFile) -and [string]::IsNullOrWhiteSpace($Adapter)) {
        # Szenariodatei direkt gegen den Kanal: ein Array aus { command, arguments }.
        $index = 0
        foreach ($entry in (Get-Content -LiteralPath $CommandFile -Raw | ConvertFrom-Json)) {
            $argumentsJson = $entry.arguments | ConvertTo-Json -Compress -Depth 8
            if ([string]::IsNullOrWhiteSpace($argumentsJson)) { $argumentsJson = '{}' }
            $index = $index + 1
            $argumentFile = Join-Path $checkDirectory ('args-' + $index + '.json')
            [IO.File]::WriteAllText($argumentFile, $argumentsJson, (New-Object System.Text.UTF8Encoding($false)))
            Write-Host ''
            Write-Host ('--- KOMMANDO ' + $entry.command + ' ' + $argumentsJson)
            & $python $Client $readyPath $entry.command ('@' + $argumentFile)
            if ($LASTEXITCODE -ne 0) { Write-Host ("Kommando {0} meldete Fehlercode {1}" -f $entry.command, $LASTEXITCODE); $exitCode = $LASTEXITCODE }
        }
    }
    foreach ($command in ($Commands -split ',')) {
        if ([string]::IsNullOrWhiteSpace($command)) { continue }
        Write-Host ''
        Write-Host ('--- KOMMANDO ' + $command + ' ---')
        & $python $Client $readyPath $command '{}'
        if ($LASTEXITCODE -ne 0) { Write-Host ("Kommando {0} meldete Fehlercode {1}" -f $command, $LASTEXITCODE); $exitCode = $LASTEXITCODE }
    }
}
finally {
    if (-not $LeaveEditorRunning) {
        Write-Host ''
        Write-Host 'Schliesse den Editor.'
        try {
            $current = Get-Process -Id $process.Id -ErrorAction SilentlyContinue
            if ($current -and -not $current.HasExited) {
                # PowerShell 5.1 kennt kein Process.Kill(entireProcessTree).
                # Test maps are never saved: an unsaved level makes Unreal show a modal
                # "Inhalt speichern" dialog that would block this script. Give the editor a
                # short chance to close on its own, then end it without saving.
                if (-not $current.CloseMainWindow()) { $current.Kill() }
                $current.WaitForExit(15000) | Out-Null
                if (-not $current.HasExited) { Write-Host 'Editor wartet auf eine Speicherabfrage; beende ihn ohne zu speichern.'; $current.Kill() }
                $current.WaitForExit(30000) | Out-Null
            }
        } catch { Write-Host ("Editor liess sich nicht sauber schliessen: {0}" -f $_.Exception.Message) }
    } else {
        Write-Host 'Editor bleibt laufen (-LeaveEditorRunning).'
    }
    if (-not $LeaveEditorRunning) {
        Remove-Item -LiteralPath $checkDirectory -Recurse -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host ('Prüfverzeichnis bleibt erhalten (Editor läuft weiter): ' + $checkDirectory)
    }
}

Write-Host ''
Write-Host ('Kanalpruefung beendet mit Code ' + $exitCode)
exit $exitCode

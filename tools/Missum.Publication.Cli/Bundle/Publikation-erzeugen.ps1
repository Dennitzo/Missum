#requires -Version 5.1
<#
.SYNOPSIS
Erzeugt Publikation.pdf aus Publikation.md mit dem originalen Missum-Renderer.
.DESCRIPTION
Der Projektpfad ist standardmaessig das Verzeichnis dieses Skripts, unabhaengig
vom aktuellen Arbeitsverzeichnis. Das Paket .publication-renderer muss daneben
liegen. Kein laufendes Missum, AI-Modell, Python, Node oder .NET-SDK erforderlich.
Fortschritt: stdout und publication-build.log. Ergebnis: publication-build.json.
.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publikation-erzeugen.ps1
.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publikation-erzeugen.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [string]$ProjectPath,
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding
try {
    if ([string]::IsNullOrWhiteSpace($ProjectPath)) { $ProjectPath = $PSScriptRoot }
    $project = [IO.Path]::GetFullPath($ProjectPath)
    $renderer = Join-Path $PSScriptRoot '.publication-renderer\Missum.Publication.Cli.exe'
    if (-not (Test-Path -LiteralPath $renderer -PathType Leaf)) {
        throw 'Das mitgelieferte Paket .publication-renderer fehlt. Kopiere es zusammen mit diesem Skript in den Projektordner.'
    }
    $arguments = @('--project', $project)
    if ($CheckOnly) { $arguments += '--check-only' }
    & $renderer @arguments
    exit $LASTEXITCODE
}
catch {
    [Console]::Error.WriteLine('Publikation konnte nicht verarbeitet werden: ' + $_.Exception.Message)
    exit 3
}

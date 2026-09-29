#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$shortcutPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonStartup)) 'Missum-AI-Server.lnk'
$approvedKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder'
$approvedName = 'Missum-AI-Server.lnk'

if (Test-Path -LiteralPath $shortcutPath -PathType Leaf) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $targetName = [IO.Path]::GetFileName($shortcut.TargetPath)
    if (-not [string]::Equals($targetName, 'Missum-AI-Server.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected startup shortcut target: $($shortcut.TargetPath)"
    }
    Remove-Item -LiteralPath $shortcutPath -Force
}

if (Test-Path -LiteralPath $approvedKey) {
    $approved = Get-ItemProperty -LiteralPath $approvedKey -Name $approvedName -ErrorAction SilentlyContinue
    if ($null -ne $approved) {
        Remove-ItemProperty -LiteralPath $approvedKey -Name $approvedName
    }
}

$shortcutStillExists = Test-Path -LiteralPath $shortcutPath
$registryStillExists = $false
if (Test-Path -LiteralPath $approvedKey) {
    $registryStillExists = $null -ne (Get-ItemProperty -LiteralPath $approvedKey -Name $approvedName -ErrorAction SilentlyContinue)
}
if ($shortcutStillExists -or $registryStillExists) {
    throw 'The legacy Missum AI Server startup registration could not be removed completely.'
}

Write-Host 'Legacy Missum AI Server startup shortcut and StartupApproved registration removed.' -ForegroundColor Green

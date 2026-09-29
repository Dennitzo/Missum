#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $NativeStateDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-MissumAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
$configured = Read-MissumAiStackEnvironment -Path $paths.EnvironmentFile
if ([string]::IsNullOrWhiteSpace($NativeStateDirectory)) { $NativeStateDirectory = $configured['MISSUM_AI_NATIVE_STATE_ROOT'] }
if ([string]::IsNullOrWhiteSpace($NativeStateDirectory)) { $NativeStateDirectory = Join-Path $env:USERPROFILE '.missum\native-runtime' }
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) {
    Write-MissumAiStackEnvironment -Paths $paths
}
try {
    Invoke-MissumAiCompose -Paths $paths -Arguments @('stop')
}
finally {
    # The native runtime must stop even if Docker Desktop is unavailable.
    & (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Stop -StateDirectory $NativeStateDirectory
}

Write-Host 'Missum AI stack stopped; Docker workers and the owned native Windows model runtime released their GPU allocations.' -ForegroundColor Green

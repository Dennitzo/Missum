#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [Alias('CodingModelRoot')][string] $NativeModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '2.0.0',
    [string] $NativeBinaryPath,
    [string] $NativeStateDirectory,
    [switch] $UpdateNativeRuntime
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-MissumAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
$configured = Read-MissumAiStackEnvironment -Path $paths.EnvironmentFile
if ([string]::IsNullOrWhiteSpace($NativeStateDirectory)) { $NativeStateDirectory = $configured['MISSUM_AI_NATIVE_STATE_ROOT'] }
if ([string]::IsNullOrWhiteSpace($NativeStateDirectory)) { $NativeStateDirectory = Join-Path $env:USERPROFILE '.missum\native-runtime' }
$llamaInstallRoot = Join-Path $env:LOCALAPPDATA 'Missum\NativeRuntime\llama.cpp'
$resolvedBinaryFile = Join-Path $paths.DataRoot 'native-llama-server.path'
if ($UpdateNativeRuntime) {
    if ($NativeBinaryPath) { throw 'Choose either -NativeBinaryPath or -UpdateNativeRuntime.' }
    & (Join-Path $PSScriptRoot 'manage-llama-server.ps1') -Action Update -InstallRoot $llamaInstallRoot `
        -ResolvedPathFile $resolvedBinaryFile -SkipFirewall
    $NativeBinaryPath = (Get-Content -LiteralPath $resolvedBinaryFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($NativeBinaryPath)) { $NativeBinaryPath = $configured['MISSUM_AI_NATIVE_BINARY_PATH'] }
if ([string]::IsNullOrWhiteSpace($NativeBinaryPath) -and (Test-Path -LiteralPath $resolvedBinaryFile -PathType Leaf)) {
    $NativeBinaryPath = (Get-Content -LiteralPath $resolvedBinaryFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($NativeBinaryPath) -or -not (Test-Path -LiteralPath $NativeBinaryPath -PathType Leaf) -or
    [IO.Path]::GetFileName($NativeBinaryPath) -ne 'llama-server.exe') {
    throw 'Supply an existing tested llama-server.exe with -NativeBinaryPath, or explicitly request -UpdateNativeRuntime.'
}
$NativeBinaryPath = [IO.Path]::GetFullPath($NativeBinaryPath)
Write-MissumAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion `
    -NativeBinaryPath $NativeBinaryPath -NativeStateDirectory $NativeStateDirectory
[IO.File]::WriteAllText($resolvedBinaryFile, $NativeBinaryPath + "`n", [Text.UTF8Encoding]::new($false))
Invoke-MissumAiCompose -Paths $paths -Arguments @('config', '--quiet')
& (Join-Path $PSScriptRoot 'manage-coding-llama.ps1') -Action Start `
    -ModelRoot $paths.NativeModelRoot -BinaryPath $NativeBinaryPath -StateDirectory $NativeStateDirectory
Invoke-MissumAiCompose -Paths $paths -Arguments @('up', '-d', '--no-build', '--pull', 'never')
Write-Host "Missum AI stack is starting at http://${ServerIp}:8080; all language, vision and embedding models use native Windows llama.cpp on port 8081." -ForegroundColor Green

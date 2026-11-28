#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [Alias('CodingModelRoot')][string] $NativeModelRoot,
    [string] $ServerIp = '',  # empty: keep the address saved in stack.env (new installations: 127.0.0.1)
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
if ([string]::IsNullOrWhiteSpace($NativeBinaryPath)) {
    $candidate = $configured['MISSUM_AI_NATIVE_BINARY_PATH']
    if ([string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $resolvedBinaryFile -PathType Leaf)) {
        $candidate = (Get-Content -LiteralPath $resolvedBinaryFile -Raw).Trim()
    }
    # Older stack migrations wrote another application's installation here.
    # Explicit -NativeBinaryPath remains an intentional user override.
    if (-not [string]::IsNullOrWhiteSpace($candidate) -and
        @($candidate -split '[\\/]' | Where-Object { $_ -in @('.unsloth', 'unsloth', 'unsloth_studio') }).Count -gt 0) {
        $candidate = $null
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $releaseFile = Join-Path $llamaInstallRoot 'current-release.json'
        if (Test-Path -LiteralPath $releaseFile -PathType Leaf) {
            try { $candidate = [string](Get-Content -LiteralPath $releaseFile -Raw | ConvertFrom-Json).serverPath }
            catch { throw "Missum llama.cpp runtime information is invalid: $releaseFile" }
        }
    }
    $NativeBinaryPath = $candidate
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
    -ModelRoot (Join-Path $env:USERPROFILE '.cache\huggingface') -BinaryPath $NativeBinaryPath -StateDirectory $NativeStateDirectory
# Missum images are built locally; only the pinned upstream images (Caddy, SearXNG) may be pulled once.
Invoke-MissumAiCompose -Paths $paths -Arguments @('up', '-d', '--no-build', '--pull', 'missing')
$publicUrl = (Read-MissumAiStackEnvironment -Path $paths.EnvironmentFile)['MISSUM_AI_PUBLIC_URL']
Write-Host "Missum AI stack is starting at ${publicUrl}; all language, vision and embedding models use native Windows llama.cpp on port 8081." -ForegroundColor Green

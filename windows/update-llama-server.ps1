#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot,
    [string] $InstallRoot,
    [string] $ProbeModelRoot,
    [ValidatePattern('^b[0-9]+$')][string] $ReleaseTag,
    [ValidateRange(1024, 65534)][int] $ProbePort = 18081
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$cudaVersion = '12.4'

if (-not $DataRoot) { $DataRoot = $env:ASSISTANT_STACK_DATA_ROOT }
$paths = if ($DataRoot) { Get-MissumAiStackDefaults -DataRoot $DataRoot } else { Get-MissumAiStackDefaults }
if (-not $InstallRoot) { $InstallRoot = $env:ASSISTANT_LLAMA_INSTALL_ROOT }
if (-not $InstallRoot) {
    $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Missum\NativeRuntime\llama.cpp'
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
if (-not $ProbeModelRoot) { $ProbeModelRoot = $paths.NativeModelRoot }
foreach ($scope in @('Process', 'User', 'Machine')) {
    if ([Environment]::GetEnvironmentVariable('ASSISTANT_NATIVE_BINARY_PATH', $scope)) {
        throw "ASSISTANT_NATIVE_BINARY_PATH ($scope) overrides Missum's launcher. Remove the override before running this updater."
    }
}
if (-not (Test-Path -LiteralPath $ProbeModelRoot -PathType Container)) {
    throw "Model folder unavailable for the compatibility check: $ProbeModelRoot"
}

function Get-LatestCudaBuild {
    $headers = @{
        Accept = 'application/vnd.github+json'
        'User-Agent' = 'Missum-llama-server-updater'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=30' `
        -Headers $headers -TimeoutSec 45
    foreach ($release in ($releases | Sort-Object published_at -Descending)) {
        $tag = [string]$release.tag_name
        if ($release.draft -or $tag -notmatch '^b[0-9]+$') { continue }
        $assetNames = @($release.assets | Where-Object { $_.digest -match '^sha256:[0-9a-fA-F]{64}$' } |
            Select-Object -ExpandProperty name)
        if ($assetNames -contains "llama-$tag-bin-win-cuda-$CudaVersion-x64.zip" -and
            $assetNames -contains "cudart-llama-bin-win-cuda-$CudaVersion-x64.zip") { return $tag }
    }
    throw "No recent official Windows x64 build with verified CUDA $CudaVersion assets was found."
}

function Write-AtomicText {
    param([string] $Path, [string] $Text)
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, $Text, [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

$null = New-Item -ItemType Directory -Path $paths.DataRoot -Force
$lockPath = Join-Path $paths.DataRoot 'native-llama-update.lock'
$updateLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    if (-not $ReleaseTag) { $ReleaseTag = Get-LatestCudaBuild }
    Write-Host "Checking official llama.cpp $ReleaseTag (Windows x64, CUDA $CudaVersion)."
    $probeRoot = Join-Path $InstallRoot ('verification-' + [Guid]::NewGuid().ToString('N'))
    $resolvedPath = Join-Path $probeRoot 'candidate.path'
    $runtimeManager = Join-Path $PSScriptRoot 'manage-coding-llama.ps1'
    & (Join-Path $PSScriptRoot 'manage-llama-server.ps1') -Action Update -ReleaseTag $ReleaseTag `
        -InstallRoot $InstallRoot -CudaVersion $CudaVersion -ResolvedPathFile $resolvedPath -SkipFirewall
    $binary = [IO.Path]::GetFullPath((Get-Content -LiteralPath $resolvedPath -Raw).Trim())
    $versionRoot = [IO.Path]::GetFullPath((Join-Path $InstallRoot "versions\$ReleaseTag")).TrimEnd('\') + '\'
    if (-not $binary.StartsWith($versionRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($binary) -ne 'llama-server.exe') {
        throw "The installer did not resolve the requested build $ReleaseTag."
    }

    $savedErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $version = ((& $binary --version 2>&1 | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
        if ($LASTEXITCODE -ne 0) { throw "llama-server --version failed: $version" }
        $devices = ((& $binary --list-devices 2>&1 | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine).Trim()
        if ($LASTEXITCODE -ne 0 -or $devices -notmatch '(?m)^\s*CUDA[0-9]+:') {
            throw "The downloaded build did not detect any CUDA devices. $devices"
        }
    } finally { $ErrorActionPreference = $savedErrorAction }
    Write-Host $version
    Write-Host (($devices -split '\r?\n' | Where-Object { $_ -match '^\s*CUDA[0-9]+:' }) -join [Environment]::NewLine)

    $ports = @($ProbePort, ($ProbePort + 1))
    if (Get-NetTCPConnection -LocalPort $ports -State Listen -ErrorAction SilentlyContinue) {
        throw "The verification ports $($ports -join ', ') are in use. Choose another -ProbePort."
    }
    try {
        $status = & $runtimeManager -Action Start -ModelRoot $ProbeModelRoot -BinaryPath $binary `
            -StateDirectory $probeRoot -Port $ProbePort -ListenAddress '127.0.0.1'
        if (-not $status.Healthy) { throw 'The Missum model router did not become healthy.' }
        $models = Invoke-RestMethod -Uri "http://127.0.0.1:$ProbePort/v1/models" -TimeoutSec 10
        Write-Host "Missum router health and model listing verified ($(@($models.data | Where-Object { $null -ne $_ }).Count) entries)."
    } finally {
        if (Test-Path -LiteralPath (Join-Path $probeRoot 'supervisor.json') -PathType Leaf) {
            $stopped = & $runtimeManager -Action Stop -StateDirectory $probeRoot -Port $ProbePort -ShutdownTimeoutSeconds 15
            if ($stopped.Running) { throw "Could not stop the isolated verifier. Logs: $probeRoot" }
        }
    }

    # The desktop reads the path file. The stack launcher prefers its environment
    # file, so update both while preserving every unrelated environment setting.
    $pointerPath = Join-Path $paths.DataRoot 'native-llama-server.path'
    $hadPointer = [IO.File]::Exists($pointerPath)
    $oldPointer = if ($hadPointer) { [IO.File]::ReadAllText($pointerPath) } else { '' }
    $oldEnv = if ([IO.File]::Exists($paths.EnvironmentFile)) { [IO.File]::ReadAllText($paths.EnvironmentFile) } else { '' }
    $newEntry = 'MISSUM_AI_NATIVE_BINARY_PATH=' + $binary.Replace('\', '/')
    $envEntry = '(?im)^[ \t]*MISSUM_AI_NATIVE_BINARY_PATH=[^\r\n]*'
    $newEnv = if ($oldEnv -match $envEntry) {
        [regex]::Replace($oldEnv, $envEntry, [System.Text.RegularExpressions.MatchEvaluator]{ param($match) $newEntry })
    } else {
        $separator = if ($oldEnv.Length -gt 0 -and -not $oldEnv.EndsWith("`n")) { [Environment]::NewLine } else { '' }
        $oldEnv + $separator + $newEntry + [Environment]::NewLine
    }
    $pointerChanged = -not [string]::Equals($oldPointer.Trim(), $binary, [StringComparison]::OrdinalIgnoreCase)
    if ($pointerChanged) { Write-AtomicText -Path $pointerPath -Text ($binary + [Environment]::NewLine) }
    try {
        if ($newEnv -cne $oldEnv) { Write-AtomicText -Path $paths.EnvironmentFile -Text $newEnv }
    } catch {
        if ($pointerChanged) {
            if ($hadPointer) { Write-AtomicText -Path $pointerPath -Text $oldPointer }
            else { [IO.File]::Delete($pointerPath) }
        }
        throw
    }
    Write-Host "Missum will use $binary the next time its native runtime starts." -ForegroundColor Green
    if ($pointerChanged -and $oldPointer.Trim()) { Write-Host "Previous executable: $($oldPointer.Trim())" }
    Write-Host "Verification logs: $probeRoot"
} finally {
    $updateLock.Dispose()
}

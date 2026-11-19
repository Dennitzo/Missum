#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceDirectory,
    [Parameter(Mandatory)][string] $OfficialCudaDirectory,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
$official = (Resolve-Path -LiteralPath $OfficialCudaDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new output directory: $output" }

# The dynamic CUDA backend must share the exact GGML ABI with the rebuilt core.
$revision = '240d68fcfe0c2a1f298c8e18ca181368b1914259'
$actualRevision = & git -C $source rev-parse HEAD
if ($LASTEXITCODE -or $actualRevision -ne $revision) {
    throw 'Use a llama.cpp checkout of the official b12167 release.'
}
$cudaFiles = @('ggml-cuda.dll', 'cublas64_12.dll', 'cublasLt64_12.dll', 'cudart64_12.dll')
foreach ($name in $cudaFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $official $name) -PathType Leaf)) {
        throw "The official b12167 CUDA 12 release is missing $name."
    }
}

$patch = Join-Path $PSScriptRoot 'llama-slot-prefix-b12167.patch'
& git -C $source apply --check $patch 2>$null
if (!$LASTEXITCODE) {
    & git -C $source apply $patch
    if ($LASTEXITCODE) { throw 'Could not apply the slot-prefix patch.' }
}
else {
    & git -C $source apply --reverse --check $patch
    if ($LASTEXITCODE) { throw 'The source does not match the slot-prefix patch.' }
}

# Run from a Visual Studio x64 Developer PowerShell with CMake and Ninja.
$buildDirectory = Join-Path $source 'build-missum'
& cmake -S $source -B $buildDirectory -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON `
    -DGGML_BACKEND_DL=ON -DGGML_NATIVE=OFF -DGGML_CUDA=OFF -DLLAMA_BUILD_TESTS=OFF `
    -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_UI=OFF -DLLAMA_USE_PREBUILT_UI=OFF -DLLAMA_OPENSSL=OFF
if ($LASTEXITCODE) { throw 'Could not configure the native runtime.' }
& cmake --build $buildDirectory --target llama-server --parallel 12
if ($LASTEXITCODE) { throw 'Could not compile the native runtime.' }

$null = New-Item -ItemType Directory -Path $output
$bin = Join-Path $buildDirectory 'bin'
Copy-Item -LiteralPath (Join-Path $bin 'llama-server.exe') -Destination $output
Get-ChildItem -LiteralPath $bin -File -Filter '*.dll' | Copy-Item -Destination $output
foreach ($name in $cudaFiles) {
    Copy-Item -LiteralPath (Join-Path $official $name) -Destination $output
}
$server = Join-Path $output 'llama-server.exe'
$devices = & $server --list-devices 2>&1 | Out-String
if ($LASTEXITCODE -or $devices -notmatch 'CUDA[0-9]+:') { throw "CUDA validation failed: $devices" }
Write-Output $server

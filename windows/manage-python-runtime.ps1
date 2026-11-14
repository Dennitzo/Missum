#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'Missum\NativeRuntime\Python')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Official CPython Windows x64 embeddable archive. Its standard library is all
# the native catalog needs. Pin both URL and the SHA-256 published by python.org.
$version = '3.14.10'
$expectedSha256 = '90905e64a840557d8c1bc4f5b74db3d7ee85e3ffb217611cac1c7813acea4159'
$downloadUri = 'https://www.python.org/ftp/python/3.14.10/python-3.14.10-embed-amd64.zip'
$root = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\', '/')
if ([string]::IsNullOrWhiteSpace($root) -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\', '/')) {
    throw 'The Missum Python installation root must be a dedicated directory.'
}
$versions = Join-Path $root 'versions'
$destination = [IO.Path]::GetFullPath((Join-Path $versions $version))
$python = Join-Path $destination 'python.exe'
$marker = Join-Path $destination 'missum-python.json'
New-Item -ItemType Directory -Path $versions -Force | Out-Null

function Test-MissumPython {
    param([string] $Directory)
    $exe = Join-Path $Directory 'python.exe'
    $receipt = Join-Path $Directory 'missum-python.json'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or
        -not (Test-Path -LiteralPath $receipt -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'python314.zip') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'python314.dll') -PathType Leaf)) { return $false }
    try {
        $metadata = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        return $metadata.version -eq $version -and $metadata.archiveSha256 -eq $expectedSha256
    } catch { return $false }
}

function Write-MissumPythonPointer {
    param([string] $Executable)
    $pointer = Join-Path $root 'current.path'
    $temporary = Join-Path $root ('current-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, $Executable + "`n", [Text.UTF8Encoding]::new($false))
    if ([IO.File]::Exists($pointer)) { [IO.File]::Replace($temporary, $pointer, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $pointer) }
}

$lockPath = Join-Path $root 'install.lock'
$deadline = [DateTime]::UtcNow.AddSeconds(240)
$installLock = $null
while ($null -eq $installLock) {
    try { $installLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw 'Die Installation der Missum-Python-Laufzeit ist durch einen anderen Vorgang blockiert.' }
        Start-Sleep -Milliseconds 250
    }
}
try {
    if (Test-MissumPython -Directory $destination) { Write-MissumPythonPointer -Executable $python; Write-Output $python; return }
    if (Test-Path -LiteralPath $destination) {
        throw "Die Missum-Python-Laufzeit unter '$destination' ist unvollständig. Bitte diesen Ordner prüfen oder umbenennen und Missum erneut starten."
    }
    $id = [Guid]::NewGuid().ToString('N')
    $archive = [IO.Path]::GetFullPath((Join-Path $root ("download-$id.zip")))
    $stage = [IO.Path]::GetFullPath((Join-Path $versions ("$version.staging-$id")))
    $rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
    foreach ($path in @($archive, $stage, $destination)) {
        if (-not $path.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Die Missum-Python-Installation liegt außerhalb ihres Installationsordners.'
        }
    }
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $downloadUri -OutFile $archive -UseBasicParsing -TimeoutSec 180
        $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expectedSha256) { throw "Die Prüfsumme des offiziellen Python-Archivs stimmt nicht: $actual" }
        New-Item -ItemType Directory -Path $stage | Out-Null
        Expand-Archive -LiteralPath $archive -DestinationPath $stage
        $stagePython = Join-Path $stage 'python.exe'
        if (-not (Test-Path -LiteralPath $stagePython -PathType Leaf)) { throw 'Das Python-Archiv enthält keinen Interpreter.' }
        $output = & $stagePython -I -B -c 'import sys,ctypes,hashlib,http.server,json,pathlib,sqlite3,ssl,struct,subprocess; assert sys.version_info[:2] == (3,14); print(sys.version_info[:3])'
        if ($LASTEXITCODE -ne 0) { throw 'Die eigenständige Python-Laufzeit konnte nicht initialisiert werden.' }
        @{ version = $version; archiveSha256 = $expectedSha256; source = $downloadUri } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'missum-python.json') -Encoding UTF8
        if (-not (Test-MissumPython -Directory $stage)) { throw 'Die Missum-Python-Laufzeit ist unvollständig.' }
        [IO.Directory]::Move($stage, $destination)
        Remove-Item -LiteralPath $archive -Force
    } catch {
        throw "Die unabhängige Missum-Python-Laufzeit konnte nicht bereitgestellt werden: $($_.Exception.Message)"
    }
    Write-MissumPythonPointer -Executable $python
    Write-Output $python
}
finally { $installLock.Dispose() }

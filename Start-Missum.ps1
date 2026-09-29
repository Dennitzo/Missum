#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$releaseApp = Join-Path $PSScriptRoot 'src\Missum.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Missum.exe'
$candidates = @(
    (Join-Path $PSScriptRoot 'artifacts\portable\win-x64\Missum.exe'),
    $releaseApp
)
$latest = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    ForEach-Object { Get-Item -LiteralPath $_ } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$app = if ($null -ne $latest) { $latest.FullName } else { $releaseApp }
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    dotnet build (Join-Path $PSScriptRoot 'src\Missum.App\Missum.App.csproj') -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Missum konnte nicht gebaut werden.' }
}
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
    throw "Missum.exe wurde nach dem Build nicht gefunden: $app"
}
Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot

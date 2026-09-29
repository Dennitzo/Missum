#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $ServerUrl = 'http://192.168.0.67:8080',
    [ValidateRange(1, 120)] [int] $WaitMinutes = 10,
    [switch] $IncludeInference
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$paths = Get-MissumAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
if (-not (Test-Path -LiteralPath $paths.EnvironmentFile -PathType Leaf)) { throw "Stack environment is missing: $($paths.EnvironmentFile)" }

$deadline = [DateTimeOffset]::UtcNow.AddMinutes($WaitMinutes)
$health = $null
do {
    try {
        $health = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/live') -TimeoutSec 5
        break
    }
    catch {
        Start-Sleep -Seconds 3
    }
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($null -eq $health -or $health.status -ne 'live') { throw "Gateway did not become live at $ServerUrl." }

$nativeModels = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/models/status') -TimeoutSec 30
if (-not $nativeModels.providerReachable) { throw 'The native Unsloth / llama.cpp runtime is not reachable through the gateway.' }
foreach ($role in @('general', 'vision', 'embedding')) {
    if (@($nativeModels.models | Where-Object { $_.role -eq $role -and $_.downloaded }).Count -eq 0) {
        throw "No complete native $role model is available through the gateway."
    }
}
$codingModels = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/models/coding') -TimeoutSec 30
if (-not $codingModels.runtimeReachable -or @($codingModels.models).Count -eq 0) {
    throw 'The Coding model catalog does not contain a complete local text model.'
}

$ready = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/ready') -TimeoutSec 30
if ($ready.status -notin @('ready', 'modelNotLoaded', 'modelLoading')) {
    throw "Gateway is not ready for on-demand model loading: $($ready.status)"
}
$capabilities = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/capabilities') -TimeoutSec 30
if ($capabilities.protocolVersion -ne '1.0') { throw 'Unexpected Missum protocol version.' }

$docker = Resolve-MissumDockerCommand
$composeConfiguration = & $docker compose --env-file $paths.EnvironmentFile -f $paths.ComposeFile config --format json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Missum Compose configuration.' }
$published = @(& $docker compose --env-file $paths.EnvironmentFile -f $paths.ComposeFile ps --format json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Missum Compose project.' }
foreach ($service in @('caddy', 'gateway', 'searxng', 'speech', 'media', 'image')) {
    $containers = @($published | Where-Object { $_.Service -eq $service })
    if ($containers.Count -ne 1 -or $containers[0].State -ne 'running' -or $containers[0].Health -ne 'healthy') {
        throw "Missum service is missing or unhealthy: $service"
    }
    $container = @(& $docker inspect $containers[0].ID | ConvertFrom-Json)[0]
    if ($LASTEXITCODE -ne 0) { throw "Cannot inspect service: $service" }
    $expectedImage = $composeConfiguration.services.PSObject.Properties[$service].Value.image
    if ($container.Config.Image -ne $expectedImage) { throw "Service $service uses $($container.Config.Image), expected $expectedImage." }
    $currentImage = @(& $docker image inspect $container.Config.Image | ConvertFrom-Json)[0]
    if ($LASTEXITCODE -ne 0 -or $container.Image -ne $currentImage.Id) { throw "Service $service does not use the currently tagged image." }
}
$publicServices = @($published | Where-Object {
    @($_.Publishers | Where-Object { [int]$_.PublishedPort -gt 0 }).Count -gt 0
})
if ($publicServices.Count -ne 1 -or $publicServices[0].Service -ne 'caddy') {
    throw 'Only the Caddy service may publish a host port.'
}

if ($IncludeInference) {
    Invoke-MissumDotNet -CommandArguments @(
        'run', '--project', (Resolve-MissumRepositoryPath -RelativePath 'src\Missum.Ai.SmokeClient\Missum.Ai.SmokeClient.csproj'),
        '--configuration', 'Release', '--', 'run', '--server', $ServerUrl,
        '--mode', 'General', '--prompt', 'Antworte nur mit: Missum AI bereit.'
    )
    $ready = Invoke-RestMethod -Uri ($ServerUrl.TrimEnd('/') + '/v1/health/ready') -TimeoutSec 30
    if ($ready.status -ne 'ready') { throw 'Gateway readiness was lost after inference.' }
}
Write-Host "Missum AI native model stack smoke passed. Readiness: $($ready.status)" -ForegroundColor Green

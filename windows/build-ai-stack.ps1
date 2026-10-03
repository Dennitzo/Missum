#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $ServerIp = '192.168.0.67',
    [string] $ImageVersion = '2.0.0',
    [switch] $SkipTests,
    [switch] $Pull
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$paths = Get-MissumAiStackDefaults -DataRoot $DataRoot -ModelRoot $ModelRoot -NativeModelRoot $NativeModelRoot
Write-MissumAiStackEnvironment -Paths $paths -ServerIp $ServerIp -ImageVersion $ImageVersion

if (-not $SkipTests) {
    Invoke-MissumDotNet -CommandArguments @(
        'test', (Resolve-MissumRepositoryPath -RelativePath 'tests\Missum.Ai.Server.Tests\Missum.Ai.Server.Tests.csproj'),
        '--configuration', 'Release', '--nologo'
    )
}
Invoke-MissumAiCompose -Paths $paths -Arguments @('config', '--quiet')
$arguments = @('build')
if ($Pull) { $arguments += '--pull' }
Invoke-MissumAiCompose -Paths $paths -Arguments $arguments
$docker = Resolve-MissumDockerCommand
$runnerArguments = @('build', '--tag', 'missum-ai/research-runner:1')
if ($Pull) { $runnerArguments += '--pull' }
$runnerArguments += Resolve-MissumRepositoryPath -RelativePath 'deploy\missum-ai\research-runner'
& $docker @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Research runner build failed with exit code $LASTEXITCODE." }
Write-Host "Missum AI $ImageVersion gateway/workers and research-runner:1 were built successfully. Language, vision and embedding models use the native Windows runtime." -ForegroundColor Green

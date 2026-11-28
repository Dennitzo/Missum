#requires -Version 5.1
[CmdletBinding()]
param(
    [string] $DataRoot = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'Missum-AI-Stack'),
    [string] $ModelRoot,
    [string] $NativeModelRoot,
    [string] $ServerIp = '',  # empty: keep the address saved in stack.env (new installations: 127.0.0.1)
    [string] $ImageVersion = '2.0.0',
    [ValidatePattern('^missum-ai/research-runner:[a-zA-Z0-9_.-]+$')]
    [string] $ResearchRunnerImage = 'missum-ai/research-runner:3',
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
$runnerArguments = @('build', '--tag', $ResearchRunnerImage)
if ($Pull) { $runnerArguments += '--pull' }
$runnerArguments += Resolve-MissumRepositoryPath -RelativePath 'deploy\missum-ai\research-runner'
& $docker @runnerArguments
if ($LASTEXITCODE -ne 0) { throw "Research runner build failed with exit code $LASTEXITCODE." }
Write-Host "Missum AI $ImageVersion gateway/workers and $ResearchRunnerImage were built successfully." -ForegroundColor Green

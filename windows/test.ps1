#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoRestore
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$testProject = Resolve-MissumRepositoryPath -RelativePath 'tests\Missum.Tests\Missum.Tests.csproj'
if (-not $NoRestore) {
    Invoke-MissumDotNet -CommandArguments @('restore', $testProject, '-p:Platform=x64', ("-p:Configuration={0}" -f $Configuration), '--nologo')
}
Invoke-MissumDotNet -CommandArguments @(
    'test', $testProject, '-p:Platform=x64',
    '--configuration', $Configuration,
    '--no-restore',
    '--nologo',
    '--logger', 'console;verbosity=normal'
)
Write-Host 'Tests passed.' -ForegroundColor Green

#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# Exercise the production process wrapper without rerunning the full AI suites.
$source = Join-Path $PSScriptRoot 'test-agent-context.ps1'
$parseTokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The test wrapper has PowerShell syntax errors.' }
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ContextCheck'
}, $true))
if ($functions.Count -ne 1) { throw 'The production process wrapper was not found uniquely.' }
. ([ScriptBlock]::Create($functions[0].Extent.Text))

$evidence = Assert-MissumArtifactPath -Path (Join-Path (Get-MissumRepositoryRoot) 'artifacts\build-20261010\diagnostic-regression')
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$python = (Assert-MissumCommand -Name 'python').Source
Invoke-ContextCheck 'stderr-success' $python @('-c', "import sys; print('Standardausgabe'); print('Normale Diagnose', file=sys.stderr); print('', file=sys.stderr)")
$success = Get-Content -LiteralPath (Join-Path $evidence 'stderr-success.log') -Raw
if ($success -notmatch 'Standardausgabe' -or $success -notmatch 'Normale Diagnose' -or $success -match 'NativeCommandError|RemoteException|CategoryInfo|FullyQualifiedErrorId') {
    throw 'Normal stderr must stay readable diagnostic text, preserving both output streams.'
}
$failed = $false
try { Invoke-ContextCheck 'stderr-failure' $python @('-c', "import sys; print('Echte Fehlerdiagnose', file=sys.stderr); sys.exit(7)") }
catch {
    if ($_.Exception.Message -notmatch 'Pflichttest fehlgeschlagen: stderr-failure \(Exit 7\)') { throw }
    $failed = $true
}
if (-not $failed -or $checks.Count -ne 2 -or $checks[0].passed -ne $true -or $checks[1].exitCode -ne 7) {
    throw 'An actual failing process must still fail the build and preserve its exit code.'
}
$failure = Get-Content -LiteralPath (Join-Path $evidence 'stderr-failure.log') -Raw
if ($failure -notmatch 'Echte Fehlerdiagnose' -or $failure -match 'NativeCommandError') { throw 'Failure diagnostics were not retained as plain text.' }
Write-Host 'Build diagnostic regression passed: successful stderr stays diagnostic; exit 7 still fails.' -ForegroundColor Green

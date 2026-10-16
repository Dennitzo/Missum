#requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

# Exercise actual directory/sidecar moves with disposable text fixtures. No
# executable, model, original database or build command is started by this test.
$testRoot = Assert-MissumArtifactPath -Path (Resolve-MissumRepositoryPath -RelativePath ('artifacts\validation\publish-safety-' + [Guid]::NewGuid().ToString('N')))
$output = Join-Path $testRoot 'portable'
$stage = $output + '.staging-' + [Guid]::NewGuid().ToString('N')
$script:PublishSafetyFailTarget = $output + '.native-new.png'
$script:PublishSafetyFailOnce = $false
$script:PublishSafetyFakeProcess = $null

function Move-Item {
    [CmdletBinding()]
    param([string] $LiteralPath, [string] $Destination)
    if ($script:PublishSafetyFailOnce -and $Destination -eq $script:PublishSafetyFailTarget) {
        $script:PublishSafetyFailOnce = $false
        throw 'Intentional sidecar move failure.'
    }
    Microsoft.PowerShell.Management\Move-Item @PSBoundParameters
}

function Get-Process {
    [CmdletBinding()]
    param([string[]] $Name)
    if ($null -ne $script:PublishSafetyFakeProcess) { return $script:PublishSafetyFakeProcess }
    Microsoft.PowerShell.Management\Get-Process @PSBoundParameters
}

function Assert-PublishSafety {
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
}

try {
    New-Item -ItemType Directory -Path $output | Out-Null
    New-Item -ItemType Directory -Path $stage | Out-Null
    Set-Content -LiteralPath (Join-Path $output 'Missum.exe') -Value 'previous executable fixture' -Encoding UTF8
    Set-Content -LiteralPath ($output + '.manifest.json') -Value '{"generation":"previous"}' -Encoding UTF8
    Set-Content -LiteralPath ($output + '.native-old.json') -Value '{"generation":"previous"}' -Encoding UTF8
    Set-Content -LiteralPath ($output + '.user-notes.txt') -Value 'Preserve unrelated companion files.' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $stage 'Missum.exe') -Value 'new executable fixture' -Encoding UTF8
    Set-Content -LiteralPath ($stage + '.manifest.json') -Value '{"generation":"new"}' -Encoding UTF8
    Set-Content -LiteralPath ($stage + '.native-new.png') -Value 'new preview fixture' -Encoding UTF8
    $previousHash = (Get-FileHash -LiteralPath (Join-Path $output 'Missum.exe') -Algorithm SHA256).Hash
    $previousManifestHash = (Get-FileHash -LiteralPath ($output + '.manifest.json') -Algorithm SHA256).Hash
    $nextHash = (Get-FileHash -LiteralPath (Join-Path $stage 'Missum.exe') -Algorithm SHA256).Hash

    $script:PublishSafetyFakeProcess = [pscustomobject]@{ HasExited = $false; Id = 424242; Path = (Join-Path $output 'Missum.exe') }
    $blocked = $false
    try { $null = Install-MissumPublishStage -StageDirectory $stage -OutputDirectory $output }
    catch { $blocked = $_.Exception.Message -like '*in use by process*' }
    finally { $script:PublishSafetyFakeProcess = $null }
    Assert-PublishSafety $blocked 'A running executable must block replacement without being stopped.'
    Assert-PublishSafety ((Get-FileHash -LiteralPath (Join-Path $output 'Missum.exe') -Algorithm SHA256).Hash -eq $previousHash) 'The live-directory guard changed the previous artifact.'

    $script:PublishSafetyFailOnce = $true
    $rolledBack = $false
    try { $null = Install-MissumPublishStage -StageDirectory $stage -OutputDirectory $output }
    catch { $rolledBack = $_.Exception.Message -like '*Intentional sidecar move failure*' }
    Assert-PublishSafety $rolledBack 'The forced metadata move failure was not reported.'
    Assert-PublishSafety ((Get-FileHash -LiteralPath (Join-Path $output 'Missum.exe') -Algorithm SHA256).Hash -eq $previousHash) 'Failed installation did not restore the previous executable.'
    Assert-PublishSafety ((Get-FileHash -LiteralPath ($output + '.manifest.json') -Algorithm SHA256).Hash -eq $previousManifestHash) 'Failed installation did not restore the previous manifest.'
    Assert-PublishSafety (Test-Path -LiteralPath ($output + '.native-old.json')) 'Failed installation did not restore the previous smoke sidecar.'
    Assert-PublishSafety (Test-Path -LiteralPath ($stage + '.native-new.png')) 'Rollback lost the new staging sidecar.'

    $null = Install-MissumPublishStage -StageDirectory $stage -OutputDirectory $output
    Assert-PublishSafety ((Get-FileHash -LiteralPath (Join-Path $output 'Missum.exe') -Algorithm SHA256).Hash -eq $nextHash) 'Successful installation did not publish the new executable.'
    Assert-PublishSafety ((Get-Content -LiteralPath ($output + '.manifest.json') -Raw | ConvertFrom-Json).generation -eq 'new') 'Successful installation did not publish the new manifest.'
    Assert-PublishSafety (Test-Path -LiteralPath ($output + '.native-new.png')) 'New smoke sidecar was not renamed to the final artifact.'
    Assert-PublishSafety (-not (Test-Path -LiteralPath ($output + '.native-old.json'))) 'Old smoke sidecars remain attached to the new artifact.'
    Assert-PublishSafety (Test-Path -LiteralPath ($output + '.user-notes.txt')) 'Unrelated companion files were removed.'
    Assert-PublishSafety (-not (Test-Path -LiteralPath $stage)) 'Successful installation left its staging directory behind.'

    $escapeRejected = $false
    try { $null = Assert-MissumArtifactPath -Path (Join-Path $testRoot '..\..\..\outside-artifacts') }
    catch { $escapeRejected = $true }
    Assert-PublishSafety $escapeRejected 'Artifact paths that escape the workspace artifact tree must be rejected.'
    Write-Host 'Publish safety passed: live-directory guard, rollback, manifest/sidecars, path confinement.' -ForegroundColor Green
}
finally {
    $script:PublishSafetyFakeProcess = $null
    if (Test-Path -LiteralPath $testRoot) {
        $null = Assert-MissumArtifactTree -Path $testRoot
        Assert-MissumPublishDirectoryIdle -Path $testRoot
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}

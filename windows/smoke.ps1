#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PublishDirectory,

    [string] $ManifestPath,

    [ValidateSet('Folder', 'SingleFile')]
    [string] $Mode = 'Folder'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$PublishDirectory = Assert-MissumArtifactPath -Path $PublishDirectory
if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory does not exist: $PublishDirectory"
}

$requiredFiles = @('Missum.exe')
foreach ($file in $requiredFiles) {
    $path = Join-Path $PublishDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Smoke check failed; required file is missing: $path"
    }
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = $PublishDirectory + '.manifest.json'
}
$ManifestPath = Assert-MissumArtifactPath -Path $ManifestPath
if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    throw "Smoke check failed; publish manifest is missing: $ManifestPath"
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 'missum.windows.publish.v1' -or $manifest.mode -ne $Mode -or $manifest.runtimeIdentifier -ne 'win-x64') {
    throw 'Smoke check failed; publish manifest does not match the Missum schema, mode or runtime.'
}
$executable = Join-Path $PublishDirectory 'Missum.exe'
if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $manifest.executableSha256) {
    throw 'Smoke check failed; Missum.exe does not match the publish manifest.'
}
$publishedFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File)
$manifestFiles = @($manifest.files)
if ($manifestFiles.Count -ne $publishedFiles.Count) {
    throw 'Smoke check failed; published file count differs from the manifest.'
}
$publishPrefix = $PublishDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$seenPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifestFiles) {
    if ([string]::IsNullOrWhiteSpace($entry.path) -or [IO.Path]::IsPathRooted($entry.path)) {
        throw 'Smoke check failed; manifest contains an invalid relative path.'
    }
    $filePath = [IO.Path]::GetFullPath((Join-Path $PublishDirectory $entry.path))
    if (-not $filePath.StartsWith($publishPrefix, [StringComparison]::OrdinalIgnoreCase) -or -not $seenPaths.Add($filePath)) {
        throw "Smoke check failed; manifest path escapes the artifact or is duplicated: $($entry.path)"
    }
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
        (Get-Item -LiteralPath $filePath).Length -ne $entry.length -or
        (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Smoke check failed; published file differs from manifest: $($entry.path)"
    }
}

if ($Mode -eq 'Folder') {
    $sqlite = Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter 'e_sqlite3.dll' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $sqlite) {
        throw 'Smoke check failed; native local SQLite runtime e_sqlite3.dll is missing.'
    }
}
else {
    # The desktop is one executable. Its separate extension process may carry
    # its own runtime below ExtensionHost, without becoming a desktop sidecar.
    $sidecarDlls = @(Get-ChildItem -LiteralPath $PublishDirectory -File -Filter '*.dll' -ErrorAction SilentlyContinue)
    if ($sidecarDlls.Count -ne 0) {
        throw ("Smoke check failed; SingleFile app still requires DLL sidecars: " +
            (($sidecarDlls | ForEach-Object { $_.FullName }) -join ', '))
    }


    $runtimeSidecars = @(Get-ChildItem -LiteralPath $PublishDirectory -File | Where-Object { $_.Name -ne 'Missum.exe' })
    if ($runtimeSidecars.Count -ne 0) {
        throw ("Smoke check failed; SingleFile directory contains runtime sidecars: " +
            (($runtimeSidecars | ForEach-Object { $_.FullName }) -join ', '))
    }
}

$extensionHost = Join-Path $PublishDirectory 'ExtensionHost\ExtensionHost.exe'
if (-not (Test-Path -LiteralPath $extensionHost -PathType Leaf) -or (Get-Item -LiteralPath $extensionHost).Length -eq 0) {
    throw 'Smoke check failed; the local extension process ExtensionHost.exe is missing or empty.'
}

$smokeBase = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'Missum-Smoke'))
$smokeRoot = [IO.Path]::GetFullPath((Join-Path $smokeBase ([Guid]::NewGuid().ToString('N'))))
if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe smoke directory: $smokeRoot"
}
if (Test-Path -LiteralPath $smokeRoot) { throw "Smoke directory already exists: $smokeRoot" }
New-Item -ItemType Directory -Path $smokeRoot | Out-Null
$smokeData = Join-Path $smokeRoot 'Data'
$smokeInstance = [Guid]::NewGuid().ToString('N')
# The smoke must not bind the original application's LAN port. The OS chooses
# an available loopback port; skip configured gateway/native/control ports.
$reservedPorts = [Collections.Generic.HashSet[int]]::new()
foreach ($reserved in @(8080, 8081, 8082, 8090, 8180, 8181, 8182)) { $null = $reservedPorts.Add($reserved) }
foreach ($portName in @('ASSISTANT_GATEWAY_PORT', 'ASSISTANT_NATIVE_PORT', 'ASSISTANT_NATIVE_CONTROL_PORT', 'ASSISTANT_LAN_WEB_PORT')) {
    $configuredPort = 0
    if ([int]::TryParse([Environment]::GetEnvironmentVariable($portName, 'Process'), [ref]$configuredPort) -and
        $configuredPort -ge 1024 -and $configuredPort -le 65535) {
        $null = $reservedPorts.Add($configuredPort)
        if ($portName -eq 'ASSISTANT_NATIVE_PORT' -and $configuredPort -lt 65535) { $null = $reservedPorts.Add($configuredPort + 1) }
    }
}
$configuredGatewayText = [Environment]::GetEnvironmentVariable('ASSISTANT_GATEWAY_URL', 'Process')
[Uri]$configuredGateway = $null
if (-not [string]::IsNullOrWhiteSpace($configuredGatewayText) -and
    [Uri]::TryCreate([string]$configuredGatewayText, [UriKind]::Absolute, [ref]$configuredGateway)) {
    $null = $reservedPorts.Add($configuredGateway.Port)
}
$smokeLanPort = 0
for ($attempt = 0; $attempt -lt 32 -and $smokeLanPort -eq 0; $attempt++) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $candidatePort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        if ($candidatePort -ge 1024 -and -not $reservedPorts.Contains($candidatePort)) { $smokeLanPort = $candidatePort }
    }
    finally { $listener.Stop() }
}
if ($smokeLanPort -eq 0) { throw 'No isolated LAN port could be allocated for the smoke.' }
$smokeEnvironment = @{
    ASSISTANT_PROFILE = 'stable'
    ASSISTANT_DATA_ROOT = $smokeData
    ASSISTANT_INSTANCE_KEY = $smokeInstance
    ASSISTANT_NATIVE_STATE_ROOT = (Join-Path $smokeRoot 'NativeRuntime')
    ASSISTANT_DISABLE_RUNTIME_AUTOSTART = '1'
    ASSISTANT_LAN_WEB_PORT = [string]$smokeLanPort
    MISSUM_DATA_DIRECTORY = $smokeData
    MISSUM_SMOKE_INSTANCE_KEY = $smokeInstance
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $smokeRoot 'Bundle')
}
$previousEnvironment = @{}
$process = $null
try {
    foreach ($name in $smokeEnvironment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $smokeEnvironment[$name], 'Process')
    }
    $startedAt = [DateTime]::UtcNow
    $process = Start-Process -FilePath $executable -WorkingDirectory $PublishDirectory -PassThru -WindowStyle Hidden
    $runtimeFiles = @(
        (Join-Path $smokeData 'Missum.db'),
        (Join-Path $smokeData 'settings.json')
    )
    $nativeReady = Join-Path $smokeData 'native-ui-ready.json'
    $nativeState = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        if (Test-Path -LiteralPath $nativeReady -PathType Leaf) {
            # The writer may still be flushing its first JSON document.
            try { $nativeState = Get-Content -LiteralPath $nativeReady -Raw | ConvertFrom-Json }
            catch { $nativeState = $null }
            if ($null -ne $nativeState) { break }
        }
        Start-Sleep -Milliseconds 250
    }
    if ($process.HasExited) {
        $startupFailurePath = Join-Path $smokeData 'native-ui-failure.json'
        if (Test-Path -LiteralPath $startupFailurePath -PathType Leaf) {
            Copy-Item -LiteralPath $startupFailurePath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-ui-failure.json')) -Force
            foreach ($diagnosticName in @('native-simulation-errors.jsonl', 'simulation-view.jsonl', 'native-publication-errors.jsonl', 'publication-view.jsonl', 'native-ui-render-errors.jsonl', 'crash.log')) {
                $diagnosticPath = Join-Path (Join-Path $smokeData 'Diagnostics') $diagnosticName
                if (Test-Path -LiteralPath $diagnosticPath -PathType Leaf) {
                    Copy-Item -LiteralPath $diagnosticPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $diagnosticName)) -Force
                }
            }
            $startupFailure = Get-Content -LiteralPath $startupFailurePath -Raw | ConvertFrom-Json
            throw ("Native WinUI smoke failed: {0}" -f $startupFailure.error)
        }
        throw "Missum exited early: $($process.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $nativeReady)) {
        $nativeFailure = Join-Path $smokeData 'native-ui-failure.json'
        if (Test-Path -LiteralPath $nativeFailure -PathType Leaf) {
            $failureDetails = Get-Content -LiteralPath $nativeFailure -Raw | ConvertFrom-Json
            throw ("Native WinUI smoke failed: {0}" -f $failureDetails.error)
        }
        throw 'Native WinUI startup did not complete.'
    }
    $readySession = [Guid]::Empty
    if ($null -eq $nativeState -or $nativeState.renderer -ne 'WinUI3' -or
        $nativeState.page -ne 'NativeAssistantPage' -or $nativeState.ready -ne $true -or
        $nativeState.navigationSmokeVisits -ne 5 -or
        -not [Guid]::TryParse([string]$nativeState.sessionId, [ref]$readySession) -or $readySession -eq [Guid]::Empty -or
        (Get-Item -LiteralPath $nativeReady).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native chat did not freshly initialize a valid session in the isolated smoke profile.'
    }
    foreach ($runtimeFile in $runtimeFiles) {
        if (-not (Test-Path -LiteralPath $runtimeFile -PathType Leaf) -or (Get-Item -LiteralPath $runtimeFile).Length -eq 0) {
            throw "Native chat did not initialize its isolated local state: $runtimeFile"
        }
    }
    $webViewProfileRoot = Join-Path $smokeData 'WebView2'
    if (Test-Path -LiteralPath $webViewProfileRoot) {
        $unexpectedWebProfiles = @(Get-ChildItem -LiteralPath $webViewProfileRoot | Where-Object { -not $_.PSIsContainer -or $_.Name -notin @('Simulations', 'Publications') })
        if ($unexpectedWebProfiles.Count -gt 0) { throw 'Desktop unexpectedly created a WebView2 profile outside the interactive Simulation view.' }
    }
    $mathPreview = Join-Path $smokeData 'native-math-preview.png'
    if (-not (Test-Path -LiteralPath $mathPreview -PathType Leaf) -or (Get-Item -LiteralPath $mathPreview).Length -lt 100) {
        throw 'Native math smoke did not produce a rendered formula preview.'
    }
    $mathEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.math-preview.png')
    Copy-Item -LiteralPath $mathPreview -Destination $mathEvidence -Force
    Write-Host "Native math rendering and streaming verified: $mathEvidence"
    $chatStreamingPath = Join-Path $smokeData 'native-chat-streaming-validation.json'
    if (-not (Test-Path -LiteralPath $chatStreamingPath -PathType Leaf)) { throw 'Missing native chat streaming validation.' }
    $chatStreaming = Get-Content -LiteralPath $chatStreamingPath -Raw | ConvertFrom-Json
    if ($chatStreaming.passed -ne $true -or $chatStreaming.renderer -ne 'WinUI3' -or $chatStreaming.visibleDeltasBeforeCompletion -lt 3 -or $chatStreaming.chatCursorAbsent -ne $true) {
        throw 'Native chat did not render incremental answer text without a decorative cursor before completion.'
    }
    Copy-Item -LiteralPath $chatStreamingPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.chat-streaming-validation.json')) -Force
    foreach ($chatPreviewName in @('native-table-math-preview', 'native-table-narrow-preview', 'native-thinking-expanded-preview', 'native-thinking-processing-preview')) {
        $chatPreview = Join-Path $smokeData ($chatPreviewName + '.png')
        if (-not (Test-Path -LiteralPath $chatPreview -PathType Leaf) -or (Get-Item -LiteralPath $chatPreview).Length -lt 100) {
            throw "Missing native chat rendering preview: $chatPreviewName"
        }
        Copy-Item -LiteralPath $chatPreview -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $chatPreviewName + '.png')) -Force
    }
    Write-Host 'Native tables, matching formula sizes, answer streaming and reasoning disclosure verified.'
    $headingValidationPath = Join-Path $smokeData 'native-markdown-headings-validation.json'
    if (-not (Test-Path -LiteralPath $headingValidationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $headingValidationPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Missing or stale native Markdown heading validation.'
    }
    $headingValidation = Get-Content -LiteralPath $headingValidationPath -Raw | ConvertFrom-Json
    foreach ($headingCheck in @('passed', 'answerHeadings', 'reasoningHeadings', 'headingStreamRetainsControl', 'indentedModelDraft', 'mathHeadingTypeset', 'fencedCodePreserved')) {
        if ($headingValidation.PSObject.Properties.Name -notcontains $headingCheck -or $headingValidation.$headingCheck -ne $true) {
            throw "Native Markdown heading validation failed: $headingCheck"
        }
    }
    Copy-Item -LiteralPath $headingValidationPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-markdown-headings-validation.json')) -Force
    $headingPreview = Join-Path $smokeData 'native-markdown-headings-preview.png'
    if (-not (Test-Path -LiteralPath $headingPreview -PathType Leaf) -or (Get-Item -LiteralPath $headingPreview).Length -lt 100) {
        throw 'Missing native Markdown heading preview.'
    }
    Copy-Item -LiteralPath $headingPreview -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-markdown-headings-preview.png')) -Force
    Write-Host 'Native headings in streamed answers and reasoning verified.'
    $imageValidationPath = Join-Path $smokeData 'native-artifact-image-validation.json'
    if (-not (Test-Path -LiteralPath $imageValidationPath) -or
        (Get-Item -LiteralPath $imageValidationPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native original-image smoke did not produce fresh evidence.'
    }
    $imageValidation = Get-Content -LiteralPath $imageValidationPath -Raw | ConvertFrom-Json
    if ($imageValidation.processId -ne $process.Id -or $imageValidation.passed -ne $true) {
        throw 'Native original-image smoke failed.'
    }
    Copy-Item -LiteralPath $imageValidationPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-artifact-image-validation.json')) -Force
    foreach ($imagePreviewName in @('native-artifact-image-preview.png','native-artifact-image-narrow-preview.png','native-artifact-image-portrait-preview.png')) {
        $imagePreviewPath = Join-Path $smokeData $imagePreviewName
        if (-not (Test-Path -LiteralPath $imagePreviewPath) -or (Get-Item -LiteralPath $imagePreviewPath).Length -lt 100) {throw 'Native original-image preview evidence is missing.'}
        Copy-Item -LiteralPath $imagePreviewPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $imagePreviewName)) -Force
    }
    Write-Host 'Native original-image preview, responsive sizing and original-file opening verified.'
    if ($imageValidation.recovery.passed -ne $true -or $imageValidation.recovery.automaticRetryRecovered -ne $true) {
        throw 'Native original-image connection recovery failed.'
    }
    $imagePositionPath = Join-Path $smokeData 'native-artifact-position-validation.json'
    if (-not (Test-Path -LiteralPath $imagePositionPath -PathType Leaf) -or
        (Get-Item -LiteralPath $imagePositionPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native artifact position smoke did not produce fresh evidence.'
    }
    $imagePositions = Get-Content -LiteralPath $imagePositionPath -Raw | ConvertFrom-Json
    if ($imagePositions.processId -ne $process.Id -or $imagePositions.images -ne 2) {
        throw 'Native artifact position evidence does not match this process.'
    }
    foreach ($imagePositionCheck in @('passed','afterOwningToolStep','anchorsRetainedWhileStreaming','originalRecoveryRetainsAnchor','thumbnailsNotDuplicated','duplicateArtifactNotificationsCoalesced','persistedReloadRetainsPositions')) {
        if ($imagePositions.$imagePositionCheck -ne $true) { throw "Native artifact position check failed: $imagePositionCheck" }
    }
    Copy-Item -LiteralPath $imagePositionPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-artifact-position-validation.json')) -Force
    $imagePositionPreview = Join-Path $smokeData 'native-artifact-position-preview.png'
    if (-not (Test-Path -LiteralPath $imagePositionPreview -PathType Leaf) -or (Get-Item -LiteralPath $imagePositionPreview).Length -lt 100) {
        throw 'Native artifact position preview is missing.'
    }
    Copy-Item -LiteralPath $imagePositionPreview -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-artifact-position-preview.png')) -Force
    Write-Host 'Native image creation positions retained during streaming, original recovery and reload verified.'
    $looseMathValidationPath = Join-Path $smokeData 'native-loose-math-validation.json'
    if (-not (Test-Path -LiteralPath $looseMathValidationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $looseMathValidationPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Missing or stale native loose-math validation.'
    }
    $looseMathValidation = Get-Content -LiteralPath $looseMathValidationPath -Raw | ConvertFrom-Json
    if ($looseMathValidation.renderer -ne 'WinUI3' -or $looseMathValidation.visibleStreamingDeltas -lt 3) {
        throw 'Loose scientific formulas were not rendered by the native streaming path.'
    }
    foreach ($looseMathCheck in @('passed', 'answerMath', 'reasoningMath', 'formulaControlRetained', 'reasoningDisclosureRetained', 'incompleteFormulaPreserved', 'codePathsUrlsProtected', 'formulaFontMatchesText', 'chatCursorAbsent')) {
        if ($looseMathValidation.PSObject.Properties.Name -notcontains $looseMathCheck -or $looseMathValidation.$looseMathCheck -ne $true) {
            throw "Native loose-math validation failed: $looseMathCheck"
        }
    }
    Copy-Item -LiteralPath $looseMathValidationPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-loose-math-validation.json')) -Force
    foreach ($looseMathPreviewName in @('native-loose-math-answer-preview', 'native-loose-math-reasoning-preview')) {
        $looseMathPreview = Join-Path $smokeData ($looseMathPreviewName + '.png')
        if (-not (Test-Path -LiteralPath $looseMathPreview -PathType Leaf) -or (Get-Item -LiteralPath $looseMathPreview).Length -lt 100 -or
            (Get-Item -LiteralPath $looseMathPreview).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
            throw "Missing or stale native loose-math preview: $looseMathPreviewName"
        }
        Copy-Item -LiteralPath $looseMathPreview -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $looseMathPreviewName + '.png')) -Force
    }
    Write-Host 'Loose scientific formulas, protected literals, matching sizes and answer/reasoning streaming verified.'
    foreach ($composerPreviewName in @('native-composer-preview', 'native-composer-hover-preview', 'native-composer-narrow-preview', 'native-selection-preview')) {
        $composerPreview = Join-Path $smokeData ($composerPreviewName + '.png')
        if (-not (Test-Path -LiteralPath $composerPreview -PathType Leaf)) { throw "Missing native composer preview: $composerPreviewName" }
        $composerEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $composerPreviewName + '.png')
        Copy-Item -LiteralPath $composerPreview -Destination $composerEvidence -Force
    }
    Write-Host 'Native composer footer, removal affordance, narrow layout and session notices verified.'
    $subagentValidationPath = Join-Path $smokeData 'native-subagent-validation.json'
    if (-not (Test-Path -LiteralPath $subagentValidationPath -PathType Leaf) -or
        (Get-Item -LiteralPath $subagentValidationPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native subagent smoke did not produce a fresh validation report.'
    }
    $subagentValidation = Get-Content -LiteralPath $subagentValidationPath -Raw | ConvertFrom-Json
    if ($subagentValidation.renderer -ne 'WinUI3') {
        throw 'Native subagent validation did not use the real WinUI renderer.'
    }
    foreach ($subagentCheck in @('passed', 'parentStable', 'childUsesNativeRenderer', 'overlayAboveSources', 'closableAndReopenable', 'parentDraftPreserved', 'parentRunRemainedActive', 'chatCursorAbsent', 'childModelIdentityPreserved', 'compactLifecycleRow', 'lifecycleStartEntryPreserved', 'lifecycleCompletionPostedOnce', 'lifecycleCompletionWaitsForDelivery', 'lifecycleClickOpensChild', 'lifecycleKeyboardAccessible', 'acceptedManagerStepsCoalesced', 'compactSubagentSummary', 'activeInactiveCountsCorrect', 'summaryControlsStable', 'summaryClickOpensOverview', 'noSubagentAllLink', 'completeSubagentOverview', 'overviewSessionIsolated', 'uniquePlanetIcons', 'planetIdentityConsistent', 'historicalTabsStayClosed', 'closedRunningTabSurvivesRehydration')) {
        if ($subagentValidation.PSObject.Properties.Name -notcontains $subagentCheck -or $subagentValidation.$subagentCheck -ne $true) {
            throw "Native subagent smoke failed its required check: $subagentCheck"
        }
    }
    $expectsLiveSubagent = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('MISSUM_SMOKE_SUBAGENT_INPUT', 'Process'))
    if ($subagentValidation.PSObject.Properties.Name -notcontains 'liveTranscriptRendered' -or
        $subagentValidation.liveTranscriptRendered -ne $expectsLiveSubagent -or
        $subagentValidation.PSObject.Properties.Name -notcontains 'liveLifecycleClickOpensChild' -or
        $subagentValidation.liveLifecycleClickOpensChild -ne $expectsLiveSubagent) {
        throw 'Native subagent validation does not match the supplied real-model transcript.'
    }
    $subagentValidationEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-subagent-validation.json')
    Copy-Item -LiteralPath $subagentValidationPath -Destination $subagentValidationEvidence -Force
    if ((Get-FileHash -LiteralPath $subagentValidationPath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $subagentValidationEvidence -Algorithm SHA256).Hash) {
        throw 'Native subagent validation evidence was not copied intact.'
    }
    $subagentPreviewNames = @('native-subagents-outputs-preview', 'native-subagents-active-summary-preview', 'native-subagents-finished-summary-preview', 'native-subagent-chat-preview', 'native-subagent-lifecycle-preview', 'native-subagent-overview-preview')
    if ($expectsLiveSubagent) {
        $subagentPreviewNames += @('native-subagent-live-chat-preview', 'native-subagent-live-response-preview', 'native-subagent-live-outputs-preview', 'native-subagent-live-lifecycle-preview', 'native-subagent-live-overview-preview')
    }
    foreach ($subagentPreviewName in $subagentPreviewNames) {
        $subagentPreviewPath = Join-Path $smokeData ($subagentPreviewName + '.png')
        if (-not (Test-Path -LiteralPath $subagentPreviewPath -PathType Leaf) -or
            (Get-Item -LiteralPath $subagentPreviewPath).Length -lt 100 -or
            (Get-Item -LiteralPath $subagentPreviewPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
            throw "Missing or stale rendered native subagent preview: $subagentPreviewName"
        }
        $subagentPreviewBytes = [IO.File]::ReadAllBytes($subagentPreviewPath)
        if ([Convert]::ToBase64String($subagentPreviewBytes, 0, 8) -ne 'iVBORw0KGgo=') {
            throw "Native subagent preview is not a PNG: $subagentPreviewName"
        }
        $subagentPreviewWidth = ([int]$subagentPreviewBytes[16] -shl 24) -bor ([int]$subagentPreviewBytes[17] -shl 16) -bor ([int]$subagentPreviewBytes[18] -shl 8) -bor [int]$subagentPreviewBytes[19]
        $subagentPreviewHeight = ([int]$subagentPreviewBytes[20] -shl 24) -bor ([int]$subagentPreviewBytes[21] -shl 16) -bor ([int]$subagentPreviewBytes[22] -shl 8) -bor [int]$subagentPreviewBytes[23]
        if ($subagentPreviewWidth -lt 100 -or $subagentPreviewHeight -lt 60) {
            throw "Native subagent preview has no visible layout: $subagentPreviewName"
        }
        $subagentPreviewEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $subagentPreviewName + '.png')
        Copy-Item -LiteralPath $subagentPreviewPath -Destination $subagentPreviewEvidence -Force
        if ((Get-FileHash -LiteralPath $subagentPreviewPath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $subagentPreviewEvidence -Algorithm SHA256).Hash) {
            throw "Native subagent preview evidence was not copied intact: $subagentPreviewName"
        }
    }
    Write-Host "Native subagent transcript, tabs, parent stability and output order verified: $subagentValidationEvidence"
    $planetValidationPath = Join-Path $smokeData 'native-planet-palette-validation.json'
    $planetPreviewPath = Join-Path $smokeData 'native-planet-palette-preview.png'
    foreach ($planetEvidencePath in @($planetValidationPath, $planetPreviewPath)) {
        if (-not (Test-Path -LiteralPath $planetEvidencePath -PathType Leaf) -or
            (Get-Item -LiteralPath $planetEvidencePath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
            throw 'Native planet palette smoke did not produce fresh rendered evidence.'
        }
    }
    $planetValidation = Get-Content -LiteralPath $planetValidationPath -Raw | ConvertFrom-Json
    if ($planetValidation.renderer -ne 'WinUI3' -or $planetValidation.passed -ne $true -or
        $planetValidation.nativeVectors -ne $true -or $planetValidation.paletteVersion -ne 'planets-v1' -or
        $planetValidation.paletteCount -lt 4096 -or $planetValidation.renderedSamples -ne 128 -or
        $planetValidation.distinctSmallIcons -ne $planetValidation.renderedSamples -or $planetValidation.smallIconDip -ne 14) {
        throw 'Native planet palette failed its small-icon uniqueness checks.'
    }
    Copy-Item -LiteralPath $planetValidationPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-planet-palette-validation.json')) -Force
    Copy-Item -LiteralPath $planetPreviewPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-planet-palette-preview.png')) -Force
    Write-Host 'Native planet palette: 128 distinct rendered planets at 14 DIP verified.'
    $tabRetentionPath = Join-Path $smokeData 'native-tab-retention-validation.json'
    $callbackGuardPath = Join-Path $smokeData 'native-ui-callback-validation.json'
    foreach ($projectionEvidencePath in @($tabRetentionPath, $callbackGuardPath)) {
        if (-not (Test-Path -LiteralPath $projectionEvidencePath -PathType Leaf) -or
            (Get-Item -LiteralPath $projectionEvidencePath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
            throw 'Native UI projection smoke did not produce fresh validation evidence.'
        }
    }
    $tabRetention = Get-Content -LiteralPath $tabRetentionPath -Raw | ConvertFrom-Json
    if ($tabRetention.tokenUpdates -ne 100) {
        throw 'Native tab retention smoke did not exercise 100 background projections.'
    }
    foreach ($tabCheck in @('passed', 'retainedControls', 'retainedOrder', 'hoverPreserved', 'focusPreserved', 'noVisualDetach', 'appearancePropertiesStable', 'mutablePaletteResourcesRetained', 'explicitNavigationUpdatesAppearance', 'titleChangesStillVisible')) {
        if ($tabRetention.PSObject.Properties.Name -notcontains $tabCheck -or $tabRetention.$tabCheck -ne $true) {
            throw "Native tab retention smoke failed: $tabCheck"
        }
    }
    $callbackGuard = Get-Content -LiteralPath $callbackGuardPath -Raw | ConvertFrom-Json
    if ($callbackGuard.processId -ne $process.Id -or $callbackGuard.timerMilliseconds -ne 80 -or
        $callbackGuard.injected -ne 'COM_E_FAIL' -or $callbackGuard.caughtFailures -ne 1) {
        throw 'Native UI callback smoke did not exercise the real process and dispatcher timer.'
    }
    foreach ($callbackCheck in @('nextProjectionSucceeded', 'ownerUnchanged', 'messagesUnchanged', 'runUnchanged')) {
        if ($callbackGuard.PSObject.Properties.Name -notcontains $callbackCheck -or $callbackGuard.$callbackCheck -ne $true) {
            throw "Native UI callback smoke failed: $callbackCheck"
        }
    }
    Copy-Item -LiteralPath $tabRetentionPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-tab-retention-validation.json')) -Force
    Copy-Item -LiteralPath $callbackGuardPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-ui-callback-validation.json')) -Force
    Write-Host 'Native tab hover/focus retention and dispatcher-timer error recovery verified.'
    $noticePath = Join-Path $smokeData 'native-notice-selection-validation.json'
    if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or
        (Get-Item -LiteralPath $noticePath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native error-text selection smoke did not produce fresh evidence.'
    }
    $noticeValidation = Get-Content -LiteralPath $noticePath -Raw | ConvertFrom-Json
    foreach ($noticeCheck in @('passed', 'textSelectionEnabled', 'fullTextSelected', 'exactCopyText', 'latexAndLineBreaksPreserved', 'runtimeUpdatesRetainTemplate', 'noDuplicateMessage', 'existingActionPreserved', 'existingContextMenuPreserved', 'attachmentIdempotent')) {
        if ($noticeValidation.PSObject.Properties.Name -notcontains $noticeCheck -or $noticeValidation.$noticeCheck -ne $true) {
            throw "Native error-text selection smoke failed: $noticeCheck"
        }
    }
    Copy-Item -LiteralPath $noticePath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-notice-selection-validation.json')) -Force
    Write-Host 'Native selectable errors and exact LaTeX/Unicode copy text verified.'
    $simulationLifecyclePath = Join-Path $smokeData 'native-simulation-lifecycle-validation.json'
    if (-not (Test-Path -LiteralPath $simulationLifecyclePath -PathType Leaf) -or
        (Get-Item -LiteralPath $simulationLifecyclePath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
        throw 'Native simulation lifecycle smoke did not produce fresh evidence.'
    }
    $simulationLifecycle = Get-Content -LiteralPath $simulationLifecyclePath -Raw | ConvertFrom-Json
    if ($simulationLifecycle.passed -ne $true -or $simulationLifecycle.processId -ne $process.Id -or
        $simulationLifecycle.tabCycles -lt 40 -or $simulationLifecycle.suspendResumeCycles -lt 4 -or $simulationLifecycle.navigationCount -ne 3 -or
        -not $simulationLifecycle.controls.passed -or $simulationLifecycle.explicitReloadNavigations -ne 1 -or
        $simulationLifecycle.browserFaults -ne 0 -or $simulationLifecycle.uiFaults -ne 0) {
        throw 'Native interactive simulation failed the real-process tab lifecycle checks.'
    }
    foreach ($simulationCheck in @('stableRoot', 'stableBrowserParent', 'realCanvasAnimation', 'animationResumed')) {
        if ($simulationLifecycle.PSObject.Properties.Name -notcontains $simulationCheck -or $simulationLifecycle.$simulationCheck -ne $true) {
            throw "Native interactive simulation lifecycle failed: $simulationCheck"
        }
    }
    Copy-Item -LiteralPath $simulationLifecyclePath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-simulation-lifecycle-validation.json')) -Force
    Write-Host 'Native interactive simulation: 40 tab cycles and snapshot updates verified.'
    if (-not [string]::IsNullOrWhiteSpace($env:MISSUM_SCIENCE_PDF_SMOKE_PATH)) {
        $publicationLayoutPath = Join-Path $smokeData 'native-publication-layout-validation.json'
        if (-not (Test-Path -LiteralPath $publicationLayoutPath -PathType Leaf) -or
            (Get-Item -LiteralPath $publicationLayoutPath).LastWriteTimeUtc -lt $startedAt.AddSeconds(-1)) {
            throw 'Native publication layout smoke did not produce fresh evidence.'
        }
        $publicationLayout = Get-Content -LiteralPath $publicationLayoutPath -Raw | ConvertFrom-Json
        if ($publicationLayout.processId -ne $process.Id -or $publicationLayout.viewportWidth -le 0 -or
            [Math]::Abs($publicationLayout.viewerWidth - $publicationLayout.viewportWidth) -gt 1 -or
            $simulationLifecycle.publicationFixtureLoaded -ne $true) {
            throw 'The native PDF does not fit the complete available viewport.'
        }
        foreach ($publicationCheck in @('passed', 'fullWidth', 'headingsHidden', 'externalPdfButtonsRemoved', 'browserViewer')) {
            if ($publicationLayout.PSObject.Properties.Name -notcontains $publicationCheck -or $publicationLayout.$publicationCheck -ne $true) {
                throw "Native publication layout failed: $publicationCheck"
            }
        }
        Copy-Item -LiteralPath $publicationLayoutPath -Destination (Assert-MissumArtifactPath -Path ($PublishDirectory + '.native-publication-layout-validation.json')) -Force
        Write-Host 'Native publication: full viewport width without headings or PDF buttons verified.'
    }
    foreach ($sciencePreview in @('native-outputs-preview', 'native-publication-preview', 'native-publication-last-page-preview', 'native-simulation-empty-preview', 'native-simulation-lifecycle-preview', 'native-python-receipt-preview', 'native-changes-preview', 'native-tool-icons-preview', 'native-colored-chrome-preview', 'native-continuation-preview', 'native-continuation-preparing-preview', 'native-continuation-loading-preview', 'native-thinking-preview')) {
        $scienceImage = Join-Path $smokeData ($sciencePreview + '.png')
        if (Test-Path -LiteralPath $scienceImage -PathType Leaf) {
            $scienceEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.' + $sciencePreview + '.png')
            Copy-Item -LiteralPath $scienceImage -Destination $scienceEvidence -Force
        }
    }
    $liveMathPreview = Join-Path $smokeData 'native-math-live-preview.png'
    if (Test-Path -LiteralPath $liveMathPreview -PathType Leaf) {
        $liveMathEvidence = Assert-MissumArtifactPath -Path ($PublishDirectory + '.math-live-preview.png')
        Copy-Item -LiteralPath $liveMathPreview -Destination $liveMathEvidence -Force
        Write-Host "Native rendering of the supplied model response verified: $liveMathEvidence"
    }
    $runtimeContentDirectory = $PublishDirectory
    if ($Mode -eq 'SingleFile') {
        $extractedAssemblies = @(Get-ChildItem -LiteralPath $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Recurse -File -Filter 'Missum.dll' -ErrorAction SilentlyContinue)
        if ($extractedAssemblies.Count -ne 1) {
            throw "Runtime smoke failed; expected one extracted Missum.dll under $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR, found $($extractedAssemblies.Count)."
        }
        $runtimeContentDirectory = $extractedAssemblies[0].DirectoryName
    }
    $nativeRuntimeFiles = @(
        'Assets\NativeRuntime\windows\manage-coding-llama.ps1',
        'Assets\NativeRuntime\windows\manage-llama-server.ps1',
        'Assets\NativeRuntime\workers\coding\catalog.py',
        'Assets\NativeRuntime\workers\coding\session_cache.py'
    )
    foreach ($nativeRuntimeFile in $nativeRuntimeFiles) {
        $nativeRuntimePath = Join-Path $runtimeContentDirectory $nativeRuntimeFile
        if (-not (Test-Path -LiteralPath $nativeRuntimePath -PathType Leaf) -or
            (Get-Item -LiteralPath $nativeRuntimePath).Length -eq 0) {
            throw "Runtime smoke failed; bundled native model runtime support is missing or empty: $nativeRuntimePath"
        }
        $sourceNativeRuntimePath = Resolve-MissumRepositoryPath -RelativePath ($nativeRuntimeFile.Substring('Assets\NativeRuntime\'.Length))
        if ((Get-FileHash -LiteralPath $sourceNativeRuntimePath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $nativeRuntimePath -Algorithm SHA256).Hash) {
            throw "Runtime smoke failed; bundled native model runtime differs from current source: $nativeRuntimeFile"
        }
    }
    Write-Host "Native model runtime support verified: $runtimeContentDirectory"

    Write-Host "Native WinUI 3 session and isolated local state verified: $($nativeState.sessionId)"

    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    $smokeRoot = [IO.Path]::GetFullPath($smokeRoot)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($smokeRoot), $smokeBase, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unsafe smoke directory: $smokeRoot"
    }
    if (Test-Path -LiteralPath $smokeRoot) {
        for ($attempt = 1; $attempt -le 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $smokeRoot -Recurse -Force -ErrorAction Stop
                break
            }
            catch {
                if ($attempt -eq 20) {
                    throw
                }
                Start-Sleep -Milliseconds 250
            }
        }
    }
}

Write-Host "Smoke checks passed: $PublishDirectory" -ForegroundColor Green



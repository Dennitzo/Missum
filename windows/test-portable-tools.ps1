#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $ServerUrl = 'http://127.0.0.1:8080',

    [string] $DeepSeekModelId,

    [string] $DeepSeekVisionModelId,

    [Parameter(Mandatory = $true)]
    [string] $PortableExecutable,

    [string] $EvidenceDirectory
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$testProject = Resolve-MissumRepositoryPath -RelativePath 'tests\Missum.Tests\Missum.Tests.csproj'
$portable = Assert-MissumArtifactPath -Path $PortableExecutable
if (-not (Test-Path -LiteralPath $portable -PathType Leaf)) {
    throw "Portable tool acceptance requires the published executable: $portable"
}
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
    $EvidenceDirectory = Resolve-MissumRepositoryPath -RelativePath ("artifacts\validation\portable-tools\{0}" -f $stamp)
}
else {
    $EvidenceDirectory = Assert-MissumArtifactPath -Path $EvidenceDirectory
}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null

$portableRunRoot = Join-Path $EvidenceDirectory ("portable-runtime-{0}" -f [Guid]::NewGuid().ToString('N'))
$portableDataDirectory = Join-Path $portableRunRoot 'Data'
$portableNativeStateDirectory = Join-Path $portableRunRoot 'NativeRuntime'
$portableBundleDirectory = Join-Path $portableRunRoot 'Bundle'
$portableReadyPath = Join-Path $portableDataDirectory 'native-ui-ready.json'
$portableProcess = $null
$portableProcessId = $null
$portableMainWindowHandle = 0L
$portableNativeState = $null
$portableStartedAt = $null
$portableSession = [Guid]::Empty
$portableReady = $false
$portableStoppedCleanly = $false

$baseUrl = $ServerUrl.Trim().TrimEnd('/')
if ($baseUrl -notmatch '^https?://') { throw "ServerUrl must be an HTTP(S) URL: $ServerUrl" }
$coverage = @(
    [ordered]@{ actionId = 'builtin.workspace/attach-files-and-folders'; action = 'Dateien und Ordner'; kind = 'native-immediate'; toolPath = 'FileOpenPicker -> ImportDocument/ImportAttachment'; suites = @('native-tool-contracts', 'portable publish smoke') },
    [ordered]@{ actionId = 'builtin.coding/plan-mode'; action = 'Planmodus'; kind = 'live-model'; toolPath = 'coding read-only tools'; suites = @('translation-and-plan-mode') },
    [ordered]@{ actionId = 'builtin.web/web-search'; action = 'Websuche'; kind = 'live-gateway'; toolPath = 'web.search + web.fetch via SearXNG'; suites = @('gateway-server-media-speech') },
    [ordered]@{ actionId = 'builtin.web/deep-research'; action = 'Tiefenrecherche (Deep Research)'; kind = 'live-model'; toolPath = 'web.deepResearch + persisted science project'; suites = @('claude-science-deep-research') },
    [ordered]@{ actionId = 'builtin.media/image-analysis'; action = 'Bild analysieren'; kind = 'live-media'; toolPath = 'window/file input -> media.analyze'; suites = @('native-session-ui', 'portable-preflight', 'gateway-server-media-speech', 'workspace-image-input') },
    [ordered]@{ actionId = 'builtin.media/audio-analysis'; action = 'Audio analysieren'; kind = 'live-media'; toolPath = 'session-scoped audio capture tab -> transcription -> media.analyze'; suites = @('native-session-ui', 'gateway-server-media-speech') },
    [ordered]@{ actionId = 'builtin.media/video-analysis'; action = 'Video analysieren'; kind = 'live-media'; toolPath = 'session-scoped window clip tab -> media.analyze'; suites = @('native-session-ui', 'portable-preflight', 'gateway-server-media-speech') },
    [ordered]@{ actionId = 'builtin.documents/create'; action = 'Dokument'; kind = 'live-client-tool'; toolPath = 'document.create + deterministic renderer'; suites = @('documents-and-audiobook') },
    [ordered]@{ actionId = 'builtin.image/generate'; action = 'Bild'; kind = 'live-worker'; toolPath = 'image.generate + artifact download/hash/decode'; suites = @('gateway-server-media-speech') },
    [ordered]@{ actionId = 'builtin.audiobook/create'; action = 'Hörbuch'; kind = 'live-client-tool'; toolPath = 'audiobook.create + paragraph speech lifecycle'; suites = @('documents-and-audiobook') },
    [ordered]@{ actionId = 'builtin.documents/export-chat-pdf'; action = 'Chat als PDF exportieren'; kind = 'native-immediate'; toolPath = 'native chat PDF exporter + save-dialog contract'; suites = @('native-tool-contracts', 'portable publish smoke') },
    [ordered]@{ actionId = 'builtin.speech/translate'; action = 'Übersetzen'; kind = 'live-model'; toolPath = 'translation prompt mode'; suites = @('translation-and-plan-mode') },
    [ordered]@{ actionId = 'builtin.speech/read-aloud'; action = 'Vorlesen'; kind = 'live-worker'; toolPath = 'speech.synthesize + verified audio artifact'; suites = @('documents-and-audiobook', 'gateway-server-media-speech') },
    [ordered]@{ actionId = 'builtin.speech/live-captions'; action = 'Live-Untertitel'; kind = 'live-worker'; toolPath = 'session-scoped caption tab + Whisper translation/persistence'; suites = @('native-session-ui', 'gateway-server-media-speech') }
)
if (@($coverage.actionId | Select-Object -Unique).Count -ne 14 -or $coverage.Count -ne 14) {
    throw 'Portable tool coverage contains duplicate or missing built-in action ids.'
}
$nativeUiCoverage = @(
    [ordered]@{ featureId = 'native-session-tool-tabs'; feature = 'Sitzungsgebundene Werkzeug-Tabs'; toolPath = 'session navigation + persistent media/caption tabs'; suites = @('native-session-ui') },
    [ordered]@{ featureId = 'native-prompt-timeline'; feature = 'Prompt-Zeitleiste'; toolPath = 'preview normalization + viewport/last-prompt selection'; suites = @('native-session-ui') },
    [ordered]@{ featureId = 'native-accent-contrast'; feature = 'Akzentfarben'; toolPath = 'readable foreground selection for bright and dark accents'; suites = @('native-session-ui') },
    [ordered]@{ featureId = 'native-media-window-selection'; feature = 'Medien- und Fensterauswahl'; toolPath = 'capture lifecycle + exact portable process window screenshot/clip + media.analyze'; suites = @('native-session-ui', 'portable-preflight') }
)
if (@($nativeUiCoverage.featureId | Select-Object -Unique).Count -ne 4 -or $nativeUiCoverage.Count -ne 4) {
    throw 'Portable native UI coverage contains duplicate or missing feature ids.'
}

function Write-PortableToolPreflightFailure {
    param([Parameter(Mandatory = $true)][string] $Message)
    [ordered]@{
        schema = 'missum.portable-tool-acceptance.v1'
        checkedAtUtc = [DateTime]::UtcNow.ToString('o')
        passed = $false
        phase = 'preflight'
        error = $Message
        portable = [ordered]@{
            path = $portable
            sha256 = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        serverUrl = $baseUrl
        coverage = $coverage
        nativeUiCoverage = $nativeUiCoverage
        checks = @()
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
    throw $Message
}
try {
    $status = Invoke-RestMethod -Uri ($baseUrl + '/v1/models/status') -TimeoutSec 30
    if ($null -eq $status.PSObject.Properties['providerReachable'] -or
        $null -eq $status.PSObject.Properties['models']) {
        throw 'The gateway returned an incomplete model-status response.'
    }
    if (-not [bool]$status.providerReachable) {
        $errorProperty = $status.PSObject.Properties['errorMessage']
        $detail = if ($null -eq $errorProperty -or [string]::IsNullOrWhiteSpace([string]$errorProperty.Value)) {
            'no runtime diagnostic was returned'
        }
        else { [string]$errorProperty.Value }
        throw "The model runtime is not reachable: $detail"
    }
}
catch {
    Write-PortableToolPreflightFailure -Message "Portable tool acceptance model preflight failed at $baseUrl/v1/models/status. Start the Missum AI stack first. $($_.Exception.Message)"
}

function Select-DeepSeekProfile {
    param(
        [Parameter(Mandatory = $true)][string] $Role,
        [string] $RequestedId,
        [switch] $RequiresVision
    )
    $profiles = @($status.models | Where-Object {
        $_.downloaded -and $_.supportsTools -and $_.role -eq $Role -and
        $_.id -match '(?i)deepseek' -and (-not $RequiresVision -or $_.supportsVision)
    })
    if (-not [string]::IsNullOrWhiteSpace($RequestedId)) {
        $profiles = @($profiles | Where-Object { $_.id -eq $RequestedId.Trim() })
    }
    if ($profiles.Count -eq 0) {
        $suffix = if ([string]::IsNullOrWhiteSpace($RequestedId)) { '' } else { " '$RequestedId'" }
        throw "No downloaded, tool-capable DeepSeek $Role profile$suffix is available."
    }
    if ($profiles.Count -gt 1 -and $Role -eq 'vision') {
        $nativeVision = @($profiles | Where-Object { $_.id -like 'vision/*' })
        if ($nativeVision.Count -eq 1) { return $nativeVision[0] }
    }
    return @($profiles | Sort-Object id)[0]
}

try {
    $general = Select-DeepSeekProfile -Role 'general' -RequestedId $DeepSeekModelId
    $coding = Select-DeepSeekProfile -Role 'coding' -RequestedId $DeepSeekModelId
    $vision = Select-DeepSeekProfile -Role 'vision' -RequestedId $DeepSeekVisionModelId -RequiresVision
    foreach ($profile in @($general, $coding, $vision)) {
        if (-not (@($profile.reasoningEfforts) -contains 'none')) {
            throw "DeepSeek profile '$($profile.id)' ($($profile.role)) does not expose explicit reasoning=none."
        }
    }
}
catch {
    Write-PortableToolPreflightFailure -Message $_.Exception.Message
}

$environmentValues = [ordered]@{
    MISSUM_AI_PORTABLE_TOOL_ACCEPTANCE = '1'
    MISSUM_AI_SERVER_URL = $baseUrl
    MISSUM_AI_LIVE_GENERAL_MODEL = [string]$general.id
    MISSUM_AI_LIVE_CODING_MODEL = [string]$coding.id
    MISSUM_AI_NATIVE_VISION_MODEL = [string]$vision.id
    MISSUM_AI_LIVE_REASONING_EFFORT = 'none'
    MISSUM_AI_LIVE_EVIDENCE_DIRECTORY = $EvidenceDirectory
    MISSUM_AI_GATEWAY_TOOLS_LIVE = '1'
    MISSUM_AI_ADDITIONAL_TOOLS_LIVE = '1'
    MISSUM_AI_BUILTIN_MODES_LIVE = '1'
    MISSUM_AI_CONTEXT_RETRIEVAL_LIVE = '1'
    MISSUM_AI_WORKSPACE_IMAGE_LIVE = '1'
    MISSUM_AI_CODING_LIVE = '1'
    MISSUM_AI_CODING_FILE_LIVE = '1'
    MISSUM_AI_SCIENCE_LIVE = '1'
    MISSUM_AI_SCIENCE_LIVE_TIMEOUT_MINUTES = '45'
    MISSUM_NATIVE_PDF_EXPORT_LIVE = '1'
    MISSUM_AI_SCIENCE_TOOLS_LIVE = '1'
    MISSUM_AI_PORTABLE_PROCESS_ID = ''
    ASSISTANT_PROFILE = 'stable'
    ASSISTANT_DATA_ROOT = $portableDataDirectory
    ASSISTANT_GATEWAY_URL = $baseUrl
    ASSISTANT_INSTANCE_KEY = [Guid]::NewGuid().ToString('N')
    ASSISTANT_NATIVE_STATE_ROOT = $portableNativeStateDirectory
    ASSISTANT_DISABLE_RUNTIME_AUTOSTART = '1'
    MISSUM_DATA_DIRECTORY = $portableDataDirectory
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = $portableBundleDirectory
}
$savedEnvironment = @{}

$suites = @(
    [pscustomobject]@{ Name = 'portable-preflight'; Filter = 'FullyQualifiedName~PortableToolAcceptanceTests'; ReasoningPath = 'real window screenshot and clip analysis return DeepSeek reasoning=none' },
    [pscustomobject]@{ Name = 'native-session-ui'; Filter = 'FullyQualifiedName~NativeNavigationStateTests|FullyQualifiedName~NativePromptTimelineStateTests|FullyQualifiedName~NativeAccentColorTests|FullyQualifiedName~MediaCaptureLifecycleTests|FullyQualifiedName~AssistantIntegrationTests.ComposerKeepsLiveDraftAndPersistentSessionToolsAcrossHostUpdates'; ReasoningPath = 'not applicable: native session isolation, timeline, accent contrast, and media capture lifecycle' },
    [pscustomobject]@{ Name = 'native-tool-contracts'; Filter = 'FullyQualifiedName~ExtensionFoundationTests|FullyQualifiedName~AssistantIntegrationTests|FullyQualifiedName~DocumentOfficeToolTests|FullyQualifiedName~WorkspaceToolTests|FullyQualifiedName~ScienceToolExecutionTests|FullyQualifiedName~NativeChatPdfExportTests|FullyQualifiedName~DatabaseTests|FullyQualifiedName~ProjectMemoryTests'; ReasoningPath = 'not applicable: no model request' },
    [pscustomobject]@{ Name = 'gateway-server-media-speech'; Filter = 'FullyQualifiedName~GatewayToolsLiveTests'; ReasoningPath = 'RunRequest and MediaJobRequest results assert reasoning=none' },
    [pscustomobject]@{ Name = 'documents-and-audiobook'; Filter = 'FullyQualifiedName~AdditionalToolsLiveTests'; ReasoningPath = 'AppSettings.ReasoningEffortsByModel=none' },
    [pscustomobject]@{ Name = 'translation-and-plan-mode'; Filter = 'FullyQualifiedName~BuiltinModesLiveTests'; ReasoningPath = 'AppSettings general/coding reasoning=none' },
    [pscustomobject]@{ Name = 'embedding-and-retrieval'; Filter = 'FullyQualifiedName~ContextRetrievalLiveTests'; ReasoningPath = 'RunRequest.ReasoningEffort=none' },
    [pscustomobject]@{ Name = 'workspace-image-input'; Filter = 'FullyQualifiedName~WorkspaceImageInputLiveTests'; ReasoningPath = 'DeepSeek vision media result asserts reasoning=none' },
    [pscustomobject]@{ Name = 'coding-tools'; Filter = 'FullyQualifiedName~CodingAgentLiveTests'; ReasoningPath = 'RunRequest.ReasoningEffort=none' },
    [pscustomobject]@{ Name = 'claude-science-deep-research'; Filter = 'FullyQualifiedName~ClaudeScienceLiveTests'; ReasoningPath = 'AppSettings.ReasoningEffortsByModel=none' }
)
$checks = [Collections.Generic.List[object]]::new()
$executionError = $null

function Invoke-PortableToolSuite {
    param([Parameter(Mandatory = $true)] $Suite)
    if ($null -eq $portableProcess -or $portableProcess.HasExited) {
        throw "Published Missum is not running before portable suite '$($Suite.Name)'."
    }
    $log = Join-Path $EvidenceDirectory ($Suite.Name + '.log')
    $trx = $Suite.Name + '.trx'
    $arguments = @(
        'test', $testProject,
        '--configuration', $Configuration,
        '-p:Platform=x64',
        '--no-build',
        '--nologo',
        '--filter', $Suite.Filter,
        '--blame-hang-timeout', '50m',
        '--results-directory', $EvidenceDirectory,
        '--logger', ("trx;LogFileName={0}" -f $trx),
        '--logger', 'console;verbosity=normal'
    )
    Write-Host ("Portable tool acceptance: {0}" -f $Suite.Name) -ForegroundColor Cyan
    $started = [DateTime]::UtcNow
    $savedErrorAction = $ErrorActionPreference
    $lines = @()
    $exitCode = -1
    $testCount = 0
    $verificationError = $null
    try {
        $ErrorActionPreference = 'Continue'
        $lines = & dotnet @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $portableProcess.Refresh()
        if ($portableProcess.HasExited) {
            $lines = @($lines) + "PORTABLE PROCESS FAILED: Missum exited during suite '$($Suite.Name)' with code $($portableProcess.ExitCode)."
            $exitCode = -3
        }
    }
    finally { $ErrorActionPreference = $savedErrorAction }
    $trxPath = Join-Path $EvidenceDirectory $trx
    if ($exitCode -eq 0) {
        try {
            if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) {
                throw "The suite produced no TRX result: $trxPath"
            }
            [xml] $trxDocument = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trxDocument.SelectSingleNode("//*[local-name()='Counters']")
            if ($null -eq $counters -or -not [int]::TryParse($counters.GetAttribute('total'), [ref]$testCount) -or
                $testCount -lt 1) {
                throw 'The suite filter matched no tests.'
            }
        }
        catch {
            $verificationError = $_.Exception.Message
            $exitCode = -2
            $lines = @($lines) + ("RESULT VERIFICATION FAILED: " + $verificationError)
        }
    }
    @($lines) | ForEach-Object { $_.ToString() } | Set-Content -LiteralPath $log -Encoding UTF8
    @($lines) | Select-Object -Last 20 | ForEach-Object { Write-Host $_ }
    $checks.Add([ordered]@{
        name = $Suite.Name
        passed = ($exitCode -eq 0)
        exitCode = $exitCode
        testCount = $testCount
        verificationError = $verificationError
        startedAtUtc = $started.ToString('o')
        completedAtUtc = [DateTime]::UtcNow.ToString('o')
        reasoningPath = $Suite.ReasoningPath
        filter = $Suite.Filter
        log = $log
        trx = Join-Path $EvidenceDirectory $trx
    })
    if ($exitCode -ne 0) { throw "Portable tool acceptance failed: $($Suite.Name) (exit $exitCode). See $log" }
}

try {
    foreach ($entry in $environmentValues.GetEnumerator()) {
        $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if ([Environment]::GetEnvironmentVariable('MISSUM_AI_LIVE_REASONING_EFFORT', 'Process') -ne 'none') {
        throw 'Portable tool acceptance failed to apply MISSUM_AI_LIVE_REASONING_EFFORT=none.'
    }
    Assert-MissumCommand -Name 'dotnet' | Out-Null
    Invoke-MissumDotNet -CommandArguments @(
        'build', $testProject,
        '--configuration', $Configuration,
        '-p:Platform=x64',
        '--nologo'
    )
    New-Item -ItemType Directory -Path $portableDataDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $portableNativeStateDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $portableBundleDirectory -Force | Out-Null
    $portableStartedAt = [DateTime]::UtcNow
    $portableProcess = Start-Process -FilePath $portable -WorkingDirectory ([IO.Path]::GetDirectoryName($portable)) -PassThru
    $portableProcessId = $portableProcess.Id
    $portableDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $portableDeadline -and -not $portableProcess.HasExited) {
        $portableProcess.Refresh()
        if (Test-Path -LiteralPath $portableReadyPath -PathType Leaf) {
            try { $portableNativeState = Get-Content -LiteralPath $portableReadyPath -Raw | ConvertFrom-Json }
            catch { $portableNativeState = $null }
        }
        if ($null -ne $portableNativeState -and $portableProcess.MainWindowHandle -ne 0) { break }
        Start-Sleep -Milliseconds 250
    }
    $portableProcess.Refresh()
    if ($portableProcess.HasExited) {
        throw "Published Missum exited before portable tool acceptance began: $($portableProcess.ExitCode)"
    }
    if ($null -eq $portableNativeState -or $portableNativeState.renderer -ne 'WinUI3' -or
        $portableNativeState.page -ne 'NativeAssistantPage' -or $portableNativeState.ready -ne $true -or
        -not [Guid]::TryParse([string]$portableNativeState.sessionId, [ref]$portableSession) -or
        $portableSession -eq [Guid]::Empty -or
        (Get-Item -LiteralPath $portableReadyPath).LastWriteTimeUtc -lt $portableStartedAt.AddSeconds(-1)) {
        throw 'Published Missum did not freshly initialize a valid native assistant session for portable tool acceptance.'
    }
    if ($portableProcess.MainWindowHandle -eq 0) {
        throw 'Published Missum did not expose a native main window for portable tool acceptance.'
    }
    $portableMainWindowHandle = $portableProcess.MainWindowHandle.ToInt64()
    $portableReady = $true
    [Environment]::SetEnvironmentVariable('MISSUM_AI_PORTABLE_PROCESS_ID', $portableProcess.Id.ToString([Globalization.CultureInfo]::InvariantCulture), 'Process')
    Write-Host ("Portable native window ready: PID {0}; session {1}" -f $portableProcess.Id, $portableSession) -ForegroundColor Cyan
    foreach ($suite in $suites) { Invoke-PortableToolSuite -Suite $suite }
}
catch {
    $executionError = $_.Exception.Message
    throw
}
finally {
    try {
        if ($null -ne $portableProcess -and -not $portableProcess.HasExited) {
            if ($portableProcess.CloseMainWindow() -and $portableProcess.WaitForExit(15000)) {
                $portableStoppedCleanly = $true
            }
            else {
                Stop-Process -Id $portableProcess.Id -Force -ErrorAction SilentlyContinue
                $portableProcess.WaitForExit(5000) | Out-Null
            }
        }
        $portableHash = (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash.ToLowerInvariant()
        [ordered]@{
            schema = 'missum.portable-tool-acceptance.v1'
            checkedAtUtc = [DateTime]::UtcNow.ToString('o')
            passed = ($portableReady -and $checks.Count -eq $suites.Count -and @($checks | Where-Object { -not $_.passed }).Count -eq 0)
            error = $executionError
            portable = [ordered]@{
                path = $portable
                sha256 = $portableHash
                processId = $portableProcessId
                processSelectionVariable = 'MISSUM_AI_PORTABLE_PROCESS_ID'
                mainWindowHandle = $portableMainWindowHandle
                nativeReady = $portableReady
                nativeReadyPath = $portableReadyPath
                sessionId = if ($portableSession -ne [Guid]::Empty) { $portableSession } else { $null }
                stoppedCleanly = $portableStoppedCleanly
            }
            serverUrl = $baseUrl
            models = [ordered]@{
                general = $general.id
                coding = $coding.id
                vision = $vision.id
                reasoningEffort = 'none'
            }
            coverage = $coverage
            nativeUiCoverage = $nativeUiCoverage
            checks = @($checks.ToArray())
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'summary.json') -Encoding UTF8
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

Write-Host ("All portable tool acceptance suites passed. Evidence: {0}" -f $EvidenceDirectory) -ForegroundColor Green

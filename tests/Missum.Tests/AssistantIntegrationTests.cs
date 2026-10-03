using Missum.App.Services;
using Missum.App.Pages;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Memory;
using Missum.Ai.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace Missum.Tests;

public sealed class AssistantIntegrationTests
{
    [Fact]
    public void LiveModelTokensKeepPromptAndGeneratedCountsForTheCurrentModelRun()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();

        var started = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("generationStarted"),
            counter);
        var firstTurn = MissumAiAssistantService.FormatModelTokenProgress(new ModelGenerationEvent(
            "tokenProgress",
            PromptProgress: 0.75,
            PromptTokens: 1_200,
            ProcessedPromptTokens: 900,
            GeneratedTokens: 42,
            TokensPerSecond: 17.25),
            counter);
        var selectedTool = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("toolSelected", "fs.readText"),
            counter);
        var nextTurn = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("generationStarted"),
            counter);
        var secondTurn = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("tokenProgress", GeneratedTokens: 8),
            counter);

        Assert.Equal("0 Token", started);
        Assert.Equal("942 Token", firstTurn);
        Assert.Equal("942 Token", selectedTool);
        Assert.Equal("0 Token", nextTurn);
        Assert.Equal("8 Token", secondTurn);
        Assert.DoesNotContain("Prompt", secondTurn, StringComparison.Ordinal);
        Assert.DoesNotContain("Token/s", secondTurn, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveModelTokensPreferLlamaCurrentTokensForModelsWithoutReasoning()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();

        _ = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("generationStarted"),
            counter);
        var promptEvaluation = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent(
                "tokenProgress",
                ProcessedPromptTokens: 13_331,
                GeneratedTokens: 0,
                CurrentTokens: 13_331),
            counter);

        Assert.Equal($"{13_331:N0} Token", promptEvaluation);
        Assert.NotEqual("0 Token", promptEvaluation);
    }

    [Fact]
    public void LiveModelTokensDeriveLlamaCurrentTokensFromOlderGatewayEvents()
    {
        var counter = new MissumAiAssistantService.ModelTokenProgressState();

        _ = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent("generationStarted"),
            counter);
        var promptEvaluation = MissumAiAssistantService.FormatModelTokenProgress(
            new ModelGenerationEvent(
                "tokenProgress",
                ProcessedPromptTokens: 13_331,
                GeneratedTokens: 0),
            counter);

        Assert.Equal($"{13_331:N0} Token", promptEvaluation);
    }

    [Fact]
    public void PersistentExtensionActionUsesTheGenericBridgeContract()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("action.invoke"));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("session.tool"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"action.invoke\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"session.tool\"", bridge, StringComparison.Ordinal);

        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        Assert.Contains("bridge.js?v=20260927-science-workbench-1", html, StringComparison.Ordinal);

        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("post(\"action.invoke\"", app, StringComparison.Ordinal);
        Assert.Contains("builtin.audiobook/create", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ScientificWorkbenchBridgeContractIsBidirectionalAndHostAuthoritative()
    {
        foreach (var action in new[] { "research.list", "research.open", "research.export" })
            Assert.True(AssistantWebBridge.IsIncomingTypeAllowed(action));
        foreach (var message in new[] { "research.snapshot", "research.exported" })
            Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed(message));

        var json = JsonSerializer.Serialize(AssistantWebBridge.BuildProtocolContract());
        using var contract = JsonDocument.Parse(json);
        Assert.Equal(AssistantWebBridge.ProtocolVersion, contract.RootElement.GetProperty("version").GetInt32());
        Assert.Contains(contract.RootElement.GetProperty("clientActions").EnumerateArray(),
            item => item.GetString() == "research.list");
        Assert.Contains(contract.RootElement.GetProperty("hostEvents").EnumerateArray(),
            item => item.GetString() == "research.snapshot");
    }

    /*
    [Fact]
    public void SessionGroupingButtonTriggersManualAiGroupingRun()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.groupNow"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var coordinator = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Missum.App", "Services", "AssistantCoordinator.cs"));
        Assert.Contains("id=\"group-sessions\"", html, StringComparison.Ordinal);
        Assert.Contains("Sitzungen durch die KI gruppieren lassen", html, StringComparison.Ordinal);
        Assert.Contains("\"session.groupNow\"", bridge, StringComparison.Ordinal);
        Assert.Contains("post(\"session.groupNow\", {});", app, StringComparison.Ordinal);
        Assert.Contains("case \"session.groupNow\":", coordinator, StringComparison.Ordinal);
        Assert.Contains("MaybeGroupSessionsAsync(emit, envelope.RequestId)", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("_ = MaybeGroupSessions", coordinator, StringComparison.Ordinal);
    }
    */

    [Fact]
    public void SessionPinAndHeaderActionsAreAbsentAcrossTheWebViewContract()
    {
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("session.pin"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.DoesNotContain("\"session.pin\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chat.exportPdf\"", bridge, StringComparison.Ordinal);
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        Assert.DoesNotContain("chat-header", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"chat-heading\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("header-actions", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sitzung anpinnen", html, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.DoesNotContain("post(\"session.pin\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("session.isPinned", app, StringComparison.Ordinal);
        Assert.Contains("builtin.documents/export-chat-pdf", app, StringComparison.Ordinal);
        Assert.DoesNotContain("builtin.workflows/open-library", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionProjectCreateButtonTriggersProjectSessionCreation()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.projectCreate"));
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("session.workspaceCreate"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"session.projectCreate\"", bridge, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("session.projectCreate", app, StringComparison.Ordinal);
        Assert.Contains("Neue Sitzung im Projekt", app, StringComparison.Ordinal);
        Assert.Contains("name: \"Allgemeine Sitzungen\"", app, StringComparison.Ordinal);
        Assert.Contains("addable: false", app, StringComparison.Ordinal);
        Assert.DoesNotContain("post(\"session.create\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposerPasteRoutesFilesAndImagesThroughTheNativeAttachmentImport()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("document.paste"));
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("document.upload"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var page = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Missum.App", "Pages", "AssistantPage.xaml.cs"));

        Assert.Contains("\"document.paste\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"document.upload\"", bridge, StringComparison.Ordinal);
        Assert.Contains("uploadFiles(files, state.activeSessionId)", app, StringComparison.Ordinal);
        Assert.Contains("elements.prompt.addEventListener(\"paste\"", app, StringComparison.Ordinal);
        Assert.Contains("clipboard.files.length > 0", app, StringComparison.Ordinal);
        Assert.Contains("item.kind === \"file\"", app, StringComparison.Ordinal);
        Assert.Contains("post(\"document.paste\", { sessionId: state.activeSessionId })", app, StringComparison.Ordinal);
        Assert.Contains("case \"document.paste\":", page, StringComparison.Ordinal);
        Assert.Contains("case \"document.upload\":", page, StringComparison.Ordinal);
        Assert.Contains("StandardDataFormats.StorageItems", page, StringComparison.Ordinal);
        Assert.Contains("StandardDataFormats.Bitmap", page, StringComparison.Ordinal);
        Assert.Contains("ImportStorageFilesAsync(sessionId, files", page, StringComparison.Ordinal);
        Assert.Contains("_coordinator.ImportAttachmentAsync(", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatAndMessagePdfExportsUseTheSharedDinA4BookLayout()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.InRange(AssistantPage.PdfA4WidthInches, 8.267, 8.268);
        Assert.InRange(AssistantPage.PdfA4HeightInches, 11.692, 11.693);
        Assert.InRange(AssistantPage.PdfBookMarginLeftInches, .944, .946);
        Assert.InRange(AssistantPage.PdfBookMarginBottomInches, .944, .946);
        Assert.Matches("""<link\b[^>]*\brel="stylesheet"[^>]*\bhref="styles\.css(?:\?[^\"]*)?"[^>]*>""", html);
        Assert.Matches("""<script\b[^>]*\bsrc="markdown\.js(?:\?[^\"]*)?"[^>]*>""", html);
        Assert.Matches("""<script\b[^>]*\bsrc="voice\.js(?:\?[^\"]*)?"[^>]*>""", html);
        Assert.Matches("""<script\b[^>]*\bsrc="app\.js(?:\?[^\"]*)?"[^>]*>""", html);
        Assert.Contains("globalThis.missumPrepareBookPdf = messageId =>", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.missumPdfBookReady = () =>", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.missumPrepareMessagePdf = globalThis.missumPrepareBookPdf", app, StringComparison.Ordinal);
        Assert.Contains("pdf-book--message", app, StringComparison.Ordinal);
        Assert.Contains("pdf-book--chat", app, StringComparison.Ordinal);
        Assert.Contains("size: A4 portrait", styles, StringComparison.Ordinal);
        Assert.Contains("background: #fff", styles, StringComparison.Ordinal);
        Assert.Contains("color-scheme: only light !important", styles, StringComparison.Ordinal);
        Assert.Contains("font: 10.75pt/1.58 Georgia", styles, StringComparison.Ordinal);
        Assert.Contains("hyphens: auto", styles, StringComparison.Ordinal);
        Assert.Contains("orphans: 3", styles, StringComparison.Ordinal);
        Assert.Contains("widows: 3", styles, StringComparison.Ordinal);
        Assert.Contains(".pdf-book thead { display: table-header-group; }", styles, StringComparison.Ordinal);
        Assert.Contains("white-space: pre-wrap !important", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("pdf-exporting-message", styles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("microphone.start")]
    [InlineData("microphone.audio")]
    [InlineData("microphone.speak")]
    [InlineData("microphone.stopSpeech")]
    [InlineData("microphone.toggleSpeechPause")]
    [InlineData("microphone.stop")]
    [InlineData("microphone.cancel")]
    public void MicrophoneMessagesAreAcceptedByTheWebBridge(string messageType)
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed(messageType));
    }

    [Fact]
    public void WebAssetsExposeTheSameMicrophoneContractAndPlaceItAboveSend()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        Assert.Contains("\"microphone.start\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.audio\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.speak\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.stopSpeech\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.toggleSpeechPause\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.previousSpeechParagraph", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.skipSpeechParagraph", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.stop\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.cancel\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.changed\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"microphone.transcript\"", bridge, StringComparison.Ordinal);
        Assert.Contains("\"attachment.remove\"", bridge, StringComparison.Ordinal);

        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var submit = html.IndexOf("class=\"toolbar-group composer-submit\"", StringComparison.Ordinal);
        var microphone = html.IndexOf("id=\"microphone\"", submit, StringComparison.Ordinal);
        var send = html.IndexOf("id=\"send\"", submit, StringComparison.Ordinal);
        Assert.True(submit >= 0 && microphone > submit && send > microphone);

        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));
        Assert.Contains(".composer-submit {", styles, StringComparison.Ordinal);
        Assert.Contains(".send-button, .microphone-button", styles, StringComparison.Ordinal);

        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        Assert.Contains("navigator.mediaDevices.getUserMedia", voice, StringComparison.Ordinal);
        Assert.Contains("microphone.audio", voice, StringComparison.Ordinal);
        Assert.Contains("missum:voice-level", voice, StringComparison.Ordinal);
        Assert.Contains("createAnalyser", voice, StringComparison.Ordinal);
        Assert.Contains("createMediaStreamDestination", voice, StringComparison.Ordinal);
        Assert.Contains("getUserMedia({ audio: true, video: false })", voice, StringComparison.Ordinal);
        Assert.Contains("beginTurn(values)", voice, StringComparison.Ordinal);
        Assert.Contains("const bridgeFrameSamples = 1600;", voice, StringComparison.Ordinal);
        Assert.Contains("if (turn.speechSamples >= minimumTurnSamples) emitAvailableFrames(turn);", voice, StringComparison.Ordinal);
        Assert.Contains("const preRollSamples = 4000;", voice, StringComparison.Ordinal);
        Assert.Contains("const silenceToFinishSamples = 8000;", voice, StringComparison.Ordinal);
        Assert.Contains("sendFrame(turn, turn.frameBuffer.splice(0), true);", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("windowSamples", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("for (const value of values) samples.push(value);", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaRecorder", voice, StringComparison.Ordinal);

        Assert.Contains("microphone-frequency", html, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-feedback", html, StringComparison.Ordinal);
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("voice-context-chip", app, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-listening-preview", app, StringComparison.Ordinal);
        Assert.Contains("Ich höre zu", app, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"live-caption\"", html, StringComparison.Ordinal);
        Assert.Contains("isLiveCaption: true", app, StringComparison.Ordinal);
        Assert.Contains("state.liveCaption?.isActive", app, StringComparison.Ordinal);
        Assert.Contains("post(\"liveCaption.stop\", {})", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Live-Untertitel beenden", app, StringComparison.Ordinal);
        Assert.DoesNotContain("start-live-translation", html, StringComparison.Ordinal);
        Assert.Contains("artifact.provider === \"screen-capture\"", app, StringComparison.Ordinal);
        Assert.Contains("message-artifacts--captures", app, StringComparison.Ordinal);
        Assert.DoesNotContain("renderVoiceFeedback()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"capture-screen\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"capture-clip\"", html, StringComparison.Ordinal);
        Assert.Contains("builtin.web/web-search", app, StringComparison.Ordinal);
        Assert.Contains("builtin.image/generate", app, StringComparison.Ordinal);
        Assert.Contains("renderActionMenu()", app, StringComparison.Ordinal);
        Assert.Contains("Vorlesen", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.speak\", {", app, StringComparison.Ordinal);
        Assert.Contains("messageId: String(message.id)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("speechMessageId: String(message.id)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenClipEventsRenderOnlyInTheComposerContext()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.DoesNotContain("createScreenClipProgressMessage", app, StringComparison.Ordinal);
        Assert.DoesNotContain("data-screen-clip-progress", app, StringComparison.Ordinal);
        Assert.Contains("Video aufnehmen · ${formatClipTime(elapsed)}", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.stop\"", app, StringComparison.Ordinal);
        Assert.Contains("post(\"screenClip.cancel\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".screen-clip-progress", styles, StringComparison.Ordinal);
        Assert.Contains(".screen-clip-chip", styles, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("webSearch", PromptTriggerAction.WebSearch)]
    [InlineData("imageGeneration", PromptTriggerAction.ImageGeneration)]
    [InlineData("audiobook", PromptTriggerAction.Audiobook)]
    [InlineData("textToSpeech", PromptTriggerAction.TextToSpeech)]
    [InlineData("documentCreate", PromptTriggerAction.DocumentCreate)]
    public void ExplicitComposerToolCreatesOneShotTrigger(string tool, PromptTriggerAction expected)
    {
        var match = AssistantCoordinator.CreateToolMatch(tool, "Aufgabe ohne Präfix");
        Assert.Equal(expected, match.Trigger.Action);
        Assert.Equal("Aufgabe ohne Präfix", match.RemainingPrompt);
    }

    [Fact]
    public async Task ReadAloudPromptIsClassifiedWithoutBecomingAChatRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Parallel vorlesen");

        using var speechPayload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sessionId = session.Id,
            prompt = "Vorlesen",
            toolAction = "textToSpeech",
        }));
        using var chatPayload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            sessionId = session.Id,
            prompt = "Analysiere den nächsten Fall.",
        }));

        Assert.True(await coordinator.IsSpeechRequestAsync(speechPayload.RootElement));
        Assert.False(await coordinator.IsSpeechRequestAsync(chatPayload.RootElement));
    }

    [Fact]
    public void PersistedProcessReportCanAlwaysBeReadAloud()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ChatRole.Assistant,
            "### Prozessbericht\n\nDie Schwarzschild-Metrik wird symbolisch geprüft.",
            MessageStatus.Completed,
            now,
            now);

        Assert.True(MissumAiAssistantService.IsReadableSpeechMessage(message));
        Assert.NotEmpty(SpeechSourceSegmentation.CreateUnits(message.Content));
    }

    [Fact]
    public void BrowserMicrophonePcmIsWrappedAsA16KhzMonoWave()
    {
        var pcm = new byte[16_000 * 2];

        var wave = MicrophoneTranscriptionService.CreatePcm16Wave(pcm);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(1, BitConverter.ToInt16(wave, 22));
        Assert.Equal(16_000, BitConverter.ToInt32(wave, 24));
        Assert.Equal(16, BitConverter.ToInt16(wave, 34));
        Assert.Equal(pcm.Length + 44, wave.Length);
    }

    [Fact]
    public void DictationCaptureSchedulesAtFiveHundredMillisecondsAndKeepsSixSeconds()
    {
        var capture = new MicrophoneTranscriptionService.DictationCaptureState("turn-1", "session-1");
        var frame = new byte[16_000 * sizeof(short) / 10];

        for (var index = 0; index < 4; index++)
        {
            capture.Append(frame);
        }
        Assert.False(capture.ShouldSchedule(isFinal: false));

        capture.Append(frame);
        Assert.True(capture.ShouldSchedule(isFinal: false));
        var first = capture.CreateWindow(isFinal: false);
        capture.MarkScheduled();
        Assert.Equal(0, first.Revision);
        Assert.Equal(16_000, first.Pcm.Length);

        capture.Append(frame);
        capture.Append(frame);
        Assert.False(capture.ShouldSchedule(isFinal: false));
        capture.Append(frame);
        Assert.True(capture.ShouldSchedule(isFinal: false));
        _ = capture.CreateWindow(isFinal: false);
        capture.MarkScheduled();

        for (var index = 0; index < 60; index++)
        {
            capture.Append(frame);
        }
        var rolling = capture.CreateWindow(isFinal: true);
        Assert.Equal(6_000 * 32, rolling.Pcm.Length);
        Assert.Equal(800, rolling.WindowStartMilliseconds);
        Assert.True(capture.ShouldSchedule(isFinal: true));
    }

    [Fact]
    public void SystemAudioAnalysisCaptureProducesAValidTenMinuteBoundedWave()
    {
        var pcm = new byte[SystemAudioAnalysisCaptureService.SampleRate * sizeof(short)];

        var wave = SystemAudioAnalysisCaptureService.CreateWave(pcm);

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        Assert.Equal(SystemAudioAnalysisCaptureService.SampleRate, BitConverter.ToInt32(wave, 24));
        Assert.Equal(1, BitConverter.ToInt16(wave, 22));
        Assert.Equal(16, BitConverter.ToInt16(wave, 34));
        Assert.Equal(pcm.Length + 44, wave.Length);
    }

    [Fact]
    public void MediaAnalysisPrefersDocumentsAndOtherwiseRequiresMatchingMedia()
    {
        var now = DateTimeOffset.UtcNow;
        var image = new AssistantAttachment(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "foto.png", "image/png", "blob", 10, now);

        Assert.True(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.AudioAnalysis,
            hasDocuments: true,
            []));
        Assert.True(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.ImageAnalysis,
            hasDocuments: false,
            [image]));
        Assert.False(AssistantCoordinator.HasMediaAnalysisContext(
            PromptTriggerAction.VideoAnalysis,
            hasDocuments: false,
            [image]));
    }

    [Fact]
    public void AnalysisToolsOwnCaptureAndSidebarDeleteRemainsInteractiveDuringRuns()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));
        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));

        Assert.Contains("builtin.media/audio-analysis", app, StringComparison.Ordinal);
        Assert.Contains("builtin.media/video-analysis", app, StringComparison.Ordinal);
        Assert.Contains("builtin.media/image-analysis", app, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tool-immediate=\"screen.capture\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-tool-immediate=\"screenClip.toggle\"", html, StringComparison.Ordinal);
        Assert.Contains("beginMediaCapture(action)", app, StringComparison.Ordinal);
        Assert.Contains("if (state.documents.length > 0) return true", app, StringComparison.Ordinal);
        Assert.DoesNotContain("remove.disabled = state.isRunning", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".session-delete:disabled", styles, StringComparison.Ordinal);
        Assert.Contains(".session-item:hover .session-delete", styles, StringComparison.Ordinal);
        Assert.Contains("post(\"audioCapture.start\", { sessionId: state.activeSessionId })", app, StringComparison.Ordinal);
        Assert.Contains("Systemaudio aufnehmen", app, StringComparison.Ordinal);
        Assert.DoesNotContain("missumAnalysisAudioCapture", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("audioCapture.audio", bridge, StringComparison.Ordinal);
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.start"));
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.stop"));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("audioCapture.audio"));
    }

    [Fact]
    public void ToolsMenuLeavesReasoningAtTheNativeModelDefault()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.DoesNotContain("id=\"reasoning-model\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"reasoning-options\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"reasoning\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-reasoning=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoningProfiles", app, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoningEffort:", app, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.reasoning", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsUseTheGlobalPromptModelSelection()
    {
        var settingsPage = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Missum.App",
            "Pages",
            "SettingsPage.xaml"));
        var settingsViewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Missum.App",
            "ViewModels",
            "SettingsViewModel.cs"));

        Assert.DoesNotContain("Header=\"General AI Modell\"", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Coding AI Modell\"", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedGeneralModelItem", settingsPage, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectedCodingModelItem", settingsPage, StringComparison.Ordinal);
        Assert.Contains("Promptfenster", settingsPage, StringComparison.Ordinal);
        Assert.Contains("SelectedModel = current.SelectedModel", settingsViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneralModelSelectionUsesThePersistedValue()
    {
        var settings = new AppSettings
        {
            SelectedModel = "general-model",
        };

        Assert.Equal("general-model", MissumAiAssistantService.ResolvePreferredModel(settings));
    }

    [Fact]
    public void ModelCatalogRefreshKeepsCurrentOrPersistedSelectionInsteadOfFallingBack()
    {
        Assert.Equal("changed-during-refresh", SettingsViewModel.PreferCurrentSelection(
            "changed-during-refresh",
            "persisted-model",
            "fallback-model"));
        Assert.Equal("persisted-model", SettingsViewModel.PreferCurrentSelection(
            null,
            "persisted-model",
            "fallback-model"));
        Assert.Equal("fallback-model", SettingsViewModel.PreferCurrentSelection(
            null,
            null,
            "fallback-model"));
    }

    [Fact]
    public void ToolsReasoningSelectionAndBridgeHandlerAreRemoved()
    {
        var root = FindRepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Missum.App", "Assets", "Web", "app.js"));
        var coordinator = File.ReadAllText(Path.Combine(root, "src", "Missum.App", "Services", "AssistantCoordinator.cs"));

        Assert.DoesNotContain("settings.reasoning", app, StringComparison.Ordinal);
        Assert.DoesNotContain("settings.reasoning", coordinator, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning.changed", app, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Beenden")]
    [InlineData("Aufnahme beenden.")]
    [InlineData("Abschließen!")]
    [InlineData("Aufnahme abschließen")]
    public void VoiceCanFinishAnActiveMediaCaptureWithoutAnIntentModelRun(string command)
    {
        Assert.True(AssistantPage.IsMediaCaptureFinishCommand(command));
    }

    [Theory]
    [InlineData("Senden")]
    [InlineData("Prompt senden.")]
    [InlineData("  PROMPT   SENDEN!  ")]
    public void ExplicitVoiceCommandSendsTheCurrentComposerDraft(string command)
    {
        Assert.True(AssistantPage.IsVoicePromptSendCommand(command));
    }

    [Theory]
    [InlineData("Bitte diesen Text senden")]
    [InlineData("Hörbuch erstellen")]
    [InlineData("Suche im Web")]
    public void OrdinaryDictationNeverSendsItself(string dictation)
    {
        Assert.False(AssistantPage.IsVoicePromptSendCommand(dictation));
    }

    [Fact]
    public void VoiceRecognitionUsesEditableComposerDictationWithoutWhisperHotwordsOrIntentDispatch()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var voice = File.ReadAllText(Path.Combine(webRoot, "voice.js"));
        var repositoryRoot = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(repositoryRoot, "workers", "speech", "app.py"));
        var page = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Missum.App", "Pages", "AssistantPage.xaml.cs"));
        var microphone = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Missum.App",
            "Services",
            "MicrophoneTranscriptionService.cs"));

        Assert.Contains("function updateVoiceDictation(", app, StringComparison.Ordinal);
        Assert.Contains("setPromptValue(turn.renderedValue)", app, StringComparison.Ordinal);
        Assert.Contains("nextRevision <= turn.lastRevision", app, StringComparison.Ordinal);
        Assert.Contains("turn.manuallyConfirmed = true", app, StringComparison.Ordinal);
        Assert.Contains("payload?.isFinal && payload?.sendPrompt", app, StringComparison.Ordinal);
        Assert.Contains("void submitPrompt();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("submitVoicePrompt", app, StringComparison.Ordinal);
        Assert.Contains("const bridgeFrameSamples = 1600;", voice, StringComparison.Ordinal);
        Assert.Contains("const silenceToFinishSamples = 8000;", voice, StringComparison.Ordinal);
        Assert.DoesNotContain("windowSamples", voice, StringComparison.Ordinal);
        Assert.Contains("FirstDecodeMilliseconds = 480", microphone, StringComparison.Ordinal);
        Assert.Contains("DecodeCadenceMilliseconds = 300", microphone, StringComparison.Ordinal);
        Assert.Contains("WindowMilliseconds = 6_000", microphone, StringComparison.Ordinal);
        Assert.Contains("LiveCaptionProfile.Dictation", microphone, StringComparison.Ordinal);
        Assert.Contains("_pendingDictationPartial = pending", microphone, StringComparison.Ordinal);
        Assert.Contains("_pendingDictationFinals.Enqueue(pending)", microphone, StringComparison.Ordinal);
        Assert.DoesNotContain("WHISPER_HOTWORDS", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("hotwords=", worker, StringComparison.Ordinal);
        Assert.Contains("condition_on_previous_text=not dictation", worker, StringComparison.Ordinal);
        Assert.Contains("word_timestamps=dictation", worker, StringComparison.Ordinal);
        Assert.Contains("if not dictation and temporary is not None", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("ClassifyIntentAsync", page, StringComparison.Ordinal);
        Assert.DoesNotContain("UtteranceIntent.", page, StringComparison.Ordinal);
    }

    [Fact]
    public void VoiceDictationRemainsVisibleWhileSpeechPlaybackIsActive()
    {
        var repositoryRoot = FindRepositoryRoot();
        var page = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Missum.App",
            "Pages",
            "AssistantPage.xaml.cs"));
        var app = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Web",
            "app.js"));

        Assert.Contains(
            "Whisper dictation and speech playback are independent",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (!_microphone.Current.IsSpeaking)",
            page,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (microphoneState.IsSpeaking)\r\n            {\r\n                await bridge.PostAsync(\"microphone.transcript\"",
            page,
            StringComparison.Ordinal);
        Assert.Contains(
            "Speech playback and dictation have independent lifetimes",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "setSuspended(!state.microphone?.isRecording)",
            app,
            StringComparison.Ordinal);
        var microphone = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Missum.App",
            "Services",
            "MicrophoneTranscriptionService.cs"));
        Assert.Contains("private string? _transcriptionProvider;", microphone, StringComparison.Ordinal);
        Assert.Contains("private string? _speechProvider;", microphone, StringComparison.Ordinal);
        Assert.Contains("_speaking ? _speechProvider : _transcriptionProvider", microphone, StringComparison.Ordinal);
        Assert.Contains("Sprache wird erkannt · Vorlesen läuft", microphone, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pausieren", false, "Pause")]
    [InlineData("Vorlesen pausieren.", false, "Pause")]
    [InlineData("Fortsetzen", true, "Resume")]
    [InlineData("Weiterlesen!", true, "Resume")]
    [InlineData("Abbrechen", false, "Cancel")]
    public void VoicePlaybackCommandsAreHandledLocallyWhileSpeechIsActive(
        string command,
        bool paused,
        string expected)
    {
        var state = new MicrophoneSnapshot(
            IsRecording: true,
            IsBusy: true,
            IsSpeaking: true,
            CanPauseSpeech: true,
            IsSpeechPaused: paused,
            Status: "AI-Antwort wird vorgelesen",
            StartedAt: DateTimeOffset.UtcNow,
            Error: null,
            PartialTranscript: string.Empty,
            Provider: "supertonic-3-F5-cuda",
            DeviceLabel: "Test");

        Assert.Equal(
            expected,
            AssistantPage.ResolveVoicePlaybackControl(command, state, speechOperationActive: true).ToString());
    }

    [Theory]
    [InlineData("Sprachsteuerung abbrechen")]
    [InlineData("Sprachsteuerung beenden.")]
    [InlineData("  SPRACHSTEUERUNG   BEENDEN!  ")]
    public void ExplicitVoiceCommandsStopPersistentVoiceControl(string command)
    {
        Assert.True(AssistantPage.IsVoiceControlStopCommand(command));
    }

    [Theory]
    [InlineData("Abbrechen")]
    [InlineData("Vorlesen abbrechen")]
    [InlineData("Sprachsteuerung starten")]
    [InlineData("Beenden")]
    public void GenericCommandsDoNotDisablePersistentVoiceControl(string command)
    {
        Assert.False(AssistantPage.IsVoiceControlStopCommand(command));
    }

    [Fact]
    public void SpeechStatusHidesHardwareAndSelectedMessageDetails()
    {
        var app = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "app.js"));
        var styles = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Web", "styles.css"));

        Assert.Equal("Supertonic F5 Ultra", MissumAiAssistantService.DisplaySpeechProvider(null));
        Assert.Equal("Supertonic F5 Ultra", MissumAiAssistantService.DisplaySpeechProvider("supertonic-3-F5-cuda"));
        Assert.Contains("setSuspended(!state.microphone?.isRecording)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Boolean(state.microphone?.isSpeaking)\n      || state.voicePlaybackPending", app, StringComparison.Ordinal);
        Assert.Contains("elements.microphone.disabled = Boolean(state.voiceStarting)", app, StringComparison.Ordinal);
        Assert.Contains("payload?.isFinal && payload?.stopVoice", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.isBusy && !active", app, StringComparison.Ordinal);
        Assert.Contains("const active = Boolean(browserActive || state.voiceStarting)", app, StringComparison.Ordinal);
        Assert.Contains("function resetTransientVoiceStateForSessionChange()", app, StringComparison.Ordinal);
        Assert.Contains("resetTransientVoiceStateForSessionChange();", app, StringComparison.Ordinal);
        Assert.Contains("isBusy: browserCaptureActive && Boolean(state.microphone?.isBusy)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("browserVoiceActive || state.microphone?.isBusy", app, StringComparison.Ordinal);
        Assert.Contains("elements.microphone.classList.remove(\"speaking\")", app, StringComparison.Ordinal);
        Assert.DoesNotContain("classList.toggle(\"speaking\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".microphone-button.speaking", styles, StringComparison.Ordinal);
        Assert.Contains("&& isVoiceControlActive()", app, StringComparison.Ordinal);
        Assert.Contains("const captureStop = globalThis.missumVoiceCapture?.stop(false)", app, StringComparison.Ordinal);
        Assert.Contains("if (notifyHost) post(\"microphone.stop\", {})", app, StringComparison.Ordinal);
        Assert.True(
            app.IndexOf("if (notifyHost) post(\"microphone.stop\", {})", StringComparison.Ordinal)
            < app.IndexOf("await captureStop", StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticVoiceOutputRemovesMarkdownNoise()
    {
        var value = MicrophoneTranscriptionService.PrepareSpeechText(
            "## Ergebnis\n\n**Volumenstrom:** [siehe Quelle](https://example.test) | 450 m³/h");

        Assert.Equal("Ergebnis Volumenstrom: siehe Quelle , vierhundertfünfzig Kubikmeter pro Stunde", value);
    }

    [Theory]
    [InlineData(
        @"Die Druckdifferenz ist \(\Delta p = \frac{\rho}{2} \cdot v^2\).",
        "Delta p gleich Rho geteilt durch zwei mal v hoch zwei")]
    [InlineData(
        @"Volumenstrom: $$\dot{V} = A \cdot v$$",
        "V Punkt gleich A mal v")]
    [InlineData(
        @"Die Kantenlänge lautet \(\sqrt[3]{x_1}\).",
        "dritte Wurzel aus x Index eins")]
    [InlineData(
        @"Einheit: \frac{\mathrm{m}^3}{\mathrm{h}}",
        "Kubikmeter pro Stunde")]
    [InlineData(
        "Für den Druck gilt Δp = ρ · v².",
        "Delta p gleich Rho mal v hoch zwei")]
    public void AutomaticVoiceOutputSpeaksLatexAsGermanMathematics(
        string markdown,
        string expectedPhrase)
    {
        var value = MicrophoneTranscriptionService.PrepareSpeechText(markdown);

        Assert.Contains(expectedPhrase, value, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', value);
        Assert.DoesNotContain('$', value);
        Assert.DoesNotContain('{', value);
        Assert.DoesNotContain('}', value);
    }

    [Theory]
    [InlineData("Der Anteil beträgt 2 %.", "Der Anteil beträgt zwei Prozent.")]
    [InlineData("Der Wert ist 3,14.", "Der Wert ist drei Komma eins vier.")]
    [InlineData("Beginn: 09:23 Uhr.", "Beginn: neun Uhr dreiundzwanzig.")]
    [InlineData("Stand 19.08.2026.", "Stand neunzehnter August zweitausendsechsundzwanzig.")]
    [InlineData("Nach DIN 1946-6.", "Nach DIN eintausendneunhundertsechsundvierzig Strich sechs.")]
    [InlineData("Leistung 12 kW bei -5 °C.", "Leistung zwölf Kilowatt bei minus fünf Grad Celsius.")]
    [InlineData("Bereich 10-12 m.", "Bereich zehn bis zwölf Meter.")]
    public void DeterministicSpeechPlanSpeaksGermanValues(
        string source,
        string expected)
    {
        Assert.Equal(expected, MicrophoneTranscriptionService.PrepareSpeechText(source));
    }

    [Fact]
    public void HiddenSpeechTextRemovesQuotationFormsAndPreservesWordApostrophes()
    {
        var value = SpeechSourceSegmentation.NormalizeSpeechPunctuation(
            "„Hallo“, «Welt» und O’Connor's Anlage.");

        Assert.Equal("Hallo, Welt und O'Connor's Anlage.", value);
        Assert.False(SpeechSourceSegmentation.ContainsForbiddenSpeechQuotation(value));
    }

    [Theory]
    [InlineData(@"\(f(x)=x\)")]
    [InlineData(@"\(x^2 + \sqrt{x}=4\)")]
    [InlineData(@"$$\int_0^1 \frac{x^2 + 1}{\sqrt{x}}\,dx = 2$$")]
    public void MathematicsUsesTheSameDeterministicPipelineAsProse(string source)
    {
        var units = SpeechSourceSegmentation.CreateUnits(source);

        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.NotEmpty(segments);
        Assert.All(segments, static segment =>
        {
            Assert.Single(segment.SourceUnitIds);
            Assert.DoesNotContain("MISSUMMATH", segment.Text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void DeterministicSpeechPlanNormalizesInlineMathematicsAndRemovesQuotes()
    {
        var units = SpeechSourceSegmentation.CreateUnits(
            @"„Die Gleichung lautet \(\Delta p = \frac{\rho}{2}\)“, erklärte Lea.");
        var prepared = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.NotEmpty(prepared);
        Assert.Contains("Delta p gleich Rho geteilt durch zwei", string.Join(' ', prepared.Select(static segment => segment.Text)), StringComparison.Ordinal);
        Assert.All(prepared, static segment =>
        {
            Assert.False(SpeechSourceSegmentation.ContainsForbiddenSpeechQuotation(
                SpeechSourceSegmentation.PrepareForSynthesis(segment)));
        });
    }


    [Theory]
    [InlineData("Vorlesen Seite 3", 3, 3, "Seite 3")]
    [InlineData("Vorlesen Seiten 3-5", 3, 5, "Seite 3 bis 5")]
    [InlineData("Vorlesen Seiten 2 bis 4", 2, 4, "Seite 2 bis 4")]
    [InlineData("Vorlesen ab Seite 3 bis Seite 5", 3, 5, "Seite 3 bis 5")]
    [InlineData("Vorlesen ab Seite 7", 7, null, "ab Seite 7")]
    public void DocumentSpeechRecognizesGermanPageSelections(string prompt, int start, int? end, string description)
    {
        var selection = MissumAiAssistantService.ParseSpeechPageSelection(prompt);

        Assert.True(selection.HasValue);
        Assert.Equal(start, selection.Value.Start);
        Assert.Equal(end, selection.Value.End);
        Assert.Equal(description, selection.Value.Description);
    }

    [Theory]
    [InlineData("Vorlesen")]
    [InlineData("Lies vor")]
    [InlineData("Lies die letzte Nachricht vor")]
    public void PlainSpeechCommandsDoNotAccidentallySelectDocumentPages(string prompt)
    {
        Assert.Null(MissumAiAssistantService.ParseSpeechPageSelection(prompt));
    }


    [Fact]
    public void SpeechSourceUnitsPreserveMarkdownStructureAndStableOrder()
    {
        const string markdown = """
            # Heading

            First sentence. Second sentence!

            - List item.

            | Name | Value |
            | --- | --- |
            | Air | 42 |

            > Quoted sentence.

            $$x^2$$

            ```csharp
            Console.WriteLine(42);
            ```
            """;

        var units = SpeechSourceSegmentation.CreateUnits(markdown);

        Assert.NotEmpty(units);
        Assert.Equal(
            Enumerable.Range(1, units.Count).Select(index => $"u{index:0000}"),
            units.Select(static unit => unit.Id));
        Assert.Contains(units, static unit => unit.Kind == "heading");
        Assert.Equal(2, units.Count(static unit => unit.Kind == "paragraph"));
        Assert.Contains(units, static unit => unit.Kind == "listItem");
        Assert.Equal(2, units.Count(static unit => unit.Kind == "tableRow"));
        Assert.Contains(units, static unit => unit.Kind == "quote");
        Assert.Contains(units, static unit => unit.Kind == "math");
        Assert.Contains(units, static unit => unit.Kind == "code");
    }

    [Fact]
    public void DirectSpeechSegmentsKeepSourceMappingAndStayBelowPlaybackLimit()
    {
        var source = string.Join(' ', Enumerable.Repeat("A deliberately long clause,", 160)) + " complete.";
        var units = SpeechSourceSegmentation.CreateUnits(source);

        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        Assert.True(segments.Count > 1);
        Assert.All(segments, segment =>
        {
            Assert.InRange(segment.Text.Length, 1, SpeechSourceSegmentation.MaximumSegmentCharacters);
            Assert.Single(segment.SourceUnitIds);
            Assert.Contains(segment.SourceUnitIds[0], units.Select(static unit => unit.Id));
        });
    }

    [Fact]
    public void LongSentenceBelowThreeThousandCharactersRemainsOneSpeechRequest()
    {
        var source = string.Join(
            ' ',
            Enumerable.Repeat("Dieser ausführliche Satzteil bleibt für eine flüssige Aussprache zusammen,", 35))
            + " und endet erst hier.";

        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segment = Assert.Single(SpeechSourceSegmentation.CreateDirectSegments(units));
        var batch = Assert.Single(SpeechSourceSegmentation.CreatePlaybackBatches([segment]));

        Assert.InRange(segment.Text.Length, 301, SpeechSourceSegmentation.MaximumSegmentCharacters);
        Assert.Equal([0], batch.SegmentIndexes);
    }

    [Fact]
    public void PlaybackBatchesContainExactlyOneSentenceAndPreserveSourceOrder()
    {
        const string source = "Narration before dialogue. \"We continue together.\" Narration after dialogue.\n\nSecond paragraph starts here. \"Ready.\"";
        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);

        var batches = SpeechSourceSegmentation.CreatePlaybackBatches(segments);

        Assert.Equal(segments.Count, batches.Count);
        Assert.All(batches, static batch => Assert.Single(batch.SegmentIndexes));
        Assert.Equal(Enumerable.Range(0, segments.Count), batches.SelectMany(static batch => batch.SegmentIndexes));
        Assert.Contains(segments, static segment => segment.Text.Contains("We continue together", StringComparison.Ordinal));
        Assert.Equal(batches.Count, batches.Select(static batch => batch.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlaybackNormalizationPreservesBatchAcrossLongSegmentSplits()
    {
        var longText = string.Join(' ', Enumerable.Repeat("A long sentence fragment,", 160)) + ".";
        var normalized = SpeechSourceSegmentation.NormalizePreparedSegments([
            new PreparedSpeechSegment(
                "source",
                longText,
                ["u0001"],
                PlaybackBatchId: "paragraph-1"),
        ]);

        Assert.True(normalized.Count > 1);
        Assert.All(normalized, static segment => Assert.Equal("paragraph-1", segment.PlaybackBatchId));
        var batches = SpeechSourceSegmentation.CreatePlaybackBatches(normalized);
        Assert.Equal(normalized.Count, batches.Count);
        Assert.All(batches, static batch => Assert.Single(batch.SegmentIndexes));
        Assert.Equal(Enumerable.Range(0, normalized.Count), batches.SelectMany(static batch => batch.SegmentIndexes));
        Assert.All(batches.Take(batches.Count - 1), static batch => Assert.Equal(40, batch.PauseAfterMilliseconds));
        Assert.Equal(180, batches[^1].PauseAfterMilliseconds);
    }

    [Theory]
    [InlineData("Natascha ließ das Steuer sanft nach vorne gleiten und spürte, wie")]
    [InlineData("Natascha ließ das Steuer sanft nach vorne gleiten und spürte, wie, “")]
    [InlineData("„Alles bleibt ruhig“, sagte Natascha, „während wir weiterfliegen.“")]
    public void PlaybackNormalizationNeverCreatesEmptySegmentsAtCommasOrQuotes(string source)
    {
        var units = SpeechSourceSegmentation.CreateUnits(source);
        var direct = SpeechSourceSegmentation.CreateDirectSegments(units, source);
        var playable = SpeechSourceSegmentation.NormalizePreparedSegments(direct);

        Assert.NotEmpty(playable);
        Assert.All(playable, segment =>
            Assert.False(string.IsNullOrWhiteSpace(
                SpeechSourceSegmentation.PrepareForSynthesis(segment))));
        Assert.Contains(
            playable,
            segment => segment.Text.Contains("Natascha", StringComparison.Ordinal));
    }

    [Fact]
    public void PlaybackNormalizationDropsPunctuationOnlyTechnicalFragments()
    {
        var playable = SpeechSourceSegmentation.NormalizePreparedSegments([
            new PreparedSpeechSegment("text", "Ein hörbarer Satz,", ["u0001"]),
            new PreparedSpeechSegment("punctuation", "“,", ["u0001"]),
        ]);

        var segment = Assert.Single(playable);
        Assert.Equal("Ein hörbarer Satz", segment.Text);
        Assert.Equal("Ein hörbarer Satz", SpeechSourceSegmentation.PrepareForSynthesis(segment));
    }

    [Fact]
    public void QuotedSentenceEndingMapsToOneVisibleSentenceAtATime()
    {
        const string source = "„Wir stabilisieren das System.“ Meine Stimme blieb ruhig. „Dann fliegen wir weiter.“";

        var units = SpeechSourceSegmentation.CreateUnits(source);
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units, source);

        Assert.Equal(3, units.Count);
        Assert.Equal(3, segments.Count);
        Assert.Equal([0, 1, 2], units.Select(static unit => unit.OrdinalInBlock));
        Assert.All(segments, static segment => Assert.Single(segment.SourceUnitIds));
        Assert.Equal(
            units.Select(static unit => unit.Id),
            segments.Select(static segment => segment.SourceUnitIds.Single()));
    }

    [Fact]
    public void SpeechProgressBridgeCarriesVisibleSourceMappingWithoutSpokenRewrite()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var unit = Assert.Single(SpeechSourceSegmentation.CreateUnits("Visible original sentence."));
        var playbackId = Guid.NewGuid();
        var payload = SpeechPlaybackProgressBridge.ToPayload(new(
            sessionId,
            messageId,
            "AI-Nachricht",
            playbackId,
            7,
            0,
            1,
            [unit.Id],
            SpeechPlaybackState.Playing,
            [unit]));

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        Assert.Contains($"\"sessionId\":\"{sessionId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"sourceMessageId\":\"{messageId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains($"\"playbackId\":\"{playbackId:D}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"eventSequence\":7", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"playing\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceUnitIds\":[\"u0001\"]", json, StringComparison.Ordinal);
        Assert.Contains("Visible original sentence.", json, StringComparison.Ordinal);
        Assert.DoesNotContain("speechText", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebSpeechProgressHighlightsSourcesWithoutAutomaticScrolling()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var styles = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("case \"speech.progress\":", app, StringComparison.Ordinal);
        Assert.Contains("updateSpeechProgress(payload)", app, StringComparison.Ordinal);
        Assert.Contains("CSS.highlights.set", app, StringComparison.Ordinal);
        Assert.Contains("speechSourceRangeMap(content, sourceUnits)", app, StringComparison.Ordinal);
        Assert.Contains("activeSourceUnitIds.slice(0, 1)", app, StringComparison.Ordinal);
        Assert.Contains("incomingPlaybackId !== currentPlaybackId", app, StringComparison.Ordinal);
        Assert.Contains("if (!payload?.isSpeaking || !wasSpeaking || payload?.error)", app, StringComparison.Ordinal);
        Assert.Contains("previousScrollTop", app, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollIntoView", app, StringComparison.Ordinal);
        Assert.Contains("::highlight(missum-speech-current)", styles, StringComparison.Ordinal);
        Assert.Contains("background-color: Highlight", styles, StringComparison.Ordinal);
        Assert.Contains("\"speech.progress\"", bridge, StringComparison.Ordinal);
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("speech.progress"));
        Assert.False(AssistantWebBridge.IsOutgoingTypeAllowed("chat.removed"));
    }

    [Fact]
    public void ArtifactImagePreviewUsesAnEmbeddedPngDataUrl()
    {
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

        var url = AssistantArtifactPreviewService.BuildImageDataUrl(png);

        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        Assert.Equal(png, Convert.FromBase64String(url[(url.IndexOf(',', StringComparison.Ordinal) + 1)..]));
    }

    [Fact]
    public async Task ArtifactImagesCanBeMaterializedAndOpenedThroughTheBridge()
    {
        Assert.True(AssistantWebBridge.IsIncomingTypeAllowed("artifact.open"));
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("\"artifact.open\"", bridge, StringComparison.Ordinal);
        Assert.Contains("post(\"artifact.open\", { artifactId: artifact.id })", app, StringComparison.Ordinal);

        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Bild öffnen");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bild", MessageStatus.Completed);
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)).ToLowerInvariant();
        await using var source = new MemoryStream(png, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "test-image",
            "plot.png",
            "image/png",
            sha256,
            png.Length,
            "missum-ai",
            null,
            null,
            source);
        var cacheRoot = Path.Combine(environment.Directory, "preview-cache");
        using var previews = new AssistantArtifactPreviewService(
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            cacheRoot);

        var path = await previews.MaterializeOriginalAsync(artifact.Id, CancellationToken.None);

        Assert.Equal(Path.Combine(cacheRoot, artifact.Id.ToString("N"), "original.png"), path);
        Assert.Equal(png, await File.ReadAllBytesAsync(path));
    }


    [Fact]
    public async Task ArtifactAudioPreviewIsMaterializedForTheStaticWebViewHost()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Audio-Vorschau");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.User, "Audio", MessageStatus.Completed);
        var wave = SystemAudioAnalysisCaptureService.CreateWave(new byte[SystemAudioAnalysisCaptureService.SampleRate * sizeof(short)]);
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave)).ToLowerInvariant();
        await using var source = new MemoryStream(wave, writable: false);
        var artifact = await environment.Get<IChatArtifactRepository>().ImportAsync(
            message.Id,
            "test-audio",
            "Missum-Systemaudio-Test.wav",
            "audio/wav",
            sha256,
            wave.Length,
            "screen-capture",
            null,
            null,
            source);
        var cacheRoot = Path.Combine(environment.Directory, "preview-cache");
        using var previews = new AssistantArtifactPreviewService(
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IBinaryObjectStore>(),
            cacheRoot);

        var preview = await previews.PrepareAsync(artifact.Id, CancellationToken.None);

        Assert.Equal($"https://{AssistantArtifactPreviewService.VirtualHost}/{artifact.Id:N}/media.wav", preview.Url);
        var cached = Path.Combine(cacheRoot, artifact.Id.ToString("N"), "media.wav");
        Assert.True(File.Exists(cached));
        Assert.Equal(wave, await File.ReadAllBytesAsync(cached));
    }

    [Fact]
    public void WebViewAllowsStaticAudioAndVideoPreviewResources()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("media-src 'self' https://assistant-preview.local", html, StringComparison.Ordinal);
        Assert.Contains("mediaType.startsWith(\"audio/\") || mediaType.startsWith(\"video/\")", app, StringComparison.Ordinal);
        Assert.Contains("audio.src = previewUrl", app, StringComparison.Ordinal);
        Assert.Contains("video.src = previewUrl", app, StringComparison.Ordinal);
        Assert.Contains("Audio speichern", app, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalChatCanAutonomouslyUseImageAndMediaTools()
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(null);
        Assert.Contains("image.generate", tools);
        Assert.Contains("media.analyze", tools);
        Assert.Contains("web.search", tools);
        Assert.Contains("web.fetch", tools);
    }

    [Theory]
    [InlineData("Welche Karten sind heute im Pokémon 30th Anniversary Set erschienen? Zeige Bilder.")]
    [InlineData("Was ist die aktuellste Version von WebView2?")]
    public void CurrentQuestionsTriggerResearchWithoutWebSearchChip(string prompt)
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(null, prompt);
        Assert.Contains("web.search", tools);
        Assert.Contains("web.fetch", tools);
    }

    [Fact]
    public void WebSearchActionReceivesSearchAndFetchTools()
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(PromptTriggerAction.WebSearch);

        Assert.Equal(["web.search", "web.fetch"], tools);
    }

    [Fact]
    public void GeneralWebSearchReadsRelevantPagesBeforePreparingTheAnswer()
    {
        const string prompt = "Vergleiche die aktuellen WebView2-APIs und nenne die Quellen.";

        var transformed = MissumAiAssistantService.BuildWebResearchPrompt(prompt);

        Assert.Contains("[MISSUM_WEB_RESEARCH_REQUEST]", transformed, StringComparison.Ordinal);
        Assert.Contains("isolierten SDK-Schritten", transformed, StringComparison.Ordinal);
        Assert.Contains("Evidenzdossier", transformed, StringComparison.Ordinal);
        Assert.Contains("Titel und URL", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("web.search", transformed, StringComparison.Ordinal);
        Assert.DoesNotContain("web.fetch", transformed, StringComparison.Ordinal);
        Assert.EndsWith(prompt, transformed, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentAnswersKeepInlineCitationsButRemoveTheEvidenceFooter()
    {
        const string response = "Die XREF-Vorlage wird im Projekt geladen [Anleitung_C.A.T.S.pdf, S. 12].\n\n"
            + "**Verwendete Dokumentbelege:** [Anleitung_C.A.T.S.pdf, S. 1]; [Anleitung_C.A.T.S.pdf, S. 12]";

        var cleaned = MissumAiAssistantService.RemoveDocumentEvidenceFooter(response);

        Assert.Equal("Die XREF-Vorlage wird im Projekt geladen [Anleitung_C.A.T.S.pdf, S. 12].", cleaned);
    }

    [Fact]
    public void ToolOnlyAssistantCardsAreExcludedFromTheNextServerRunHistory()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        ChatMessage[] history =
        [
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Warum wirken manche Möbel altmodisch?", MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.Assistant, "Das hat mehrere kulturelle und wirtschaftliche Ursachen.", MessageStatus.Completed, now, now),
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Lies die ausgewählte Nachricht vor", MessageStatus.Completed, now, now),
            new(
                Guid.NewGuid(),
                sessionId,
                ChatRole.Assistant,
                string.Empty,
                MessageStatus.Completed,
                now,
                now,
                ToolExecution: new ToolExecutionInfo("Vorlesen", "AI-Nachricht", "Abgeschlossen")),
            new(Guid.NewGuid(), sessionId, ChatRole.User, "Erkläre den genannten Punkt genauer.", MessageStatus.Completed, now, now),
        ];

        var messages = MissumAiAssistantService.BuildHistoryMessages(history, 400_000);

        Assert.Equal(4, messages.Count);
        Assert.Equal("Erkläre den genannten Punkt genauer.", Assert.Single(messages[^1].Content).Text);
        Assert.DoesNotContain(
            messages.SelectMany(static message => message.Content),
            static part => string.IsNullOrWhiteSpace(part.Text)
                && string.IsNullOrWhiteSpace(part.UploadId)
                && string.IsNullOrWhiteSpace(part.ArtifactId));
    }

    [Fact]
    public void ComposerKeepsLiveDraftAndPersistentSessionToolsAcrossHostUpdates()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains(
            "new Set([\"builtin.audiobook/create\", \"builtin.coding/plan-mode\"])",
            app,
            StringComparison.Ordinal);
        Assert.Contains("!persistentExtensionActionIds.has(previousExtensionActionId)", app, StringComparison.Ordinal);
        Assert.Contains("clearCompletedOneShotToolAction();", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.completed\":", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.cancelled\":", app, StringComparison.Ordinal);
        Assert.Contains("case \"chat.failed\":", app, StringComparison.Ordinal);

        // A mode or project-folder snapshot may arrive before session.draft is saved.
        // Only opening another session may replace the live text, including a cleared draft.
        var snapshotBlock = app[
            app.IndexOf("function applySnapshot(payload)", StringComparison.Ordinal)..
            app.IndexOf("function applyConversationSnapshot(payload)", StringComparison.Ordinal)];
        var draftAssignments = System.Text.RegularExpressions.Regex.Matches(
            snapshotBlock,
            @"setPromptValue\(");
        Assert.Single(draftAssignments.Cast<System.Text.RegularExpressions.Match>());
        Assert.Matches(
            @"if \(sessionChanged\)\s*\{\s*setPromptValue\(payload\.draft \|\| """"\);\s*\}",
            snapshotBlock);
    }

    [Fact]
    public void ComposerCollapsesTwoOrMoreAttachedFilesIntoAnUpwardOverlayMenu()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var css = File.ReadAllText(Path.Combine(webRoot, "styles.css"));

        Assert.Contains("if (attachedFiles.length < 2)", app, StringComparison.Ordinal);
        Assert.Contains("summary.className = \"active-tool-chip attachment-summary\"", app, StringComparison.Ordinal);
        Assert.Contains("menu.className = \"attachment-menu\"", app, StringComparison.Ordinal);
        Assert.Contains("removeAll.className = \"attachment-summary__remove active-tool-chip__remove\"", app, StringComparison.Ordinal);
        Assert.Contains("Alle Dateianhänge entfernen", app, StringComparison.Ordinal);
        Assert.Contains("document-preparation-status", app, StringComparison.Ordinal);
        Assert.Contains("document.import.started", app, StringComparison.Ordinal);
        Assert.Contains("\"document.import.started\"", File.ReadAllText(Path.Combine(webRoot, "bridge.js")), StringComparison.Ordinal);
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("document.import.started"));
        Assert.Contains("wird verarbeitet", app, StringComparison.Ordinal);
        Assert.Contains("@keyframes document-status-spin", css, StringComparison.Ordinal);
        Assert.Contains("bottom: calc(100% + 8px)", css, StringComparison.Ordinal);
        Assert.Contains("position: absolute", css, StringComparison.Ordinal);
    }

    [Fact]
    public void RunningMessageShowsItsModelAndPreservesContextWhenUpdatesOmitTokenCounts()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("runStatusText(liveStatus)", app, StringComparison.Ordinal);
        Assert.Contains("uniqueStatusParts(detail)", app, StringComparison.Ordinal);
        var startedBlock = app[
            app.IndexOf("case \"chat.started\":", StringComparison.Ordinal)..
            app.IndexOf("case \"chat.delta\":", StringComparison.Ordinal)];
        Assert.Contains("if (payload.message?.id) state.messageRunStatus.set(String(payload.message.id)", startedBlock, StringComparison.Ordinal);
        Assert.Contains("model: payload.model || state.model || null", startedBlock, StringComparison.Ordinal);
        Assert.Contains("renderMessages(true);", startedBlock, StringComparison.Ordinal);
        Assert.Contains("cleanStatusMetadata", app, StringComparison.Ordinal);
        Assert.DoesNotContain("Kontexttoken", app, StringComparison.Ordinal);
        Assert.Contains("if (Number.isFinite(payload.contextUsed))", app, StringComparison.Ordinal);
        Assert.Contains("function visibleModelLabel(value)", app, StringComparison.Ordinal);
        Assert.Contains("model.replace(/\\s*·\\s*MXFP4", app, StringComparison.Ordinal);
        Assert.Contains("state.messageRunStatus.clear();", app, StringComparison.Ordinal);
        Assert.Contains("const acceptsRunStatus =", app, StringComparison.Ordinal);
        Assert.Contains("!isTerminalMessageStatus(statusMessage?.status)", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeechUsesComposerStatusWithoutRetryOrChatRendering()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        var bridge = File.ReadAllText(Path.Combine(webRoot, "bridge.js"));

        Assert.Contains("id=\"composer-speech-status\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"composer-speech-pause\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"composer-speech-stop\"", html, StringComparison.Ordinal);
        Assert.Contains("title=\"Vorlesen beenden\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-previous", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-skip", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Vorheriger Absatz", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Absatz überspringen", html, StringComparison.Ordinal);
        Assert.DoesNotContain("composer-speech-pause-label", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("id=\"composer-speech-status\"", StringComparison.Ordinal)
            < html.IndexOf("<div class=\"composer\">", StringComparison.Ordinal));
        Assert.Contains("case \"speech.status\":", app, StringComparison.Ordinal);
        Assert.Contains("renderSpeechStatus();", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.toggleSpeechPause\"", app, StringComparison.Ordinal);
        Assert.Contains("elements.composerSpeechStop.addEventListener", app, StringComparison.Ordinal);
        Assert.Contains("post(\"microphone.stopSpeech\", {});", app, StringComparison.Ordinal);
        var promptStop = app.IndexOf("async function handleComposerAction()", StringComparison.Ordinal);
        var promptSubmit = app.IndexOf("async function submitPrompt()", promptStop, StringComparison.Ordinal);
        Assert.True(promptStop >= 0 && promptSubmit > promptStop);
        Assert.DoesNotContain(
            "microphone.stopSpeech",
            app[promptStop..promptSubmit],
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"chat.removed\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.previousSpeechParagraph", app, StringComparison.Ordinal);
        Assert.DoesNotContain("microphone.skipSpeechParagraph", app, StringComparison.Ordinal);
        Assert.Contains("isPaused ? \"Fortsetzen\" : \"Pausieren\"", app, StringComparison.Ordinal);
        Assert.Contains("messageId: String(message.id)", app, StringComparison.Ordinal);
        Assert.Contains("sessionId: state.activeSessionId", app, StringComparison.Ordinal);
        Assert.Contains("\"speech.status\"", bridge, StringComparison.Ordinal);
        Assert.DoesNotContain("Erneut senden", app, StringComparison.Ordinal);
        Assert.DoesNotContain("retryMessage", app, StringComparison.Ordinal);
        Assert.DoesNotContain("createMessageFooterLink(\"Fortsetzen\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("function continueMessage()", app, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadAloudRemainsIndependentAndScrollIsPersistedOnlyAcrossSessionChanges()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.Contains("globalThis.missumRunSteering?.canSteer(state)", app, StringComparison.Ordinal);
        Assert.Contains("if (sessionChanged && previousSessionId) persistSessionScrollPosition(previousSessionId);", app, StringComparison.Ordinal);
        Assert.Contains("if (sessionChanged) restoreSessionScrollPosition(state.activeSessionId);", app, StringComparison.Ordinal);
        Assert.Contains("sessionScrollStoragePrefix", app, StringComparison.Ordinal);
        Assert.Contains("anchorMessageId", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(currentSessionMessagesChanged);", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(messagesChanged);", app, StringComparison.Ordinal);
        Assert.Contains("renderMessages(true);", app, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduleSessionScrollSave", app, StringComparison.Ordinal);
        Assert.DoesNotContain("elements.messageScroll.addEventListener(\"scroll\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener(\"pagehide\", () => persistSessionScrollPosition", app, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveForSpeech", app, StringComparison.Ordinal);
        Assert.Contains("&& isVoiceControlActive()", app, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.message?.contentProfile !== \"audiobook\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeechAnchorSelectsTheRequestedBlockAndEverythingAfterIt()
    {
        var units = SpeechSourceSegmentation.CreateUnits("Erster Absatz.\n\nZweiter Absatz.\n\nDritter Absatz.");
        var selected = MissumAiAssistantService.SelectSpeechUnitsFromAnchor(
            units,
            new SpeechStartAnchor("paragraph", 1));

        Assert.DoesNotContain(selected, unit => unit.Text.Contains("Erster", StringComparison.Ordinal));
        Assert.Contains(selected, unit => unit.Text.Contains("Zweiter", StringComparison.Ordinal));
        Assert.Contains(selected, unit => unit.Text.Contains("Dritter", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadFromHereOnlyAcceptsStableAuthoritativeAssistantBlocks()
    {
        var sessionId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow;
        var anchor = new SpeechStartAnchor("paragraph", 0);
        foreach (var status in new[]
                 {
                     MessageStatus.Completed,
                     MessageStatus.Cancelled,
                     MessageStatus.Interrupted,
                     MessageStatus.Failed,
                 })
        {
            var message = new ChatMessage(
                messageId,
                sessionId,
                ChatRole.Assistant,
                "Ein stabil gespeicherter Absatz.",
                status,
                updatedAt.AddMinutes(-1),
                updatedAt);
            MissumAiAssistantService.ValidateAnchoredSpeechMessage(
                message,
                sessionId,
                updatedAt,
                anchor);
        }

        var streaming = new ChatMessage(
            messageId,
            sessionId,
            ChatRole.Assistant,
            "Noch nicht stabil.",
            MessageStatus.Streaming,
            updatedAt.AddMinutes(-1),
            updatedAt);
        Assert.Throws<InvalidOperationException>(() =>
            MissumAiAssistantService.ValidateAnchoredSpeechMessage(
                streaming,
                sessionId,
                updatedAt,
                anchor));
        Assert.Throws<InvalidOperationException>(() =>
            MissumAiAssistantService.ValidateAnchoredSpeechMessage(
                streaming with { Status = MessageStatus.Completed },
                sessionId,
                updatedAt.AddSeconds(1),
                anchor));
    }

    [Fact]
    public void ReadFromHereExcerptRequiresStableMatchingSessionAndRevision()
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.User,
            "Erster Absatz.\n\nZweiter Absatz.\n\nDritter Absatz.", MessageStatus.Completed, now, now);
        MissumAiAssistantService.ValidateSpeechExcerpt(message, message.SessionId, now, "Zweiter Absatz.\n\nDritter Absatz.");
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateSpeechExcerpt(message, Guid.NewGuid(), now, "Zweiter Absatz."));
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateSpeechExcerpt(message, message.SessionId, now.AddSeconds(-1), "Zweiter Absatz."));
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateSpeechExcerpt(message with { Status = MessageStatus.Streaming }, message.SessionId, now, "Zweiter Absatz."));
        Assert.Throws<InvalidOperationException>(() => MissumAiAssistantService.ValidateSpeechExcerpt(message, message.SessionId, now, " "));
    }

    [Fact]
    public void FooterSpeechAcceptsEveryStableStoredAssistantMessage()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var status in new[]
                 {
                     MessageStatus.Completed,
                     MessageStatus.Cancelled,
                     MessageStatus.Interrupted,
                     MessageStatus.Failed,
                 })
        {
            var message = new ChatMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ChatRole.Assistant,
                "Der vorherige AI-Lauf wurde beim Clientstart gestoppt.",
                status,
                now,
                now);
            Assert.True(MissumAiAssistantService.IsReadableSpeechMessage(message));
        }

        var invalid = new ChatMessage(
            Guid.NewGuid(),
            Guid.NewGuid(),
            ChatRole.Assistant,
            "Noch nicht stabil.",
            MessageStatus.Streaming,
            now,
            now);
        Assert.False(MissumAiAssistantService.IsReadableSpeechMessage(invalid));
        Assert.True(MissumAiAssistantService.IsReadableSpeechMessage(
            invalid with { Role = ChatRole.User, Status = MessageStatus.Completed }));
        Assert.False(MissumAiAssistantService.IsReadableSpeechMessage(
            invalid with { Content = string.Empty, Status = MessageStatus.Completed }));
    }

    [Fact]
    public void WebViewAnnotatesReadableBlocksForTheNativeReadFromHereMenu()
    {
        var target = new ReadFromContextTarget(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "tableRow",
            2);
        Assert.True(AssistantWebBridge.IsValidReadFromContextTarget(target));
        Assert.False(AssistantWebBridge.IsValidReadFromContextTarget(target with { Kind = "button" }));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("microphone.previousSpeechParagraph"));
        Assert.False(AssistantWebBridge.IsIncomingTypeAllowed("microphone.skipSpeechParagraph"));

        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        Assert.Contains("annotateReadableSpeechBlocks(message, article, content)", app, StringComparison.Ordinal);
        Assert.Contains("globalThis.missumGetReadFromContextTarget", app, StringComparison.Ordinal);
        Assert.Contains("data-speech-block-kind", app, StringComparison.Ordinal);
        Assert.Contains("messageUpdatedAt", app, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaTransportButtonsMapToSpeechControls()
    {
        Assert.Equal(
            SpeechMediaTransportCommand.Play,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Play));
        Assert.Equal(
            SpeechMediaTransportCommand.Pause,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Pause));
        Assert.Equal(
            SpeechMediaTransportCommand.None,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Next));
        Assert.Equal(
            SpeechMediaTransportCommand.None,
            SpeechMediaTransportController.ResolveCommand(Windows.Media.SystemMediaTransportControlsButton.Previous));
    }

    [Fact]
    public void MediaTransportControllerRegistersAndReleasesNativeControls()
    {
        using var controller = new SpeechMediaTransportController(
            _ => Task.CompletedTask,
            exception => throw new InvalidOperationException("Die Windows-Mediensteuerung ist fehlgeschlagen.", exception));

        controller.Activate();
        controller.SetPlaying(paused: false);
        controller.SetPlaying(paused: true);
        controller.Deactivate();
    }

    [Fact]
    public void AudiobookToolRemainsPersistentInTheComposer()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        var app = File.ReadAllText(Path.Combine(webRoot, "app.js"));

        Assert.DoesNotContain("data-tool-action=\"audiobook\"", html, StringComparison.Ordinal);
        Assert.Contains("builtin.audiobook/create", app, StringComparison.Ordinal);
        Assert.DoesNotContain("displayName: \"Hörbuch erstellen\"", app, StringComparison.Ordinal);
        Assert.DoesNotContain("payload.message?.contentProfile !== \"audiobook\"", app, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookContextIncludesInterruptedStoryTextButExcludesEarlierGeneralConversation()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var generalUser = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Allgemeine Frage", MessageStatus.Completed, now, now);
        var generalAnswer = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.Assistant, "Allgemeine Antwort", MessageStatus.Completed, now, now);
        var storyPrompt = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Eine Geschichte im Maschinenraum", MessageStatus.Completed, now.AddSeconds(1), now.AddSeconds(1));
        var firstChapter = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "Im Maschinenraum begann die Reise.", MessageStatus.Completed,
            now.AddSeconds(2), now.AddSeconds(2), ContentProfile: MessageContentProfile.Audiobook);
        var direction = new ChatMessage(Guid.NewGuid(), sessionId, ChatRole.User, "Fortsetzen: Ein Alarm ertönt.", MessageStatus.Completed, now.AddSeconds(3), now.AddSeconds(3));
        var interrupted = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "Plötzlich heulte die Sirene", MessageStatus.Interrupted,
            now.AddSeconds(4), now.AddSeconds(4), ContentProfile: MessageContentProfile.Audiobook);

        var eligible = SessionContextPreparationService.SelectEligibleHistory(
            [generalUser, generalAnswer, storyPrompt, firstChapter, direction, interrupted],
            SessionContextProfile.Audiobook);

        Assert.Equal([storyPrompt.Id, firstChapter.Id, direction.Id, interrupted.Id], eligible.Select(static item => item.Id));
    }

    [Fact]
    public void AudiobookPromptCreatesOneChapterAndContinuationStartsAtTheLatestScene()
    {
        var first = AssistantCoordinator.CreateToolMatch(
            "audiobook",
            "Eine Ingenieurin entdeckt unter Berlin einen verlassenen Maschinenraum.");
        var continuation = AssistantCoordinator.CreateToolMatch(
            "audiobook",
            "Fortsetzen: Der Generator springt unerwartet an.");

        var firstPrompt = MissumAiAssistantService.BuildAudiobookPrompt(first, first.OriginalPrompt, hasAudiobookHistory: false);
        var nextPrompt = MissumAiAssistantService.BuildAudiobookPrompt(continuation, continuation.OriginalPrompt, hasAudiobookHistory: true);

        Assert.Contains("erste Kapitel", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("# Kapitel eins – Titel", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("eintausendfünfhundert bis zweitausendfünfhundert Wörter", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("Hauptfigur", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("langfristigen Leitfaden", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("zukünftige Handlungsfäden", firstPrompt, StringComparison.Ordinal);
        Assert.Contains("unmittelbar letzte Szene", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("langfristigen Serienleitfaden", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("Perspektive der Hauptfigur", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("neuer AI-Lauf ist ausdrücklich keine Kapitelgrenze", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("ohne neue Kapitelüberschrift", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("tatsächlich ein neues Kapitel beginnt", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("zwei Prozent", nextPrompt, StringComparison.Ordinal);
        Assert.Contains("Der Generator springt unerwartet an.", nextPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Fortsetzen:", nextPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacySpeechToolMessagesAreExcludedFromPersistentSessionContext()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var speechCommand = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.User, "Lies die ausgewählte Nachricht vor",
            MessageStatus.Completed, now, now);
        var legacySpeechCard = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, string.Empty,
            MessageStatus.Completed, now.AddSeconds(1), now.AddSeconds(1),
            ToolExecution: new ToolExecutionInfo("Vorlesen", "AI-Nachricht", "Abgeschlossen"));
        var normalUser = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.User, "Wie hoch ist der Volumenstrom?",
            MessageStatus.Completed, now.AddSeconds(2), now.AddSeconds(2));
        var normalAssistant = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, "4.200 m³/h.",
            MessageStatus.Completed, now.AddSeconds(3), now.AddSeconds(3));

        var eligible = SessionContextPreparationService.SelectEligibleHistory(
            [speechCommand, legacySpeechCard, normalUser, normalAssistant]);

        Assert.Equal([normalUser.Id, normalAssistant.Id], eligible.Select(static item => item.Id));
    }

    [Fact]
    public void DocumentHistoryReserveAndBlockTargetsScaleWithAvailableHistory()
    {
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var longMessage = new ChatMessage(
            Guid.NewGuid(), sessionId, ChatRole.Assistant, new string('x', 60_000),
            MessageStatus.Completed, now, now);

        Assert.Equal(1_024, MissumAiAssistantService.CalculateDocumentHistoryReserveTokens([]));
        Assert.InRange(
            MissumAiAssistantService.CalculateDocumentHistoryReserveTokens([longMessage]),
            4_096,
            16_384);
        Assert.True(SessionContextPreparationService.CalculateBlockSummaryTarget(4_000, 4) < 4_000);
    }

    [Fact]
    public void SseReconnectBudgetResetsAfterPersistedEventProgress()
    {
        Assert.Equal(4, MissumAiAssistantService.ReconnectAttemptsAfterProgress(4, 120, 120));
        Assert.Equal(0, MissumAiAssistantService.ReconnectAttemptsAfterProgress(4, 120, 121));
    }

    [Fact]
    public void GeneralPromptOnlyRetriesFailuresMarkedAsRecoverable()
    {
        Assert.True(MissumAiAssistantService.ShouldRetryCurrentPrompt(
            action: null,
            new MissumAiRunTerminalException("run.timeout", "Zeitlimit", retryable: true)));
        Assert.False(MissumAiAssistantService.ShouldRetryCurrentPrompt(
            action: null,
            new MissumAiRunTerminalException("run.invalid_operation", "Ungültiger Auftrag", retryable: false)));
        Assert.True(MissumAiAssistantService.IsRetryableServerErrorCode("provider.generation_terminated"));
    }

    [Fact]
    public void PromptAndStreamRetryBackoffIsBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), MissumAiAssistantService.PromptRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(30), MissumAiAssistantService.PromptRetryDelay(100));
        Assert.Equal(TimeSpan.FromMilliseconds(500), MissumAiAssistantService.StreamReconnectDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(30), MissumAiAssistantService.StreamReconnectDelay(100));
    }

    private static ChatMessage Message(Guid sessionId, string content) => new(
        Guid.NewGuid(), sessionId, ChatRole.Assistant, content, MessageStatus.Completed,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("Person 1: Die Lüftungsanlage läuft.", null, "**Live-Untertitel**", "Die Lüftungsanlage läuft")]
    [InlineData("Person 1: Die Lüftungsanlage läuft.\nPerson 2: Ich prüfe den Volumenstrom.", null, "**Live-Untertitel**", "Person 1: Die Lüftungsanlage läuft.\n\nPerson 2: Ich prüfe den Volumenstrom.")]
    [InlineData("", "Missum AI Server ist nicht erreichbar.", "**Live-Untertitel fehlgeschlagen**", "Missum AI Server ist nicht erreichbar")]
    [InlineData("", null, "**Live-Untertitel**", "Es wurde kein Sprachinhalt erkannt")]
    public async Task CompletedCaptionStatesBecomePersistentChatMessages(
        string transcript,
        string? error,
        string expectedTitle,
        string expectedDetail)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await coordinator.AddLiveCaptionResultAsync(transcript, error);

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var message = Assert.Single(await environment.Get<IChatRepository>().ListMessagesAsync(sessionId));
        Assert.Equal(ChatRole.Assistant, message.Role);
        Assert.Equal(MessageStatus.Completed, message.Status);
        Assert.Contains(expectedTitle, message.Content, StringComparison.Ordinal);
        Assert.Contains(expectedDetail, message.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildingSessionSnapshotUsesMissumAiServerStateOnly()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var snapshot = await coordinator.BuildSnapshotAsync();

        Assert.NotNull(snapshot);
    }

    [Theory]
    [InlineData(false, "measured")]
    [InlineData(true, "measured")]
    [InlineData(false, "estimated")]
    [InlineData(true, "estimated")]
    public async Task TabAndSessionSnapshotsRestoreContextSourceAndRunningMessageWithoutCrossSessionLeakage(bool coding, string contextSource)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            SelectedModel = "general-a", SelectedCodingModel = "coding-a",
        });
        var chats = environment.Get<IChatRepository>();
        var mode = coding ? ChatMode.Coding : ChatMode.General;
        var active = await chats.CreateSessionAsync("Aktiv", mode);
        var other = await chats.CreateSessionAsync("Andere Sitzung", mode);
        if (coding) await chats.SetCodingWorkspacePathAsync(active.Id, environment.Directory);
        var message = await chats.AddMessageAsync(active.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
            Status: "Denkt nach", Detail: "12.345 Token", Model: coding ? "coding-a" : "general-a",
            ContextUsed: 12345, ContextLimit: 32768, LoadedFiles: 4, ContextSource: contextSource), static (_, _, _) => Task.CompletedTask, "progress");

        var first = await OpenAndCaptureSnapshotAsync(coordinator, active.Id);
        Assert.True(first.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Denkt nach", first.GetProperty("runStatus").GetString());
        Assert.Equal("12.345 Token", first.GetProperty("runDetail").GetString());
        Assert.Equal(message.Id, first.GetProperty("runMessageId").GetGuid());
        Assert.Equal(contextSource, first.GetProperty("contextSource").GetString());
        Assert.Equal(12345, first.GetProperty("contextUsed").GetInt32());
        Assert.Equal(4, first.GetProperty("loadedFiles").GetInt32());

        var otherSnapshot = await OpenAndCaptureSnapshotAsync(coordinator, other.Id);
        Assert.False(otherSnapshot.GetProperty("isRunning").GetBoolean());
        Assert.True(otherSnapshot.GetProperty("isAiBusy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, otherSnapshot.GetProperty("runStatus").ValueKind);
        Assert.Equal("estimated", otherSnapshot.GetProperty("contextSource").GetString());
        var returned = await OpenAndCaptureSnapshotAsync(coordinator, active.Id);
        Assert.Equal(contextSource, returned.GetProperty("contextSource").GetString());
        Assert.Equal(first.GetProperty("contextUsed").GetInt32(), returned.GetProperty("contextUsed").GetInt32());
        Assert.Equal(first.GetProperty("runDetail").GetString(), returned.GetProperty("runDetail").GetString());

        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Started, message,
            Status: "Wird fortgesetzt", Detail: "SSE erneut verbunden"), static (_, _, _) => Task.CompletedTask, "resume");
        var resumed = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(contextSource, resumed.GetProperty("contextSource").GetString());
        Assert.Equal(12345, resumed.GetProperty("contextUsed").GetInt32());
        Assert.Equal("12.345 Token", resumed.GetProperty("runDetail").GetString());

        await chats.UpdateMessageAsync(message.Id, "Fertig", MessageStatus.Completed);
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Completed, message with { Status = MessageStatus.Completed },
            Status: "Abgeschlossen"), static (_, _, _) => Task.CompletedTask, "done");
        var completed = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.False(completed.GetProperty("isRunning").GetBoolean());
        Assert.False(completed.GetProperty("isAiBusy").GetBoolean());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("runMessageId").ValueKind);
        Assert.Equal(12345, completed.GetProperty("contextUsed").GetInt32());
    }

    [Theory]
    [InlineData("Abgebrochen", false)]
    [InlineData("Abgebrochen", true)]
    [InlineData("Fehlgeschlagen", false)]
    [InlineData("Fehlgeschlagen", true)]
    [InlineData("Fertig", false)]
    [InlineData("Fertig", true)]
    public async Task ContinuedAnchorSnapshotsReplaceTerminalStatusEvenAfterPreparation(string oldStatus, bool prepare)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Ursprünglicher Auftrag", ChatMode.General);
        var turn = await chats.AddTurnAsync(session.Id, "Bearbeite den ursprünglichen Auftrag.");
        var message = turn.AssistantMessage;
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        await OpenAndCaptureSnapshotAsync(coordinator, session.Id);
        var terminal = oldStatus switch
        {
            "Abgebrochen" => MissumAiAssistantUpdateKind.Cancelled,
            "Fehlgeschlagen" => MissumAiAssistantUpdateKind.Failed,
            _ => MissumAiAssistantUpdateKind.Completed,
        };
        await coordinator.EmitMissumAiUpdateAsync(new(terminal, message, Status: oldStatus, Detail: "Vorheriger Versuch"),
            static (_, _, _) => Task.CompletedTask, "terminal");
        if (prepare)
            await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Status, message,
                Status: "Vorbereitung", Detail: "Vorbereitung des neuen Versuchs"), static (_, _, _) => Task.CompletedTask, "prepare");
        await chats.UpdateMessageAsync(message.Id, "Gespeicherte Teilantwort.", MessageStatus.Streaming);
        await coordinator.EmitMissumAiUpdateAsync(new(MissumAiAssistantUpdateKind.Started, message with { Status = MessageStatus.Streaming },
            Status: "Lauf wird fortgesetzt", Detail: "Neuer Versuch desselben Auftrags",
            ToolStep: new("continuation:test", "assistant.continuation", "completed")), static (_, _, _) => Task.CompletedTask, "continue");
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.True(snapshot.GetProperty("isRunning").GetBoolean());
        Assert.Equal("Lauf wird fortgesetzt", snapshot.GetProperty("runStatus").GetString());
        Assert.Equal("Neuer Versuch desselben Auftrags", snapshot.GetProperty("runDetail").GetString());
        Assert.Equal(message.Id, snapshot.GetProperty("runMessageId").GetGuid());
        Assert.Equal(2, (await chats.ListMessagesAsync(session.Id)).Count);
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task ProjectPlusCreatesModeOwnedWorkspaceSessionWithoutChangingExistingHistory(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Bisherige Sitzung", mode);
        await chats.SetCodingWorkspacePathAsync(first.Id, environment.Directory, activateCoding: mode == ChatMode.Coding);
        var group = Assert.Single(await chats.ListSessionGroupsAsync(mode));
        await chats.SetSessionGroupCollapsedAsync(group.Id, true);
        var message = await chats.AddMessageAsync(first.Id, ChatRole.User, "Erhalten", MessageStatus.Completed);
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = mode,
            ActiveGeneralSessionId = mode == ChatMode.General ? first.Id : current.ActiveGeneralSessionId,
            ActiveCodingSessionId = mode == ChatMode.Coding ? first.Id : current.ActiveCodingSessionId,
            ActiveClaudeScienceSessionId = mode == ChatMode.ClaudeScience ? first.Id : current.ActiveClaudeScienceSessionId,
            ActiveSessionId = first.Id,
        });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        await HandleAsync(coordinator, "session.projectCreate", new
        {
            workspacePath = environment.Directory,
            chatMode = mode switch
            {
                ChatMode.General => "general",
                ChatMode.Coding => "coding",
                ChatMode.ClaudeScience => "claudescience",
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            },
        });
        var created = (await chats.ListSessionsAsync(mode)).Single(session => session.Id != first.Id);
        Assert.Equal(created.Id, settings.Current.ActiveSessionId);
        Assert.Equal(group.Id, created.SessionGroupId);
        Assert.Equal(environment.Directory, created.CodingWorkspacePath);
        Assert.Null(created.PersistentExtensionActionId);
        Assert.False(Assert.Single(await chats.ListSessionGroupsAsync(mode)).IsCollapsed);
        Assert.Equal(mode, created.ChatMode);
        Assert.Equal(mode, (await chats.GetSessionAsync(first.Id))!.ChatMode);
        Assert.NotNull(await chats.GetMessageAsync(message.Id));
    }

    [Fact]
    public async Task ProjectMemoryEventsIdentifyExplicitSessionAndDoNotLeakAcrossSessions()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var targetWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "memory-target")).FullName;
        var activeWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "memory-active")).FullName;
        var target = await chats.CreateSessionAsync("Gedächtnis-Ziel", ChatMode.Coding);
        var active = await chats.CreateSessionAsync("Aktive Coding-Sitzung", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(target.Id, targetWorkspace, activateCoding: true);
        await chats.SetCodingWorkspacePathAsync(active.Id, activeWorkspace, activateCoding: true);
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = ChatMode.Coding,
            ActiveCodingSessionId = active.Id,
            ActiveSessionId = active.Id,
        });
        await using var projectMemory = new SqliteProjectMemoryStore(new ProjectMemoryOptions
        {
            SharedDataDirectory = Path.Combine(environment.Directory, "project-memory"),
        });
        await projectMemory.InitializeAsync();
        var coordinator = CreateCoordinator(
            environment,
            settings,
            CreateRecentActivity(settings),
            projectMemory);

        var changed = await HandleAndCaptureAsync(
            coordinator,
            "memory.create",
            new { sessionId = target.Id, kind = "decision", content = "Nur für das Zielprojekt", source = "test" },
            "memory.changed");
        Assert.Equal(target.Id, changed.GetProperty("sessionId").GetGuid());
        Assert.Equal("Nur für das Zielprojekt", Assert.Single(changed.GetProperty("entries").EnumerateArray()).GetProperty("content").GetString());

        var activeSnapshot = await HandleAndCaptureAsync(
            coordinator,
            "memory.list",
            new { sessionId = active.Id },
            "memory.snapshot");
        Assert.Equal(active.Id, activeSnapshot.GetProperty("sessionId").GetGuid());
        Assert.Empty(activeSnapshot.GetProperty("entries").EnumerateArray());

        var targetSnapshot = await HandleAndCaptureAsync(
            coordinator,
            "memory.list",
            new { sessionId = target.Id },
            "memory.snapshot");
        Assert.Equal(target.Id, targetSnapshot.GetProperty("sessionId").GetGuid());
        Assert.Equal("Nur für das Zielprojekt", Assert.Single(targetSnapshot.GetProperty("entries").EnumerateArray()).GetProperty("content").GetString());
        Assert.Equal(active.Id, settings.Current.ActiveSessionId);
    }

    [Fact]
    public async Task OpeningSessionsAlwaysEmitsTheExactVisibleDatabaseRowsForThatSession()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erste Sitzung");
        var firstTurn = await chats.AddTurnAsync(first.Id, "Erste Frage");
        await chats.UpdateMessageAsync(firstTurn.AssistantMessage.Id, "Erste Antwort", MessageStatus.Completed);
        var second = await chats.CreateSessionAsync("Zweite Sitzung");
        var secondTurn = await chats.AddTurnAsync(second.Id, "Zweite Frage");
        await chats.UpdateMessageAsync(secondTurn.AssistantMessage.Id, "Zweite Antwort", MessageStatus.Completed);
        await settings.UpdateAsync(current => current with { ActiveSessionId = first.Id });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var secondSnapshot = await OpenAndCaptureSnapshotAsync(coordinator, second.Id);
        var firstSnapshot = await OpenAndCaptureSnapshotAsync(coordinator, first.Id);

        Assert.Equal(
            new[] { secondTurn.UserMessage.Id, secondTurn.AssistantMessage.Id },
            secondSnapshot.GetProperty("messages").EnumerateArray()
                .Select(static message => message.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(
            new[] { firstTurn.UserMessage.Id, firstTurn.AssistantMessage.Id },
            firstSnapshot.GetProperty("messages").EnumerateArray()
                .Select(static message => message.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("Erste Antwort", firstSnapshot.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ScienceSnapshotRepairsTheStoredCutTitleAndGeneratedIntroductionWithoutChangingTheResearch()
    {
        const string legacy = "Erstelle eine wissenschaftliche Publikation: Einsteinsche Feldgl";
        const string topic = "Einsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik";
        const string prompt = "Erstelle eine wissenschaftliche Publikation:\nEinsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik kombinieren und eine allgemeine Gleichung finden. Stelle mehrere verschiedene Hypothesen auf.";
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync(legacy, ChatMode.ClaudeScience);
        var user = await chats.AddMessageAsync(session.Id, ChatRole.User, prompt, MessageStatus.Completed);
        var answer = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Die vollständige Herleitung bleibt erhalten.", MessageStatus.Cancelled);
        await chats.SaveToolStepAsync(answer.Id, new("science-introduction:" + answer.Id.ToString("N"),
            "assistant.narration", "completed", "**" + legacy + "**\n\nDie Quellenprüfung bleibt offen.", ContentOffset: 0));
        await chats.SaveDraftAsync(session.Id, "Entwurf erhalten");
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id,
            ActiveClaudeScienceSessionId = session.Id, SelectedChatMode = ChatMode.ClaudeScience });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);

        Assert.Equal(topic, Assert.Single(snapshot.GetProperty("sessions").EnumerateArray()).GetProperty("title").GetString());
        var repaired = (await chats.GetSessionAsync(session.Id))!;
        Assert.Equal(topic, repaired.Title);
        Assert.Equal("Entwurf erhalten", repaired.Draft);
        Assert.Equal(prompt, (await chats.GetMessageAsync(user.Id))!.Content);
        var preserved = (await chats.GetMessageAsync(answer.Id))!;
        Assert.Equal("Die vollständige Herleitung bleibt erhalten.", preserved.Content);
        Assert.Equal(MessageStatus.Cancelled, preserved.Status);
        Assert.Equal("**" + topic + "**\n\nDie Quellenprüfung bleibt offen.", Assert.Single(preserved.ToolSteps!).Detail);
        await coordinator.BuildSnapshotAsync();
        var reopened = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        await reopened.BuildSnapshotAsync();
        Assert.Equal(repaired.ConversationRevision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
    }

    [Theory]
    [InlineData(ChatMode.ClaudeScience, "Mein selbst gewählter Forschungstitel")]
    [InlineData(ChatMode.General, "Erstelle eine wissenschaftliche Publikation: Einsteinsche Feldgl")]
    [InlineData(ChatMode.Coding, "Erstelle eine wissenschaftliche Publikation: Einsteinsche Feldgl")]
    public async Task ScienceTitleRepairPreservesCustomTitlesAndOtherModes(ChatMode mode, string title)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync(title, mode);
        await chats.AddMessageAsync(session.Id, ChatRole.User,
            "Erstelle eine wissenschaftliche Publikation:\nEinsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik kombinieren und eine allgemeine Gleichung finden.", MessageStatus.Completed);
        await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "# Ein anderer AI-Titel darf nicht die Sitzung benennen", MessageStatus.Completed);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { ActiveSessionId = session.Id,
            ActiveClaudeScienceSessionId = mode == ChatMode.ClaudeScience ? session.Id : current.ActiveClaudeScienceSessionId,
            ActiveGeneralSessionId = mode == ChatMode.General ? session.Id : current.ActiveGeneralSessionId,
            ActiveCodingSessionId = mode == ChatMode.Coding ? session.Id : current.ActiveCodingSessionId,
            SelectedChatMode = mode });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await coordinator.BuildSnapshotAsync();

        Assert.Equal(title, (await chats.GetSessionAsync(session.Id))!.Title);
    }

    [Fact]
    public async Task NewSciencePromptStoresTheCompleteSubjectBeforeConnectingToTheModel()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Neue Sitzung", ChatMode.ClaudeScience);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(coordinator, "chat.send", new
        {
            sessionId = session.Id,
            prompt = "Erstelle eine wissenschaftliche Publikation:\nEinsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik kombinieren und eine allgemeine Gleichung finden.",
        }));

        Assert.Equal("Einsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik",
            (await chats.GetSessionAsync(session.Id))!.Title);
    }

    [Fact]
    public async Task ScienceFollowupDoesNotReplaceTheOriginalResearchTitle()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        const string title = "Quantenfelder in gekrümmter Raumzeit";
        var session = await chats.CreateSessionAsync(title, ChatMode.ClaudeScience);
        await chats.AddMessageAsync(session.Id, ChatRole.User, "Erstelle eine wissenschaftliche Publikation: " + title, MessageStatus.Completed);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        // No model service in this fixture: naming is committed before the
        // unavailable transport, so the real prompt handler is still exercised.
        await Assert.ThrowsAsync<InvalidOperationException>(() => HandleAsync(coordinator, "chat.send",
            new { sessionId = session.Id, prompt = "Ergänze jetzt die mathematischen Grenzfälle ausführlich." }));

        Assert.Equal(title, (await chats.GetSessionAsync(session.Id))!.Title);
    }

    [Fact]
    public async Task SessionActionsUseTheNewDefaultTitleAndUpdateRecentActivity()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recentActivity = CreateRecentActivity(settings);
        var coordinator = CreateCoordinator(environment, settings, recentActivity);

        await HandleAsync(coordinator, "session.create", new { });

        var sessionId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var session = await environment.Get<IChatRepository>().GetSessionAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal("Neue Sitzung", session.Title);
        Assert.Equal("AI-Sitzung „Neue Sitzung“ erstellt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.rename", new { sessionId, title = "Planung" });
        Assert.Equal("AI-Sitzung in „Planung“ umbenannt", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.open", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ geöffnet", settings.Current.LastActivityText);

        await HandleAsync(coordinator, "session.delete", new { sessionId });
        Assert.Equal("AI-Sitzung „Planung“ gelöscht", settings.Current.LastActivityText);
        var replacementId = Assert.IsType<Guid>(settings.Current.ActiveSessionId);
        var replacement = await environment.Get<IChatRepository>().GetSessionAsync(replacementId);
        Assert.NotNull(replacement);
        Assert.Equal("Neue Sitzung", replacement.Title);
    }

    [Fact]
    public async Task BulkSessionDeletionOnlyDeletesSessionsInTheSelectedMode()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var coding = await chats.CreateSessionAsync("Coding-Sitzung", ChatMode.Coding);
        var codingMessage = await chats.AddMessageAsync(
            coding.Id,
            ChatRole.Assistant,
            "Dieser Inhalt muss erhalten bleiben.",
            MessageStatus.Completed);
        var general = await chats.CreateSessionAsync("General-Sitzung", ChatMode.General);
        await chats.AddMessageAsync(
            general.Id,
            ChatRole.Assistant,
            "Dieser Inhalt darf gesammelt gelöscht werden.",
            MessageStatus.Completed);
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = ChatMode.General,
            ActiveGeneralSessionId = general.Id,
            ActiveCodingSessionId = coding.Id,
            ActiveSessionId = general.Id,
        });
        var coordinator = CreateCoordinator(
            environment,
            settings,
            CreateRecentActivity(settings));

        await HandleAsync(coordinator, "session.clear", new { });

        Assert.Equal(coding.Id, Assert.Single(await chats.ListSessionsAsync(ChatMode.Coding)).Id);
        var replacement = Assert.Single(await chats.ListSessionsAsync(ChatMode.General));
        Assert.Equal(replacement.Id, settings.Current.ActiveSessionId);
        Assert.Equal(replacement.Id, settings.Current.ActiveGeneralSessionId);
        Assert.Equal(coding.Id, settings.Current.ActiveCodingSessionId);
        Assert.NotNull(await chats.GetMessageAsync(codingMessage.Id));
        Assert.Null(await chats.GetSessionAsync(general.Id));
        Assert.Equal(
            "Alle AI-Sitzungen der Ansicht „General“ gelöscht",
            settings.Current.LastActivityText);
    }

    [Fact]
    public async Task BulkSessionDeletionUsesTheConfirmedModeAfterTheViewChanged()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var general = await chats.CreateSessionAsync("General löschen", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Coding behalten", ChatMode.Coding);
        await settings.UpdateAsync(current => current with
        {
            // Simulate a mode switch completing after the General confirmation
            // dialog opened but before its session.clear request is handled.
            SelectedChatMode = ChatMode.Coding,
            ActiveGeneralSessionId = general.Id,
            ActiveCodingSessionId = coding.Id,
            ActiveSessionId = coding.Id,
        });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await HandleAsync(coordinator, "session.clear", new { chatMode = "general" });

        Assert.Empty(await chats.ListSessionsAsync(ChatMode.General));
        Assert.Equal(coding.Id, Assert.Single(await chats.ListSessionsAsync(ChatMode.Coding)).Id);
        Assert.Equal(ChatMode.Coding, settings.Current.SelectedChatMode);
        Assert.Null(settings.Current.ActiveGeneralSessionId);
        Assert.Equal(coding.Id, settings.Current.ActiveCodingSessionId);
        Assert.Equal(coding.Id, settings.Current.ActiveSessionId);
        Assert.Equal(
            "Alle AI-Sitzungen der Ansicht „General“ gelöscht",
            settings.Current.LastActivityText);
    }

    [Fact]
    public async Task ModeSwitchSnapshotsContainOnlyModeOwnedSessionsProjectsAndWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var generalWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "General Workspace")).FullName;
        var codingWorkspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "Coding Workspace")).FullName;
        var general = await chats.CreateSessionAsync("General-Sitzung", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Coding-Sitzung", ChatMode.Coding);
        await chats.SetCodingWorkspacePathAsync(general.Id, generalWorkspace, activateCoding: false);
        await chats.SetCodingWorkspacePathAsync(coding.Id, codingWorkspace, activateCoding: true);
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = ChatMode.General,
            ActiveGeneralSessionId = general.Id,
            ActiveCodingSessionId = coding.Id,
            ActiveSessionId = general.Id,
        });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var generalSnapshot = JsonSerializer.SerializeToElement(
            await coordinator.BuildSnapshotAsync(),
            JsonSerializerOptions.Web);
        Assert.Equal("general", generalSnapshot.GetProperty("chatMode").GetString());
        Assert.Equal(general.Id, Assert.Single(generalSnapshot.GetProperty("sessions").EnumerateArray()).GetProperty("id").GetGuid());
        var generalGroup = Assert.Single(generalSnapshot.GetProperty("sessionGroups").EnumerateArray());
        Assert.Equal("general", generalGroup.GetProperty("chatMode").GetString());
        Assert.Equal(generalWorkspace, generalSnapshot.GetProperty("workspacePath").GetString());

        await HandleAsync(coordinator, "mode.switch", new { chatMode = "coding" });
        var codingSnapshot = JsonSerializer.SerializeToElement(
            await coordinator.BuildSnapshotAsync(),
            JsonSerializerOptions.Web);
        Assert.Equal("coding", codingSnapshot.GetProperty("chatMode").GetString());
        Assert.Equal(coding.Id, Assert.Single(codingSnapshot.GetProperty("sessions").EnumerateArray()).GetProperty("id").GetGuid());
        var codingGroup = Assert.Single(codingSnapshot.GetProperty("sessionGroups").EnumerateArray());
        Assert.Equal("coding", codingGroup.GetProperty("chatMode").GetString());
        Assert.Equal(codingWorkspace, codingSnapshot.GetProperty("workspacePath").GetString());
        Assert.NotEqual(generalGroup.GetProperty("id").GetGuid(), codingGroup.GetProperty("id").GetGuid());
        Assert.Equal(general.Id, settings.Current.ActiveGeneralSessionId);
        Assert.Equal(coding.Id, settings.Current.ActiveCodingSessionId);
    }

    private static RecentActivityService CreateRecentActivity(SettingsCoordinator settings)
    {
        var service = new RecentActivityService(
            settings,
            new ShellViewModel(),
            NullLogger<RecentActivityService>.Instance);
        service.Restore();
        return service;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Das Missum-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }

    private static AssistantCoordinator CreateCoordinator(
        TestEnvironment environment,
        SettingsCoordinator settings,
        RecentActivityService recentActivity,
        IProjectMemoryStore? projectMemory = null) => new(
            environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(),
            null,
            settings,
            recentActivity,
            projectMemory: projectMemory);

    private static async Task HandleAsync(
        AssistantCoordinator coordinator,
        string type,
        object payload)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            type,
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        await coordinator.HandleAsync(envelope, static (_, _, _) => Task.CompletedTask);
    }

    private static async Task<JsonElement> HandleAndCaptureAsync(
        AssistantCoordinator coordinator,
        string requestType,
        object payload,
        string responseType)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            requestType,
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        JsonElement? captured = null;
        await coordinator.HandleAsync(
            envelope,
            (type, responsePayload, _) =>
            {
                if (type == responseType)
                {
                    captured = JsonSerializer.SerializeToElement(responsePayload, JsonSerializerOptions.Web);
                }
                return Task.CompletedTask;
            });
        return captured ?? throw new InvalidOperationException($"{responseType} wurde nicht emittiert.");
    }

    private static async Task<JsonElement> OpenAndCaptureSnapshotAsync(
        AssistantCoordinator coordinator,
        Guid sessionId)
    {
        using var payloadDocument = JsonDocument.Parse(JsonSerializer.Serialize(new { sessionId }));
        var envelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "session.open",
            Guid.NewGuid().ToString("D"),
            payloadDocument.RootElement.Clone());
        JsonElement? snapshot = null;
        await coordinator.HandleAsync(
            envelope,
            (type, payload, _) =>
            {
                if (type is "state.snapshot" or "session.changed")
                {
                    snapshot = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                }
                return Task.CompletedTask;
            });
        return snapshot ?? throw new InvalidOperationException("Der Sitzungs-Snapshot wurde nicht emittiert.");
    }

    [Fact]
    public async Task SessionGroupingIsTriggeredOnlyManuallyNotAfterCompletedRun()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        await HandleAsync(coordinator, "session.create", new { title = "Gruppe A" });
        await HandleAsync(coordinator, "session.create", new { title = "Gruppe B" });

        await coordinator.EmitMissumAiUpdateAsync(
            new MissumAiAssistantUpdate(MissumAiAssistantUpdateKind.Completed, new ChatMessage(Guid.NewGuid(), Guid.NewGuid(), ChatRole.Assistant, "Antwort", MessageStatus.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Status: "Abgeschlossen"),
            static (_, _, _) => Task.CompletedTask,
            Guid.NewGuid().ToString("D"));

        await using (var assertionConnection = new SqliteConnection($"Data Source={environment.Get<IMissumDatabase>().DatabasePath}"))
        {
            await assertionConnection.OpenAsync();
            await using var groupCommand = assertionConnection.CreateCommand();
            groupCommand.CommandText = "SELECT COUNT(*) FROM chat_session_groups;";
            Assert.Equal(0L, (long)(await groupCommand.ExecuteScalarAsync() ?? -1L));
            await using var assignmentCommand = assertionConnection.CreateCommand();
            assignmentCommand.CommandText = "SELECT COUNT(*) FROM chat_sessions WHERE session_group_id IS NOT NULL;";
            Assert.Equal(0L, (long)(await assignmentCommand.ExecuteScalarAsync() ?? -1L));
        }

        var groupNowEnvelope = new WebBridgeEnvelope(
            AssistantWebBridge.ProtocolVersion,
            "session.groupNow",
            Guid.NewGuid().ToString("D"),
            JsonDocument.Parse("{}").RootElement.Clone());
        var groupNowException = await Record.ExceptionAsync(() => coordinator.HandleAsync(
            groupNowEnvelope,
            (_, _, _) => Task.CompletedTask));
        Assert.Null(groupNowException);
    }

    [Fact]
    public async Task SessionGroupingSnapshotIncludesActualMembershipForSidebar()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Anfang");
        var second = await chats.CreateSessionAsync("Fortsetzung");
        await chats.ApplySessionGroupingAsync([new(null, "Projekt", [first.Id, second.Id])]);
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));
        var snapshot = await OpenAndCaptureSnapshotAsync(coordinator, first.Id);
        var group = Assert.Single(snapshot.GetProperty("sessionGroups").EnumerateArray());
        Assert.Equal("Projekt", group.GetProperty("name").GetString());
        var members = group.GetProperty("sessionIds").EnumerateArray().Select(item => item.GetGuid()).ToHashSet();
        Assert.Equal(2, members.Count);
        Assert.Contains(first.Id, members);
        Assert.Contains(second.Id, members);
        Assert.All(snapshot.GetProperty("sessions").EnumerateArray(),
            session => Assert.Equal(group.GetProperty("id").GetGuid(), session.GetProperty("sessionGroupId").GetGuid()));
    }

    [Fact]
    public async Task SessionGroupingRefreshIsSidebarOnlyAfterConcurrentSessionSwitch()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Ursprüngliche Sitzung");
        var second = await chats.CreateSessionAsync("Inzwischen aktive Sitzung");
        await chats.SaveDraftAsync(second.Id, "Noch nicht gesendeter Entwurf");
        await chats.ApplySessionGroupingAsync([new(null, "Projekt", [first.Id, second.Id])]);
        await settings.UpdateAsync(current => current with { ActiveSessionId = second.Id });
        var coordinator = CreateCoordinator(environment, settings, CreateRecentActivity(settings));

        var completion = JsonSerializer.SerializeToElement(
            await coordinator.BuildSessionSidebarSnapshotAsync(true), JsonSerializerOptions.Web);
        Assert.Equal("groupingCompleted,productName,assistantTitle,productIdentity,chatMode,selectedChatMode,sessions,sessionGroups",
            string.Join(",", completion.EnumerateObject().Select(property => property.Name)));
        Assert.True(completion.GetProperty("groupingCompleted").GetBoolean());
        Assert.True(AssistantWebBridge.IsOutgoingTypeAllowed("session.grouped"));

        var groupId = Assert.Single(await chats.ListSessionGroupsAsync()).Id;
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(new { groupId, collapsed = true }));
        var events = new List<(string Type, JsonElement Data)>();
        await coordinator.HandleAsync(new(AssistantWebBridge.ProtocolVersion, "session.groupCollapse", "group-collapse", payload.RootElement.Clone()),
            (type, data, _) =>
            {
                events.Add((type, JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web)));
                return Task.CompletedTask;
            });
        var item = Assert.Single(events);
        Assert.Equal("session.grouped", item.Type);
        Assert.False(item.Data.GetProperty("groupingCompleted").GetBoolean());
        Assert.Equal("groupingCompleted,productName,assistantTitle,productIdentity,chatMode,selectedChatMode,sessions,sessionGroups",
            string.Join(",", item.Data.EnumerateObject().Select(property => property.Name)));
        Assert.Equal(second.Id, settings.Current.ActiveSessionId);
        Assert.Equal("Noch nicht gesendeter Entwurf", (await chats.GetSessionAsync(second.Id))!.Draft);
    }

}

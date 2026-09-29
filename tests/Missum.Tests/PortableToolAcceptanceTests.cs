using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>
/// Shared, fail-closed configuration for the opt-in portable tool acceptance.
/// Ordinary developer live tests retain their historical defaults; the portable
/// gate explicitly selects DeepSeek and disables selectable reasoning.
/// </summary>
internal static class PortableToolAcceptance
{
    public const string EnabledVariable = "MISSUM_AI_PORTABLE_TOOL_ACCEPTANCE";
    public const string ReasoningVariable = "MISSUM_AI_LIVE_REASONING_EFFORT";

    public static bool Enabled => Environment.GetEnvironmentVariable(EnabledVariable) == "1";

    public static string ReasoningEffort(string fallback = "medium") =>
        Environment.GetEnvironmentVariable(ReasoningVariable)?.Trim().ToLowerInvariant() is { Length: > 0 } value
            ? value
            : fallback;

    public static Dictionary<string, string> ReasoningSelections(params (string ModelId, string Role)[] models)
    {
        var effort = ReasoningEffort();
        return models
            .Where(static item => !string.IsNullOrWhiteSpace(item.ModelId) && !string.IsNullOrWhiteSpace(item.Role))
            .ToDictionary(
                static item => MissumAiAssistantService.ReasoningKey(item.ModelId, item.Role),
                _ => effort,
                StringComparer.OrdinalIgnoreCase);
    }

    public static void AssertDeepSeekReasoningOff(string modelId)
    {
        if (!Enabled) return;
        Assert.Contains("deepseek", modelId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("none", ReasoningEffort());
    }

    public static IReadOnlyDictionary<string, string> BuiltInCoverage { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BuiltInActionIds.AttachFilesAndFolders] = "native-tool-contracts",
            [BuiltInActionIds.PlanMode] = "translation-and-plan-mode",
            [BuiltInActionIds.WebSearch] = "gateway-server-media-speech",
            [BuiltInActionIds.DeepResearch] = "claude-science-deep-research",
            [BuiltInActionIds.ImageAnalysis] = "gateway-server-media-speech+workspace-image-input",
            [BuiltInActionIds.AudioAnalysis] = "gateway-server-media-speech",
            [BuiltInActionIds.VideoAnalysis] = "gateway-server-media-speech",
            [BuiltInActionIds.CreateDocument] = "documents-and-audiobook",
            [BuiltInActionIds.GenerateImage] = "gateway-server-media-speech",
            [BuiltInActionIds.CreateAudiobook] = "documents-and-audiobook",
            [BuiltInActionIds.ExportChatPdf] = "native-tool-contracts",
            [BuiltInActionIds.Translate] = "translation-and-plan-mode",
            [BuiltInActionIds.ReadAloud] = "documents-and-audiobook+gateway-server-media-speech",
            [BuiltInActionIds.LiveCaptions] = "gateway-server-media-speech",
        };
}

public sealed class PortableToolAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryBuiltInActionHasAnExplicitPortableAcceptancePath()
    {
        var catalogIds = BuiltInExtensionCatalog.Actions.Select(static item => item.ActionId).Order(StringComparer.Ordinal);
        var coveredIds = PortableToolAcceptance.BuiltInCoverage.Keys.Order(StringComparer.Ordinal);
        Assert.Equal(catalogIds, coveredIds);
        Assert.All(PortableToolAcceptance.BuiltInCoverage, static item => Assert.False(string.IsNullOrWhiteSpace(item.Value)));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task InstalledDeepSeekProfilesExposeAndAcceptExplicitReasoningOff()
    {
        if (!PortableToolAcceptance.Enabled) return;

        var serverUrl = (Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL")
            ?? "http://127.0.0.1:8080").TrimEnd('/') + "/";
        var generalId = Required("MISSUM_AI_LIVE_GENERAL_MODEL");
        var codingId = Required("MISSUM_AI_LIVE_CODING_MODEL");
        var visionId = Required("MISSUM_AI_NATIVE_VISION_MODEL");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(generalId);
        PortableToolAcceptance.AssertDeepSeekReasoningOff(codingId);
        PortableToolAcceptance.AssertDeepSeekReasoningOff(visionId);

        using var http = new HttpClient { BaseAddress = new Uri(serverUrl), Timeout = TimeSpan.FromSeconds(30) };
        using var client = new MissumAiClient(http, "missum-portable-tool-preflight-" + Guid.NewGuid().ToString("N"));
        var status = await client.GetModelStatusAsync();
        Assert.True(status.ProviderReachable, status.ErrorMessage);

        AssertProfile(generalId, "general", reasoningOffRequired: true);
        AssertProfile(codingId, "coding", reasoningOffRequired: true);
        AssertProfile(visionId, "vision", reasoningOffRequired: true);
        output.WriteLine($"DeepSeek portable tool preflight: general={generalId}; coding={codingId}; vision={visionId}; reasoning=none");

        void AssertProfile(string id, string role, bool reasoningOffRequired)
        {
            var model = Assert.Single(status.Models, item => item.Id == id && item.Role == role);
            Assert.True(model.Downloaded, $"{role} profile '{id}' is not downloaded.");
            Assert.True(model.SupportsTools, $"{role} profile '{id}' does not support tools.");
            if (role == "vision") Assert.True(model.SupportsVision, $"Vision profile '{id}' does not support vision.");
            if (reasoningOffRequired)
            {
                Assert.Contains("none", model.ReasoningEfforts ?? [], StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task NativeWindowTargetsProduceARealScreenshotAndScreenClip()
    {
        if (!PortableToolAcceptance.Enabled) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        var screenshots = new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance);
        var targets = screenshots.ListTargets();
        Assert.Contains(targets, static item => item.Kind == DesktopCaptureTargetKind.VirtualDesktop);
        var windowTargets = targets.Where(static item => item.Kind == DesktopCaptureTargetKind.Window).ToArray();
        Assert.NotEmpty(windowTargets);
        var portableProcessValue = Required("MISSUM_AI_PORTABLE_PROCESS_ID");
        Assert.True(int.TryParse(portableProcessValue, out var portableProcessId) && portableProcessId > 0,
            $"MISSUM_AI_PORTABLE_PROCESS_ID is invalid: '{portableProcessValue}'.");
        var target = Assert.Single(windowTargets, item => item.ProcessId == portableProcessId);
        Assert.Contains("Missum", target.DisplayName, StringComparison.OrdinalIgnoreCase);

        var screenshot = await screenshots.CaptureAsync(target, timeout.Token);
        Assert.Equal("image/png", screenshot.ContentType);
        Assert.True(screenshot.Width > 0 && screenshot.Height > 0);
        Assert.True(screenshot.Content.Length > 8);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, screenshot.Content[..8]);

        var evidenceRoot = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY")?.Trim();
        string? screenshotEvidencePath = null;
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            var captureEvidence = Path.Combine(evidenceRoot, "native-windows-capture");
            Directory.CreateDirectory(captureEvidence);
            screenshotEvidencePath = Path.Combine(captureEvidence, "window.png");
            await File.WriteAllBytesAsync(screenshotEvidencePath, screenshot.Content, timeout.Token);
        }
        screenshotEvidencePath ??= Path.Combine(Path.GetTempPath(), "missum-window-" + Guid.NewGuid().ToString("N") + ".png");
        if (!File.Exists(screenshotEvidencePath))
            await File.WriteAllBytesAsync(screenshotEvidencePath, screenshot.Content, timeout.Token);
        var screenshotPath = screenshotEvidencePath!;

        using var clips = new ScreenClipCaptureService(NullLogger<ScreenClipCaptureService>.Instance);
        ScreenClipResult? clip = null;
        string? clipEvidencePath = null;
        try
        {
            await clips.StartAsync(Guid.NewGuid(), target, timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(1_250), timeout.Token);
            clip = await clips.StopAsync(timeout.Token);
            Assert.Equal("video/mp4", clip.ContentType);
            Assert.True(clip.Frames >= 2);
            Assert.True(File.Exists(clip.Path));
            Assert.True(new FileInfo(clip.Path).Length > 1_024);
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                var captureEvidence = Path.Combine(evidenceRoot, "native-windows-capture");
                clipEvidencePath = Path.Combine(captureEvidence, "window.mp4");
                File.Copy(clip.Path, clipEvidencePath, overwrite: true);
            }
            output.WriteLine($"Native capture acceptance: targets={targets.Count}; process={target.ProcessId}; source={target.DisplayName}; screenshot={screenshot.Content.Length} bytes ({screenshotEvidencePath ?? "temporary"}); clip={clip.Frames} frames/{new FileInfo(clip.Path).Length} bytes ({clipEvidencePath ?? "temporary"})");

            var serverUrl = (Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL")
                ?? "http://127.0.0.1:8080").TrimEnd('/') + "/";
            var visionModel = Required("MISSUM_AI_NATIVE_VISION_MODEL");
            var reasoning = PortableToolAcceptance.ReasoningEffort();
            PortableToolAcceptance.AssertDeepSeekReasoningOff(visionModel);
            using var http = new HttpClient { BaseAddress = new Uri(serverUrl), Timeout = Timeout.InfiniteTimeSpan };
            using var client = new MissumAiClient(http, "missum-native-capture-" + Guid.NewGuid().ToString("N"));
            var screenshotResult = await AnalyzeCapturedMediaAsync(
                client,
                screenshotPath,
                "image/png",
                "Beschreibe knapp die tatsächlich sichtbare Anwendungsoberfläche und erfinde keine verdeckten Inhalte.",
                visionModel,
                reasoning,
                timeout.Token);
            var clipResult = await AnalyzeCapturedMediaAsync(
                client,
                clip.Path,
                "video/mp4",
                "Beschreibe knapp die sichtbare Oberfläche und Änderungen im Bildschirmclip mit Zeitangaben.",
                visionModel,
                reasoning,
                timeout.Token);
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                await File.WriteAllTextAsync(
                    Path.Combine(evidenceRoot, "native-windows-capture", "analysis.json"),
                    JsonSerializer.Serialize(new { target = target.DisplayName, screenshotResult, clipResult }, MissumAiProtocol.CreateJsonOptions()),
                    timeout.Token);
            }
        }
        finally
        {
            if (clip is not null)
            {
                try { File.Delete(clip.Path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (string.IsNullOrWhiteSpace(evidenceRoot) && screenshotEvidencePath is not null)
            {
                try { File.Delete(screenshotEvidencePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<JsonElement> AnalyzeCapturedMediaAsync(
        MissumAiClient client,
        string path,
        string mediaType,
        string prompt,
        string modelId,
        string reasoningEffort,
        CancellationToken cancellationToken)
    {
        var upload = await client.UploadFileAsync(path, mediaType, cancellationToken: cancellationToken);
        var accepted = await client.AnalyzeMediaAsync(
            new MediaJobRequest(upload.UploadId, prompt, PreferredModelId: modelId, ReasoningEffort: reasoningEffort),
            "native-capture-analysis-" + Guid.NewGuid().ToString("N"),
            cancellationToken);
        var events = new List<RunEvent>();
        long cursor = 0;
        var terminal = false;
        try
        {
            for (var reconnect = 0; reconnect < 12 && !terminal; reconnect++)
            {
                await foreach (var item in client.StreamRunEventsAsync(accepted.RunId, cursor, cancellationToken))
                {
                    Assert.True(item.Id > cursor);
                    cursor = item.Id;
                    events.Add(item);
                    if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled)
                    {
                        terminal = true;
                        break;
                    }
                }
            }
            var snapshot = await client.GetRunAsync(accepted.RunId, cancellationToken);
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (terminal
                && snapshot.State is RunState.Running or RunState.Queued
                && settle.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100, cancellationToken);
                snapshot = await client.GetRunAsync(accepted.RunId, cancellationToken);
            }
            Assert.True(terminal, $"Native capture analysis {accepted.RunId} did not reach a terminal event.");
            Assert.True(snapshot.State == RunState.Completed,
                $"Native capture analysis {accepted.RunId}: {snapshot.State}; {snapshot.ErrorCode}");
            var completed = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == "media.analyze");
            var result = completed.Data.GetProperty("result").Clone();
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("analysis").GetString()));
            Assert.Equal(reasoningEffort, result.GetProperty("reasoningEffort").GetString());
            if (result.TryGetProperty("visionReasoningEffort", out var visionEffort))
                Assert.Equal(reasoningEffort, visionEffort.GetString());
            return result;
        }
        finally
        {
            if (!terminal)
            {
                try { await client.CancelRunAsync(accepted.RunId, CancellationToken.None); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }
        }
    }

    private static string Required(string variable) =>
        Environment.GetEnvironmentVariable(variable)?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} must be set by windows/test-portable-tools.ps1.");
}

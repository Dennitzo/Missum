using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Real file-only WorkspaceToolService upload and gateway vision analysis; no desktop/window operations.</summary>
public sealed class WorkspaceImageInputLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    // Same deterministic 128x128 red-square PNG used by NativeRoleLiveTests and GatewayToolsLiveTests.
    private const string TestPng = "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAABWklEQVR4nO3OQQ0AMBAEofVv+iqDxzRBALvtg/wgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vzg2h4gaMOyHY2XLAAAAABJRU5ErkJggg==";

    [Fact]
    [Trait("Category", "Live")]
    public async Task ImageInputFileUploadsExactBytesAndProducesPersistedVisionResult()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_WORKSPACE_IMAGE_LIVE") != "1") return;
        var modelId = Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_VISION_MODEL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(modelId), "MISSUM_AI_NATIVE_VISION_MODEL must name an installed vision model.");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(modelId!);
        var gateway = new Uri((Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        var startedAt = DateTimeOffset.UtcNow;
        await using var environment = await TestEnvironment.CreateAsync();
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        var imagePath = Path.Combine(workspace, "known-red-square.png");
        var original = Convert.FromBase64String(TestPng);
        await File.WriteAllBytesAsync(imagePath, original, token);
        var originalSha256 = Hash(original);

        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with { MissumAiServerUrl = gateway.AbsoluteUri });
        var observedManifests = new ConcurrentQueue<UploadManifest>();
        var observedReceipts = new ConcurrentQueue<UploadCompleted>();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new UploadObservationHandler(observedManifests, observedReceipts));
        using var client = await connection.CreateClientAsync(gateway, ensureProfileNativeRuntime: false, token);
        var status = await client.GetModelStatusAsync(token);
        Assert.Contains(status.Models, model => model.Id == modelId && model.Role == "vision" && model.Downloaded);
        var tool = new WorkspaceToolService(connection);
        var proposal = new ToolProposal("workspace-image-file", "workspace-image-acceptance", WorkspaceTools.ImageInput,
            JsonSerializer.SerializeToElement(new { operation = "file", path = Path.GetFileName(imagePath) }),
            ToolRiskClass.ReadOnly, "Lokale PNG-Datei für die Sichtprüfung laden", DateTimeOffset.UtcNow.AddMinutes(5));
        string? uploadId = null;
        string? runId = null;
        var terminal = false;
        var runFinished = false;
        var events = new List<RunEvent>();
        var downloads = new List<(ArtifactDescriptor Artifact, byte[] Bytes)>();
        try
        {
            var result = JsonSerializer.SerializeToElement(await tool.ExecuteAsync(proposal, workspace, null, token, gateway), Json);
            uploadId = result.GetProperty("uploadId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(uploadId));
            Assert.Equal("file", result.GetProperty("source").GetString());
            Assert.Equal("image/png", result.GetProperty("mediaType").GetString());
            Assert.Contains("noch nicht analysiert", result.GetProperty("instruction").GetString(), StringComparison.Ordinal);
            Assert.Equal(original, await File.ReadAllBytesAsync(imagePath, token));
            var manifest = Assert.Single(observedManifests);
            var completedUpload = Assert.Single(observedReceipts);
            Assert.Equal(Path.GetFileName(imagePath), manifest.FileName);
            Assert.Equal("image/png", manifest.MediaType);
            Assert.Equal(original.LongLength, manifest.Length);
            Assert.Equal(originalSha256, manifest.Sha256);
            Assert.Equal(uploadId, completedUpload.UploadId);
            Assert.Equal(originalSha256, completedUpload.Sha256);
            Assert.Equal(original.LongLength, completedUpload.Length);
            Assert.Equal("image/png", completedUpload.MediaType);
            var upload = await client.GetUploadAsync(uploadId!, token);
            Assert.Equal(uploadId, upload.UploadId);
            Assert.True(upload.ChunkCount > 0);
            Assert.Equal(upload.ChunkCount, upload.ReceivedChunks.Count);
            output.WriteLine(JsonSerializer.Serialize(new { toolResult = result, manifest, completedUpload, originalSha256 }, Json));

            var accepted = await client.AnalyzeMediaAsync(new(uploadId!,
                "Welche Farbe hat das große Quadrat in der Bildmitte? Antworte nur mit dem deutschen Farbnamen.",
                PreferredModelId: modelId,
                ReasoningEffort: PortableToolAcceptance.ReasoningEffort()), "workspace-image-analysis-" + Guid.NewGuid().ToString("N"), token);
            runId = accepted.RunId;
            output.WriteLine($"image.input=file; sourceSha256={originalSha256}; uploadId={uploadId}; runId={runId}; vision={modelId}");
            long cursor = 0;
            for (var attempt = 0; !terminal && attempt < 8; attempt++)
            {
                await foreach (var item in client.StreamRunEventsAsync(runId, cursor, token))
                {
                    Assert.Equal(runId, item.RunId);
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
            var run = await client.GetRunAsync(runId, token);
            var settle = Stopwatch.StartNew();
            while (terminal && (run.State is RunState.Running or RunState.Queued) && settle.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100, token);
                run = await client.GetRunAsync(runId, token);
            }
            runFinished = run.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
            Assert.True(terminal, "Vision SSE did not reach a terminal event.");
            Assert.True(run.State == RunState.Completed, $"Vision run {runId}: {run.State}; {run.ErrorCode}");
            Assert.Contains(events, item => item.Type == RunEventTypes.RunCompleted);
            // Decoded media frames are internal; the public analysis is the persisted tool result.
            var completed = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("tool", out var toolName) && toolName.GetString() == "media.analyze");
            var mediaResult = completed.Data.GetProperty("result");
            Assert.Equal("image", mediaResult.GetProperty("kind").GetString());
            Assert.Equal("frames + vision", mediaResult.GetProperty("metadata").GetProperty("pipeline").GetString());
            Assert.Equal(modelId, mediaResult.GetProperty("visionModelId").GetString());
            Assert.Equal(PortableToolAcceptance.ReasoningEffort(), mediaResult.GetProperty("visionReasoningEffort").GetString());
            Assert.Equal(PortableToolAcceptance.ReasoningEffort(), mediaResult.GetProperty("reasoningEffort").GetString());
            var analysis = mediaResult.GetProperty("analysis").GetString();
            Assert.False(string.IsNullOrWhiteSpace(analysis));
            Assert.Contains("rot", analysis, StringComparison.OrdinalIgnoreCase);
            var replay = new List<RunEvent>();
            await foreach (var item in client.StreamRunEventsAsync(runId, 0, token))
            {
                replay.Add(item);
                if (item.Id == events[^1].Id) break;
            }
            Assert.Equal(JsonSerializer.Serialize(events, Json), JsonSerializer.Serialize(replay, Json));
            var artifacts = events.Where(item => item.Type == RunEventTypes.ArtifactCreated)
                .Select(item => item.Data.Deserialize<ArtifactDescriptor>(Json)!).ToArray();
            foreach (var artifact in artifacts)
            {
                var destination = Path.Combine(workspace, "analysis-" + Guid.NewGuid().ToString("N") + ".bin");
                await client.DownloadArtifactAsync(artifact.ArtifactId, destination, cancellationToken: token);
                var bytes = await File.ReadAllBytesAsync(destination, token);
                Assert.Equal(artifact.Length, bytes.LongLength);
                Assert.Equal(artifact.Sha256.ToLowerInvariant(), Hash(bytes));
                downloads.Add((artifact, bytes));
            }
            Assert.Equal(originalSha256, Hash(await File.ReadAllBytesAsync(imagePath, token)));
            var receipt = new
            {
                startedAt, completedAt = DateTimeOffset.UtcNow, operation = "file", gateway, modelId,
                original = new { fileName = Path.GetFileName(imagePath), sha256 = originalSha256, length = original.LongLength },
                toolResult = result, manifest, completedUpload, upload, run, events,
                artifacts = downloads.Select(item => item.Artifact), analysis,
            };
            var receiptJson = JsonSerializer.Serialize(receipt, Json);
            output.WriteLine(receiptJson);
            var evidenceRoot = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY")
                ?? Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidenceRoot))
            {
                var directory = Path.Combine(Path.GetFullPath(evidenceRoot), "workspace-image-file-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), receiptJson, token);
                await File.WriteAllBytesAsync(Path.Combine(directory, "original.png"), original, token);
                for (var index = 0; index < downloads.Count; index++)
                    await File.WriteAllBytesAsync(Path.Combine(directory, $"analysis-{index}.bin"), downloads[index].Bytes, token);
                output.WriteLine("Retained evidence: " + directory);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (!runFinished && runId is not null)
            {
                try { await client.CancelRunAsync(runId, cleanup.Token); }
                catch (Exception exception) { output.WriteLine("Test-run cleanup: " + exception.Message); }
            }
            uploadId ??= observedReceipts.LastOrDefault()?.UploadId;
            if (uploadId is not null)
            {
                try { await client.DeleteUploadAsync(uploadId, cleanup.Token); }
                catch (Exception exception) { output.WriteLine("Test-upload cleanup: " + exception.Message); }
            }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Observes actual HTTP bodies while forwarding every request to the real gateway.</summary>
    private sealed class UploadObservationHandler(ConcurrentQueue<UploadManifest> manifests,
        ConcurrentQueue<UploadCompleted> receipts) : DelegatingHandler(new HttpClientHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (request.Method == HttpMethod.Post && path.EndsWith("/v1/uploads", StringComparison.Ordinal))
            {
                var manifest = JsonSerializer.Deserialize<UploadManifest>(await request.Content!.ReadAsStringAsync(cancellationToken), Json);
                if (manifest is not null) manifests.Enqueue(manifest);
            }
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode && request.Method == HttpMethod.Post
                && path.Contains("/v1/uploads/", StringComparison.Ordinal) && path.EndsWith("/complete", StringComparison.Ordinal))
            {
                var receipt = JsonSerializer.Deserialize<UploadCompleted>(await response.Content.ReadAsStringAsync(cancellationToken), Json);
                if (receipt is not null) receipts.Enqueue(receipt);
            }
            return response;
        }
    }
}

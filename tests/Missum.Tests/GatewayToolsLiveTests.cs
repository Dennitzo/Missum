using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>Opt-in acceptance with real workers, uploads, generated files and persisted SSE events.</summary>
public sealed class GatewayToolsLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private const string RedSquare = "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAABWklEQVR4nO3OQQ0AMBAEofVv+iqDxzRBALvtg/wgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vwgzg/i/CDOD+L8IM4P4vzg2h4gaMOyHY2XLAAAAABJRU5ErkJggg==";

    [Fact]
    [Trait("Category", "Live")]
    public async Task SpeechCaptionsMediaImagesAndResearchProduceVerifiedArtifacts()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_AI_GATEWAY_TOOLS_LIVE") != "1") return;
        var generalModelId = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL")?.Trim();
        Assert.False(string.IsNullOrWhiteSpace(generalModelId), "MISSUM_AI_LIVE_GENERAL_MODEL must select an installed tool-capable model.");
        PortableToolAcceptance.AssertDeepSeekReasoningOff(generalModelId!);
        var requestedReasoning = PortableToolAcceptance.ReasoningEffort();
        var root = Path.Combine(Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE_DIRECTORY")
            ?? Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_EVIDENCE") ?? Path.GetTempPath(),
            "gateway-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var ct = deadline.Token;
        using var http = new HttpClient
        {
            BaseAddress = new Uri((Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL") ?? "http://127.0.0.1:8080").TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new MissumAiClient(http, "missum-tool-acceptance-" + Guid.NewGuid().ToString("N"));
        output.WriteLine("Evidence: " + root);
        var receipts = new List<object>();
        var failures = new List<string>();
        var completedEvents = new Dictionary<string, List<RunEvent>>(StringComparer.Ordinal);
        async Task Check(string name, Func<Task> action)
        {
            output.WriteLine("Starting " + name);
            var clock = Stopwatch.StartNew();
            var failureCount = failures.Count;
            try
            {
                await action();
                Assert.Equal(failureCount, failures.Count);
                receipts.Add(new { name, passed = true, seconds = clock.Elapsed.TotalSeconds });
            }
            catch (Exception ex)
            {
                failures.Add(name + ": " + ex.Message);
                receipts.Add(new { name, passed = false, seconds = clock.Elapsed.TotalSeconds, error = ex.ToString() });
                output.WriteLine("FAILED " + name + ": " + ex);
                if (ct.IsCancellationRequested) throw;
            }
            finally
            {
                // Preserve the failing step even when the overall deadline cancelled it.
                await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(receipts, Json), CancellationToken.None);
            }
        }

        await Check("local-searxng-and-fetch", async () =>
        {
            var search = await client.SearchWebAsync(new("SQLite write ahead logging official documentation", 5, "en-US"), ct);
            Assert.Equal("searxng", search.Provider);
            Assert.False(search.IsFallback);
            Assert.NotEmpty(search.Results);
            var source = await client.FetchWebAsync(new("https://www.sqlite.org/wal.html"), ct);
            Assert.True(source.IsUntrusted);
            Assert.Contains("WAL", source.Content, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(Path.Combine(root, "research.json"), JsonSerializer.Serialize(new { search, source }, Json), ct);
        });

        await Check("model-invokes-real-math-tool", async () =>
        {
            var accepted = await client.CreateRunAsync(new(MissumAiProtocol.Version, RunMode.General,
                [new("user", [new("text", "Rufe genau einmal math.evaluate mit operation=add, left=[2], right=[3] auf. Antworte danach nur mit dem berechneten Ergebnis.")])],
                AllowedServerTools: ["math.evaluate"], PreferredGeneralModelId: generalModelId,
                ReasoningEffort: requestedReasoning, Limits: new(1_024, 32_768, 180)), "math-tools-" + Guid.NewGuid().ToString("N"), ct);
            await Complete(accepted.RunId);
            var events = completedEvents[accepted.RunId];
            var execution = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == "math.evaluate");
            Assert.True(execution.Data.GetProperty("success").GetBoolean());
            var calculation = execution.Data.GetProperty("result");
            Assert.Equal("add", calculation.GetProperty("operation").GetString());
            Assert.Equal(5d, Assert.Single(calculation.GetProperty("result").EnumerateArray()).GetDouble());
            var answer = new StringBuilder();
            foreach (var item in events.Where(item => item.Type == RunEventTypes.TextDelta))
            {
                var delta = item.Data.Deserialize<TextDeltaEvent>(Json)!;
                if (delta.ReplaceFrom is { } offset) answer.Length = Math.Min(answer.Length, offset);
                answer.Append(delta.Delta);
            }
            Assert.Matches(@"(?<!\d)5(?!\d)", answer.ToString());
        });

        string? speechFile = null;
        await Check("speech-synthesis-artifact-range-and-transcription", async () =>
        {
            var response = await client.SynthesizeSpeechAsync(new("Dies ist ein Sprachtest. Der rote Apfel liegt auf dem Tisch."), ct);
            Assert.Equal(SpeechProviderIds.SupertonicF5Cuda, response.Provider);
            var originalSpeech = await VerifyArtifact(response.Artifact);
            var convertedSpeech = Path.Combine(root, "speech-16khz.wav");
            await Docker(["run", "--rm", "--network", "none", "--entrypoint", "ffmpeg", "-v", root + ":/fixtures",
                "missum-ai/media:2.0.0", "-nostdin", "-v", "error", "-y", "-i", "/fixtures/" + Path.GetFileName(originalSpeech),
                "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "/fixtures/speech-16khz.wav"], ct);
            // Publish the shared fixture only once conversion has actually succeeded.
            Assert.True(File.Exists(convertedSpeech));
            speechFile = convertedSpeech;
            var upload = await client.UploadFileAsync(speechFile, "audio/wav", cancellationToken: ct);
            var text = await client.TranscribeAsync(new(upload.UploadId, "de"), ct);
            Assert.Equal("whisper-large-v3", text.Provider);
            Assert.NotEmpty(text.Segments);
            Assert.Contains("Apfel", text.Text, StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(Path.Combine(root, "transcription.json"), JsonSerializer.Serialize(text, Json), ct);
        });

        await Check("audiobook-paragraph-and-session-lifecycle", async () =>
        {
            var session = await client.CreateSpeechSessionAsync(new(SpeechContentProfile.Audiobook, "de"), ct);
            var ended = false;
            try
            {
                Assert.Equal(SpeechContentProfile.Audiobook, session.Profile);
                Assert.Equal(SpeechProviderIds.SupertonicF5Cuda, session.Provider);
                var paragraph = await client.SynthesizeSpeechParagraphAsync(session.SessionId,
                    new("Kapitel eins. Im Garten stand ein kleiner Apfelbaum.", 0), ct);
                Assert.True(paragraph.DurationSeconds > 0);
                Assert.True(paragraph.SampleRate > 0);
                Assert.Equal(SpeechProviderIds.SupertonicF5Cuda, paragraph.Provider);
                Assert.Equal(0, paragraph.ParagraphIndex);
                await VerifyArtifact(paragraph.Artifact);
                var done = await client.EndSpeechSessionAsync(session.SessionId, ct);
                ended = true;
                Assert.Equal("completed", done.State);
            }
            finally { if (!ended) await Cleanup("speech session " + session.SessionId, token => client.CancelSpeechSessionAsync(session.SessionId, token)); }
        });

        await Check("live-captions-translation-and-persistence", async () =>
        {
            Assert.True(speechFile is not null, "The real synthesis fixture is required.");
            var session = await client.CreateLiveCaptionSessionAsync(new("de", LiveCaptionMode.TranslateToEnglish,
                WindowMilliseconds: 10_000), ct);
            var stopped = false;
            try
            {
                Assert.Equal(LiveCaptionMode.TranslateToEnglish, session.Mode);
                await client.KeepLiveCaptionSessionAliveAsync(session.SessionId, ct);
                var wave = await File.ReadAllBytesAsync(speechFile!, ct);
                var chunk = await client.SendLiveCaptionChunkAsync(session.SessionId, 0, wave, ct);
                Assert.True(chunk.IsFinal);
                Assert.Contains("apple", chunk.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("whisper-large-v3-live", chunk.Provider, StringComparison.Ordinal);
                Assert.NotEmpty(chunk.Segments);
                var replay = await client.SendLiveCaptionChunkAsync(session.SessionId, 0, wave, ct);
                Assert.Equal(JsonSerializer.Serialize(chunk, Json), JsonSerializer.Serialize(replay, Json));
                // The API retains active session state; completed sessions are deliberately removed.
                var restored = await client.GetLiveCaptionSessionAsync(session.SessionId, ct);
                Assert.Equal(chunk.Transcript, restored.Transcript);
                Assert.Equal(1, restored.NextSequence);
                var done = await client.StopLiveCaptionSessionAsync(session.SessionId, ct);
                stopped = true;
                Assert.Equal("completed", done.State);
                Assert.Contains(chunk.Text, done.Transcript, StringComparison.Ordinal);
                Assert.Equal(done.Transcript, restored.Transcript);
                var removed = await Assert.ThrowsAsync<MissumAiApiException>(() => client.GetLiveCaptionSessionAsync(session.SessionId, ct));
                Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
                await File.WriteAllTextAsync(Path.Combine(root, "captions.json"), JsonSerializer.Serialize(new { chunk, restored, done }, Json), ct);
            }
            finally { if (!stopped) await Cleanup("caption session " + session.SessionId, token => client.StopLiveCaptionSessionAsync(session.SessionId, token)); }
        });

        var imagePath = Path.Combine(root, "red-square.png");
        await File.WriteAllBytesAsync(imagePath, Convert.FromBase64String(RedSquare), ct);
        await Check("image-analysis", async () =>
        {
            var upload = await client.UploadFileAsync(imagePath, "image/png", cancellationToken: ct);
            var accepted = await client.AnalyzeMediaAsync(new(upload.UploadId,
                "Welche Farbe hat das große Quadrat in der Bildmitte? Antworte auf Deutsch.",
                PreferredModelId: Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_VISION_MODEL"),
                ReasoningEffort: requestedReasoning), "image-analysis-" + Guid.NewGuid().ToString("N"), ct);
            await Complete(accepted.RunId);
            var result = MediaResult(accepted.RunId, "image", "frames + vision");
            Assert.Contains("rot", result.GetProperty("analysis").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("visionModelId").GetString()));
            Assert.Equal(requestedReasoning, result.GetProperty("visionReasoningEffort").GetString());
            Assert.Equal(requestedReasoning, result.GetProperty("reasoningEffort").GetString());
        });

        await Check("audio-analysis", async () =>
        {
            Assert.True(speechFile is not null, "The real synthesis fixture is required.");
            var upload = await client.UploadFileAsync(speechFile!, "audio/wav", cancellationToken: ct);
            var accepted = await client.AnalyzeMediaAsync(new(upload.UploadId, "Transkribiere den gesprochenen Inhalt.",
                    PreferredModelId: generalModelId, ReasoningEffort: requestedReasoning),
                "audio-analysis-" + Guid.NewGuid().ToString("N"), ct);
            await Complete(accepted.RunId);
            var result = MediaResult(accepted.RunId, "audio", "speech-to-text + general");
            Assert.Equal(generalModelId, result.GetProperty("modelId").GetString());
            Assert.Contains("Apfel", result.GetProperty("analysis").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Apfel", result.GetProperty("transcription").GetProperty("text").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("whisper-large-v3", result.GetProperty("transcription").GetProperty("provider").GetString());
            Assert.Equal(requestedReasoning, result.GetProperty("reasoningEffort").GetString());
        });

        await Check("video-analysis", async () =>
        {
            // The pinned, already built media image supplies ffmpeg. The only mount is this test's fresh fixture directory.
            await Docker(["run", "--rm", "--network", "none", "--entrypoint", "ffmpeg", "-v", root + ":/fixtures",
                "missum-ai/media:2.0.0", "-nostdin", "-v", "error", "-y", "-loop", "1", "-i", "/fixtures/red-square.png",
                "-t", "3", "-vf", "scale=256:256", "-pix_fmt", "yuv420p", "/fixtures/red-square.mp4"], ct);
            var upload = await client.UploadFileAsync(Path.Combine(root, "red-square.mp4"), "video/mp4", cancellationToken: ct);
            var accepted = await client.AnalyzeMediaAsync(new(upload.UploadId,
                "Beschreibe die sichtbare Farbe des Quadrats mit Zeitangabe. Das Video hat keinen Ton.",
                PreferredModelId: Environment.GetEnvironmentVariable("MISSUM_AI_NATIVE_VISION_MODEL"),
                ReasoningEffort: requestedReasoning), "video-analysis-" + Guid.NewGuid().ToString("N"), ct);
            await Complete(accepted.RunId);
            var result = MediaResult(accepted.RunId, "video", "frames + vision");
            Assert.Contains("rot", result.GetProperty("analysis").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("visionModelId").GetString()));
            Assert.Equal(requestedReasoning, result.GetProperty("visionReasoningEffort").GetString());
            Assert.Equal(requestedReasoning, result.GetProperty("reasoningEffort").GetString());
        });

        await Check("image-generation-and-download", async () =>
        {
            var accepted = await client.GenerateImageAsync(new("A red apple on a plain white background", 512, 512, 424242, 1),
                "image-generation-" + Guid.NewGuid().ToString("N"), ct);
            var artifacts = await Complete(accepted.RunId);
            var generated = Assert.Single(artifacts, item => item.MediaType.StartsWith("image/", StringComparison.Ordinal));
            Assert.True(generated.Length > 1_024);
            Assert.Equal("424242", generated.Metadata!["seed"]);
            Assert.Equal("512", generated.Metadata["width"]);
            Assert.Equal("512", generated.Metadata["height"]);
            var path = await VerifyArtifact(generated);
            var image = await File.ReadAllBytesAsync(path, ct);
            Assert.True(image.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(16, 4)));
            Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(image.AsSpan(20, 4)));
            await Docker(["run", "--rm", "--network", "none", "--entrypoint", "ffmpeg", "-v", root + ":/fixtures:ro",
                "missum-ai/media:2.0.0", "-nostdin", "-v", "error", "-i", "/fixtures/" + Path.GetFileName(path), "-f", "null", "-"], ct);
        });
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        async Task<string> VerifyArtifact(ArtifactDescriptor artifact)
        {
            Assert.True(artifact.Length > 0);
            var path = Path.Combine(root, artifact.ArtifactId + Path.GetExtension(artifact.FileName));
            await client.DownloadArtifactAsync(artifact.ArtifactId, path, cancellationToken: ct);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            Assert.Equal(artifact.Length, bytes.LongLength);
            Assert.Equal(artifact.Sha256.ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            await File.WriteAllBytesAsync(path, bytes[..Math.Max(1, bytes.Length / 3)], ct);
            await client.DownloadArtifactAsync(artifact.ArtifactId, path, new FileInfo(path).Length, ct);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, ct));
            return path;
        }

        async Task Cleanup(string name, Func<CancellationToken, Task> action)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await action(timeout.Token); }
            catch (MissumAiApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
            catch (Exception ex)
            {
                failures.Add("Cleanup " + name + ": " + ex.Message);
                output.WriteLine("Cleanup failed for " + name + ": " + ex);
            }
        }

        async Task<List<ArtifactDescriptor>> Complete(string runId)
        {
            output.WriteLine("Run " + runId);
            var events = new List<RunEvent>();
            var terminal = false;
            var runFinished = false;
            long cursor = 0;
            try
            {
                for (var reconnect = 0; !terminal && reconnect < 12; reconnect++)
                {
                    await foreach (var item in client.StreamRunEventsAsync(runId, cursor, ct))
                    {
                        Assert.True(item.Id > cursor);
                        Assert.Equal(runId, item.RunId);
                        cursor = item.Id;
                        events.Add(item);
                        if (item.Type is RunEventTypes.RunCompleted or RunEventTypes.RunFailed or RunEventTypes.RunCancelled)
                        { terminal = true; break; }
                    }
                }
                var snapshot = await client.GetRunAsync(runId, ct);
                // Worker runs append the terminal event immediately before committing the snapshot state.
                var settle = Stopwatch.StartNew();
                while (terminal && (snapshot.State is RunState.Running or RunState.Queued) && settle.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(100, ct);
                    snapshot = await client.GetRunAsync(runId, ct);
                }
                runFinished = snapshot.State is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;
                completedEvents[runId] = events;
                await File.WriteAllTextAsync(Path.Combine(root, runId + ".json"), JsonSerializer.Serialize(new { snapshot, events }, Json), ct);
                Assert.True(terminal, "The SSE stream did not deliver a terminal event.");
                Assert.Equal(RunEventTypes.RunCompleted, events[^1].Type);
                Assert.True(snapshot.State == RunState.Completed, $"{runId}: {snapshot.State}; {snapshot.ErrorCode}");
                var artifacts = events.Where(item => item.Type == RunEventTypes.ArtifactCreated)
                    .Select(item => item.Data.Deserialize<ArtifactDescriptor>(Json)!).ToList();
                var receipt = events[^1].Data.Deserialize<RunCompletedEvent>(Json)!;
                Assert.Equal(artifacts.Select(item => item.ArtifactId).Order(StringComparer.Ordinal),
                    (receipt.ArtifactIds ?? []).Order(StringComparer.Ordinal));
                // Re-read the journal through the API, proving that the worker result survived SSE delivery.
                var replay = new List<RunEvent>();
                await foreach (var item in client.StreamRunEventsAsync(runId, 0, ct))
                {
                    replay.Add(item);
                    if (item.Id == events[^1].Id) break;
                }
                Assert.Equal(JsonSerializer.Serialize(events, Json), JsonSerializer.Serialize(replay, Json));
                return artifacts;
            }
            finally { if (!runFinished) await Cleanup("run " + runId, token => client.CancelRunAsync(runId, token)); }
        }

        JsonElement MediaResult(string runId, string kind, string pipeline)
        {
            // Media summaries are persisted tool results; decoded source frames are intentionally internal artifacts.
            var completed = Assert.Single(completedEvents[runId], item => item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == "media.analyze");
            var result = completed.Data.GetProperty("result");
            Assert.False(result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False,
                "Media analysis reported failure: " + result.GetRawText());
            Assert.Equal(kind, result.GetProperty("kind").GetString());
            Assert.Equal(pipeline, result.GetProperty("metadata").GetProperty("pipeline").GetString());
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("modelId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("analysis").GetString()));
            return result;
        }
    }

    private static async Task Docker(string[] arguments, CancellationToken ct)
    {
        Assert.Equal("run", arguments[0]);
        var containerName = "missum-tool-fixture-" + Guid.NewGuid().ToString("N");
        using var fixtureDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fixtureDeadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var result = await RunDocker(["run", "--name", containerName, "--pull", "never", .. arguments.Skip(1)], fixtureDeadline.Token);
            Assert.True(result.ExitCode == 0, result.Output);
        }
        catch (Exception original)
        {
            // Killing the CLI does not stop its container. Remove only this fixture's unique container.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var result = await RunDocker(["rm", "--force", containerName], cleanup.Token);
                if (result.ExitCode != 0 && !result.Output.Contains("No such container", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fixture container cleanup failed: " + result.Output);
            }
            catch (Exception error) { throw new AggregateException(original, error); }
            throw;
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDocker(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(drain.Token);
            _ = await stdout;
            _ = await stderr;
            throw;
        }
        return (process.ExitCode, await stdout + await stderr);
    }
}

using Missum.Ai.Client;
using Missum.Ai.Contracts;
using System.Text.Json;
using Xunit.Abstractions;

namespace Missum.Tests;

/// <summary>
/// Opt-in End-to-End-Abnahme des Docker-Stacks über exakt die Client- und
/// Laufpfade, die auch Missum verwendet. Der Test ist bewusst domänenneutral;
/// Zielserver und Modell kommen aus Umgebungsvariablen.
/// </summary>
public sealed class DockerClientEndToEndLiveTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ProtocolJson = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    [Trait("Category", "Live")]
    public async Task GeneralChatEmitsSeveralVisibleDeltasBeforeCompletion()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MISSUM_AI_RUN_STREAMING_LIVE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        using var http = new HttpClient
        {
            BaseAddress = ResolveServerUrl(),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        using var client = new MissumAiClient(http, $"missum-streaming-live-{Guid.NewGuid():N}");
        var modelId = Environment.GetEnvironmentVariable("MISSUM_AI_LIVE_GENERAL_MODEL")?.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            modelId = "gpt-oss-120b";
        }

        var accepted = await client.CreateRunAsync(
            new RunRequest(
                MissumAiProtocol.Version,
                RunMode.General,
                [new RunMessage("user", [new ContentPart(
                    "text",
                    "Erkläre in zwölf kurzen, nummerierten Absätzen auf Deutsch, wie ein zuverlässiger Softwaretest aufgebaut wird. "
                    + "Schreibe normalen Markdown-Fließtext ohne Werkzeuge, JSON oder technische Metadaten.")])],
                Limits: new RunLimits(2_048, 32_768, 2_400),
                SessionId: $"missum-streaming-live-{Guid.NewGuid():N}",
                PreferredGeneralModelId: modelId,
                ConversationProfile: ConversationProfile.General),
            $"missum-streaming-live-{Guid.NewGuid():N}",
            timeout.Token);

        var runFinished = false;
        try
        {
            var deltas = new List<(long EventId, DateTimeOffset SeenAt, string Text)>();
            long? completedEventId = null;
            DateTimeOffset? completedAt = null;
            RunFailedEvent? failure = null;
            await foreach (var item in client.StreamRunEventsAsync(
                accepted.RunId,
                cancellationToken: timeout.Token))
            {
                if (item.Type == RunEventTypes.TextDelta)
                {
                    deltas.Add((
                        item.Id,
                        DateTimeOffset.UtcNow,
                        item.Data.Deserialize<TextDeltaEvent>(ProtocolJson)?.Delta ?? string.Empty));
                }
                else if (item.Type == RunEventTypes.RunCompleted)
                {
                    completedEventId = item.Id;
                    completedAt = DateTimeOffset.UtcNow;
                }
                else if (item.Type == RunEventTypes.RunFailed)
                {
                    failure = item.Data.Deserialize<RunFailedEvent>(ProtocolJson);
                }
            }

            var snapshot = await client.GetRunAsync(accepted.RunId, timeout.Token);
            runFinished = IsTerminal(snapshot.State);
            Assert.True(
                snapshot.State == RunState.Completed,
                $"Streaming-Lauf endete als {snapshot.State}: {failure?.ErrorCode ?? snapshot.ErrorCode} · {failure?.Message}");
            Assert.NotNull(completedEventId);
            Assert.NotNull(completedAt);
            Assert.True(deltas.Count >= 2, $"Erwartet wurden mehrere Live-Deltas, tatsächlich empfangen: {deltas.Count}.");
            Assert.All(deltas, delta => Assert.True(delta.EventId < completedEventId));
            Assert.True(
                completedAt - deltas[0].SeenAt >= TimeSpan.FromMilliseconds(250),
                "Das erste Textdelta traf erst gemeinsam mit dem Abschlussereignis ein.");
            var visibleText = string.Concat(deltas.Select(static delta => delta.Text));
            Assert.False(string.IsNullOrWhiteSpace(visibleText));
            Assert.Contains("Test", visibleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TGA", visibleText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Gebäudeausrüstung", visibleText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (!runFinished)
            {
                // This deadline remains usable after the test deadline expires.
                // Only the run accepted by this test is eligible for cleanup.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var stateCheck = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);
                stateCheck.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    runFinished = IsTerminal((await client.GetRunAsync(accepted.RunId, stateCheck.Token)).State);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    output.WriteLine($"Cleanup status for {accepted.RunId}: {exception.Message}");
                }
                if (!runFinished)
                {
                    try
                    {
                        // The endpoint and repository atomically preserve terminal
                        // runs if completion races this last status check.
                        await client.CancelRunAsync(accepted.RunId, cleanup.Token);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Keep the original assertion, stream, or timeout failure.
                        output.WriteLine($"Cleanup cancellation for {accepted.RunId}: {exception.Message}");
                    }
                }
            }
        }
    }

    private static bool IsTerminal(RunState state) => state is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;

    private static Uri ResolveServerUrl()
    {
        var value = Environment.GetEnvironmentVariable("MISSUM_AI_SERVER_URL")?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = "http://127.0.0.1:8080";
        }
        return new Uri(value.TrimEnd('/') + "/", UriKind.Absolute);
    }

}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class LongRunningEventReplayTests
{
    private static readonly int[] ExpectedPageSizes = [256, 256, 256, 234];
    [Fact]
    public async Task ReconnectDrainsEveryPageWithStableCursorAcrossInterleavedRuns()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunAsync(repository);
        var other = await CreateRunAsync(repository);
        await SeedProgressAsync(context, run, other, 1001);
        var terminal = await repository.AppendEventAsync(run, RunEventTypes.RunCompleted,
            new RunCompletedEvent("Fertig", null, 1, 1));
        await repository.UpdateStateAsync(run, RunState.Completed);

        var all = await repository.GetEventsAfterAsync(run, 0);
        Assert.Equal(1002, all.Count); // Existing complete-history API retains its contract.
        long cursor = 0;
        var replay = new List<RunEvent>();
        var pageSizes = new List<int>();
        while (true)
        {
            var page = await repository.GetEventsPageAfterAsync(run, cursor);
            if (page.Count == 0) break;
            pageSizes.Add(page.Count);
            Assert.InRange(page.Count, 1, 256);
            Assert.All(page, item => Assert.Equal(run, item.RunId));
            Assert.True(page[0].Id > cursor);
            replay.AddRange(page);
            cursor = page[^1].Id;
        }

        Assert.Equal(ExpectedPageSizes, pageSizes);
        Assert.Equal(all.Select(item => item.Id), replay.Select(item => item.Id));
        Assert.Equal(all.Select(item => item.Data.GetRawText()), replay.Select(item => item.Data.GetRawText()));
        Assert.Equal(replay.Count, replay.Select(item => item.Id).Distinct().Count());
        Assert.Equal(terminal.Id, cursor);
        Assert.Equal(RunEventTypes.RunCompleted, replay[^1].Type);
        Assert.Empty(await repository.GetEventsPageAfterAsync(run, cursor));
        Assert.Equal(17, (await repository.GetEventsPageAfterAsync(run, 0, pageSize: 17)).Count);
    }

    [Fact]
    public async Task VisibleProjectionKeepsAllTextRevisionsWithoutLoadingProgressOrOtherRuns()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunAsync(repository);
        var other = await CreateRunAsync(repository);
        var first = await repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent("Alter Zwischenstand."));
        await SeedProgressAsync(context, run, other, 1001);
        var revision = await repository.AppendEventAsync(run, RunEventTypes.TextDelta,
            new TextDeltaEvent("Korrigierter Zwischenstand.", ReplaceFrom: 0));
        await repository.AppendEventAsync(run, RunEventTypes.TextDelta,
            new TextDeltaEvent(" Nicht in der Hauptantwort.", AgentId: "separate-agent"));
        await repository.AppendEventAsync(other, RunEventTypes.TextDelta, new TextDeltaEvent("Fremde Sitzung."));
        await repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(" Endergebnis."));

        var visible = await repository.GetVisibleTextEventsAsync(run);
        Assert.Equal(4, visible.Count);
        Assert.All(visible, item => Assert.Equal(RunEventTypes.TextDelta, item.Type));
        Assert.Equal("Korrigierter Zwischenstand. Endergebnis.", CodingTextReconciler.Project(visible));
        Assert.Equal(CodingTextReconciler.Project(await repository.GetEventsAfterAsync(run, 0)),
            CodingTextReconciler.Project(visible));
        var resumed = await repository.GetVisibleTextEventsAsync(run, first.Id);
        Assert.Equal(revision.Id, resumed[0].Id);
        Assert.Equal(3, resumed.Count);
        Assert.Equal("Korrigierter Zwischenstand. Endergebnis.", CodingTextReconciler.Project(resumed));

        // A large unrelated status payload stays outside the projection's result.
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var payload = connection.CreateCommand();
        payload.CommandText = """
            UPDATE run_events SET data_json = $json
            WHERE id = (SELECT MIN(id) FROM run_events WHERE run_id = $run AND event_type = $type);
            """;
        payload.Parameters.AddWithValue("$run", run);
        payload.Parameters.AddWithValue("$type", RunEventTypes.ModelGeneration);
        payload.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new { detail = new string('x', 262_144) }));
        await payload.ExecuteNonQueryAsync();
        Assert.Equal(4, (await repository.GetVisibleTextEventsAsync(run)).Count);
        Assert.Equal("Korrigierter Zwischenstand. Endergebnis.",
            CodingTextReconciler.Project(await repository.GetVisibleTextEventsAsync(run)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    [InlineData(int.MaxValue)]
    public async Task ReplayRejectsUnboundedOrInvalidPages(int pageSize)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.GetEventsPageAfterAsync("unused-run", 0, pageSize: pageSize));
    }

    [Fact]
    public async Task ReasoningPresenceRespectsTurnCursorRoundAndRunWithoutLoadingItsBody()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunAsync(repository);
        var other = await CreateRunAsync(repository);
        var previous = await repository.AppendEventAsync(run, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent("Frühere Runde.", 7));
        await SeedProgressAsync(context, run, other, 1001);
        await repository.AppendEventAsync(run, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent(new string('x', 4096), 8));
        await repository.AppendEventAsync(other, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent("Andere Sitzung.", 7));

        Assert.True(await repository.HasReasoningDeltaAfterAsync(run, 0, 7));
        Assert.False(await repository.HasReasoningDeltaAfterAsync(run, previous.Id, 7));
        Assert.False(await repository.HasReasoningDeltaAfterAsync(run, previous.Id, 9));
        Assert.True(await repository.HasReasoningDeltaAfterAsync(run, previous.Id, 8));
        var current = await repository.AppendEventAsync(run, RunEventTypes.ReasoningDelta,
            new ReasoningDeltaEvent(new string('y', 4096), 7));
        Assert.True(await repository.HasReasoningDeltaAfterAsync(run, previous.Id, 7));
        Assert.False(await repository.HasReasoningDeltaAfterAsync(run, current.Id, 7));
        Assert.False(await repository.HasReasoningDeltaAfterAsync("absent-run", 0, 7));
    }

    [Fact]
    public async Task ScienceContinuationPreservesSteeringAndExactInterruptedTailAcrossLargeStatusJournal()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        const string original = "Prüfe die klassische Grenze.";
        const string prefix = "Erstes Ergebnis.";
        const string tail = " Zweites Ergebnis.";
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", Text: original)])], DeepResearch: true,
            SessionId: "science-replay", WorkspacePath: context.Root);
        var previous = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var other = await CreateRunAsync(repository);
        var boundary = await repository.AppendEventAsync(previous, RunEventTypes.RunStarted, new { });
        await repository.AppendEventAsync(previous, RunEventTypes.TextDelta, new TextDeltaEvent(prefix + tail));
        await SeedProgressAsync(context, previous, other, 1001);
        await repository.AppendEventAsync(previous, RunSteeringEventTypes.Applied,
            new RunSteeringEvent("units", 1, request.SessionId!, "Prüfe auch die Einheiten.", prefix.Length));
        await repository.AppendEventAsync(previous, RunProcessor.InterruptedTurnEventType,
            new { turnStartEventId = boundary.Id, content = "Exakter nativer Zwischenstand.",
                reasoningContent = "Nicht verlorener nativer Denktext.", exactNativeTailRecovered = true });
        await repository.SaveCheckpointAsync(previous, new AgentRunCheckpoint([new("user", original)],
            100, 83, 240_000, 45_000, StreamingTurnStartEventId: boundary.Id, DeepResearchCompleted: true));
        await repository.UpdateStateAsync(previous, RunState.Cancelled);
        await using (var connection = await context.Database.OpenConnectionAsync())
        await using (var age = connection.CreateCommand())
        {
            age.CommandText = "UPDATE runs SET created_at = $old WHERE run_id = $run;";
            age.Parameters.AddWithValue("$old", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-7)));
            age.Parameters.AddWithValue("$run", previous);
            await age.ExecuteNonQueryAsync();
        }
        var next = (await repository.CreateAsync(request, null)).Snapshot.RunId;

        var filtered = await repository.GetContinuationEventsAsync(previous);
        Assert.Equal(3, filtered.Count);
        var restored = await repository.GetGeneralSessionContextAsync(next, request);
        Assert.NotNull(restored);
        Assert.True(restored.WasInterrupted);
        Assert.Equal(tail, restored.VisibleResponse);
        Assert.Equal(new[] { original, prefix, "Prüfe auch die Einheiten." },
            restored.Request.Messages.SelectMany(message => message.Content).Select(part => part.Text));
        var native = Assert.Single(restored.Checkpoint.Messages, message => message.Role == "assistant");
        Assert.Equal("Exakter nativer Zwischenstand.", native.Content);
        Assert.Equal("Nicht verlorener nativer Denktext.", native.ReasoningContent);
        Assert.Null(restored.Checkpoint.StreamingTurnStartEventId);
        Assert.Contains(restored.Checkpoint.Messages,
            message => message.Content?.StartsWith("MISSUM_INTERRUPTED_MODEL_TURN", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task InterruptedResearchReceiptIncludesOnlyCompletedToolsAfterItsCursor()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunAsync(repository);
        var other = await CreateRunAsync(repository);
        var old = await repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted, new { callId = "before-cursor" });
        await SeedProgressAsync(context, run, other, 1001);
        await repository.AppendEventAsync(run, RunEventTypes.ServerToolStarted, new { callId = "unfinished" });
        await repository.AppendEventAsync(other, RunEventTypes.ServerToolCompleted, new { callId = "other-run" });
        var completed = await repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted,
            new { callId = "verified-fetch", tool = "web.fetch", result = new { title = "Originalquelle", content = "Belegtext." } });
        await repository.AppendEventAsync(run, RunEventTypes.ReasoningDelta, new ReasoningDeltaEvent("Denke weiter.", 1));

        var filtered = await repository.GetServerToolCompletedEventsAsync(run, old.Id);
        Assert.Equal(completed.Id, Assert.Single(filtered).Id);
        using var receipt = JsonDocument.Parse(await repository.GetInterruptedResearchReceiptAsync(run, old.Id, CancellationToken.None));
        var tool = Assert.Single(receipt.RootElement.GetProperty("completedTools").EnumerateArray());
        Assert.Equal("verified-fetch", tool.GetProperty("callId").GetString());
        Assert.Equal("Belegtext.", tool.GetProperty("result").GetProperty("content").GetString());
    }

    [Fact]
    public async Task BothReplayPathsHonorCallerCancellation()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = await CreateRunAsync(repository);
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetEventsPageAfterAsync(run, 0, cancellationToken: stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetVisibleTextEventsAsync(run, 0, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.HasReasoningDeltaAfterAsync(run, 0, 1, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetContinuationEventsAsync(run, 0, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetServerToolCompletedEventsAsync(run, 0, stop.Token));
    }

    private static async Task<string> CreateRunAsync(RunRepository repository) =>
        (await repository.CreateAsync(new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", Text: "Mehrtagige Forschung fortsetzen.")])], DeepResearch: true), null)).Snapshot.RunId;

    private static async Task SeedProgressAsync(TestServerContext context, string run, string other, int count)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            VALUES($run, $type, $json, $created);
            """;
        var runParameter = insert.Parameters.AddWithValue("$run", run);
        insert.Parameters.AddWithValue("$type", RunEventTypes.ModelGeneration);
        var jsonParameter = insert.Parameters.AddWithValue("$json", "{}");
        insert.Parameters.AddWithValue("$created", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-7)));
        for (var index = 0; index < count; index++)
        {
            runParameter.Value = run;
            jsonParameter.Value = JsonSerializer.Serialize(new { generatedTokens = index, detail = new string('x', 256) });
            await insert.ExecuteNonQueryAsync();
            if (index % 3 == 0)
            {
                runParameter.Value = other;
                await insert.ExecuteNonQueryAsync();
            }
        }
        await transaction.CommitAsync();
    }
}

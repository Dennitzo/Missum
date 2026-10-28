using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Data.Sqlite;

namespace Missum.Ai.Server.Tests;

public sealed class SpecializedSteeringTests
{
    [Fact]
    public async Task SpecializedOriginAndAppliedBoundarySurviveReopenWithoutDuplicateEvents()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var input = new RunSteeringRequest("specialized-session", "review-one", "Loop-Agent: Prüfe den offenen Grenzfall.");

        var accepted = Assert.IsType<RunSteeringAccepted>(await repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"));

        var reopened = Repository(context);
        var pending = Assert.Single(await reopened.GetPendingSteeringAsync(run));
        Assert.Equal("reviewer", pending.AgentId);
        Assert.Equal("loop", pending.AgentProfile);
        Assert.Equal(input.Text, pending.Text);
        Assert.Equal(accepted.Sequence, pending.Sequence);
        var acceptedEvent = Assert.Single(await reopened.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted)
            .Data.Deserialize<RunSteeringEvent>(MissumAiProtocol.CreateJsonOptions());
        Assert.Equal(pending, acceptedEvent);
        await reopened.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(new string('a', 31)));
        var checkpoint = new AgentRunCheckpoint([new LmChatMessage("user", input.Text)], 2, 0, 20, 10,
            VisibleTextLength: 31, AppliedSteeringSequence: accepted.Sequence);
        await reopened.SaveCheckpointAsync(run, checkpoint);
        await Repository(context).SaveCheckpointAsync(run, checkpoint);

        Assert.Empty(await repository.GetPendingSteeringAsync(run));
        var applied = Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Applied)
            .Data.Deserialize<RunSteeringEvent>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal("reviewer", applied.AgentId);
        Assert.Equal("loop", applied.AgentProfile);
        Assert.Equal(31, applied.VisibleTextOffset);
        var replay = Assert.IsType<RunSteeringAccepted>(await repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"));
        Assert.True(replay.Duplicate);
        Assert.Equal("applied", replay.State);
        Assert.Equal(accepted.Sequence, replay.Sequence);
    }

    [Fact]
    public async Task PendingHumanInputOwnsNextTurnInsteadOfAutomaticFollowup()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        await repository.AcceptSteeringAsync(run, new("specialized-session", "human", "Ändere die Fragestellung."));

        var automatic = await repository.TryAcceptSpecializedSteeringAsync(run,
            new("specialized-session", "old-review", "Folge dem vorherigen Ziel."), "reviewer", "loop");

        Assert.Null(automatic);
        var pending = Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.Equal("human", pending.InputId);
        Assert.Null(pending.AgentId);
        Assert.Null(pending.AgentProfile);
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task ASecondAutomaticHandoffCannotOvertakeAnUnappliedHandoff()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var first = new RunSteeringRequest("specialized-session", "review-one", "Erster nächster Schritt.");
        await repository.TryAcceptSpecializedSteeringAsync(run, first, "reviewer", "loop");

        Assert.Null(await repository.TryAcceptSpecializedSteeringAsync(run,
            first with { InputId = "review-two", Text = "Noch ein neuer Schritt." }, "reviewer", "loop"));
        Assert.True((await repository.TryAcceptSpecializedSteeringAsync(run, first, "reviewer", "loop"))!.Duplicate);
        Assert.Equal(first.InputId, Assert.Single(await repository.GetPendingSteeringAsync(run)).InputId);
    }

    [Fact]
    public async Task InputIdentityBindsExactTextAndTrustedOrigin()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var input = new RunSteeringRequest("specialized-session", "review-one", "Unveränderter Folgeauftrag.");
        await repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop");

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcceptSteeringAsync(run, input));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryAcceptSpecializedSteeringAsync(run, input with { Text = "Anderer Auftrag" }, "reviewer", "loop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryAcceptSpecializedSteeringAsync(run, input, "other-reviewer", "loop"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "different-profile"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryAcceptSpecializedSteeringAsync(run, input with { SessionId = "foreign-session" }, "reviewer", "loop"));
        var pending = Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.Equal("reviewer", pending.AgentId);
        Assert.Equal("loop", pending.AgentProfile);
        Assert.Equal(input.Text, pending.Text);

        var humanRun = await CreateAsync(repository);
        await repository.AcceptSteeringAsync(humanRun, input);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.TryAcceptSpecializedSteeringAsync(humanRun, input, "reviewer", "loop"));
        Assert.Null(Assert.Single(await repository.GetPendingSteeringAsync(humanRun)).AgentProfile);
    }

    [Theory]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Interrupted)]
    public async Task TerminalRunNeverStartsANewAutomaticHandoffButKeepsAcceptedRetryIdempotent(RunState terminal)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var input = new RunSteeringRequest("specialized-session", "review-one", "Gespeicherter Folgeauftrag.");
        var accepted = (await repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"))!;
        await repository.UpdateStateAsync(run, terminal);

        Assert.Null(await repository.TryAcceptSpecializedSteeringAsync(run,
            input with { InputId = "review-two" }, "reviewer", "loop"));
        var retry = Assert.IsType<RunSteeringAccepted>(await Repository(context).TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"));
        Assert.True(retry.Duplicate);
        Assert.Equal(accepted.Sequence, retry.Sequence);
        Assert.Equal(terminal, (await repository.GetAsync(run))!.State);
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task ConcurrentSpecializedRetriesCommitOneSequenceAndOneAcceptedEvent()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var input = new RunSteeringRequest("specialized-session", "stable-review", "Gezielt weiterforschen.");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await ready.Task;
            return await Repository(context).TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop");
        })).ToArray();
        ready.SetResult();

        var receipts = await Task.WhenAll(tasks);

        Assert.All(receipts, receipt => Assert.NotNull(receipt));
        Assert.Single(receipts, receipt => !receipt!.Duplicate);
        Assert.Single(receipts.Select(receipt => receipt!.Sequence).Distinct());
        Assert.Single(await repository.GetPendingSteeringAsync(run));
        Assert.Single(await repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task AcceptedEventFailureRollsBackTheSpecializedInputAndItsOrigin()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreateAsync(repository);
        var input = new RunSteeringRequest("specialized-session", "review-one", "Atomarer Folgeauftrag.");
        await ExecuteSqlAsync(context, $"""
            CREATE TRIGGER fail_specialized_accept BEFORE INSERT ON run_events
            WHEN NEW.event_type = '{RunSteeringEventTypes.Accepted}' BEGIN
                SELECT RAISE(ABORT, 'injected specialized event failure');
            END;
            """);

        await Assert.ThrowsAsync<SqliteException>(() => repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"));
        Assert.Empty(await repository.GetPendingSteeringAsync(run));
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
        await ExecuteSqlAsync(context, "DROP TRIGGER fail_specialized_accept;");
        Assert.False((await repository.TryAcceptSpecializedSteeringAsync(run, input, "reviewer", "loop"))!.Duplicate);
    }

    [Fact]
    public async Task FinalizationRaceCannotLoseAnAcceptedAutomaticHandoff()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var run = await CreateAsync(repository);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = Task.Run(async () =>
            {
                await start.Task;
                return await Repository(context).TryAcceptSpecializedSteeringAsync(run,
                    new("specialized-session", "review-race", "Offener nächster Schritt."), "reviewer", "loop");
            });
            var finalize = Task.Run(async () =>
            {
                await start.Task;
                return await Repository(context).FinalizeConversationAsync(run, new("Fertig", "coding/fixture", 10, 2));
            });
            start.SetResult();
            await Task.WhenAll(accept, finalize);
            var accepted = await accept;
            var finalized = await finalize;
            if (accepted is null)
            {
                Assert.True(finalized);
                Assert.Equal(RunState.Completed, (await repository.GetAsync(run))!.State);
                Assert.Empty(await repository.GetPendingSteeringAsync(run));
            }
            else
            {
                Assert.False(finalized);
                Assert.Equal(RunState.Running, (await repository.GetAsync(run))!.State);
                Assert.Equal(accepted.Sequence, Assert.Single(await repository.GetPendingSteeringAsync(run)).Sequence);
            }
        }
    }

    private static RunRepository Repository(TestServerContext context) => new(context.Database, new RunEventNotifier());

    private static async Task<string> CreateAsync(RunRepository repository)
    {
        var created = await repository.CreateAsync(new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", "Ursprüngliches Forschungsziel")])], SessionId: "specialized-session",
            PreferredCodingModelId: "coding/fixture", CodingOptions: new(WorkspacePath: "C:/fixture/project")), null);
        await repository.UpdateStateAsync(created.Snapshot.RunId, RunState.Running);
        return created.Snapshot.RunId;
    }

    private static async Task ExecuteSqlAsync(TestServerContext context, string sql)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = await command.ExecuteNonQueryAsync();
    }
}

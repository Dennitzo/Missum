using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Storage;

namespace Missum.Ai.Server.Tests;

public sealed class StartupStorageConcurrencyTests
{
    [Fact]
    public async Task RecoveryCommitsWhileAStatusReaderStillOwnsItsWalSnapshot()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        await repository.UpdateStateAsync(run, RunState.Running);
        await using var readConnection = await context.Database.OpenConnectionAsync();
        await using var read = readConnection.CreateCommand();
        read.CommandText = "SELECT state FROM runs WHERE run_id = $id;";
        read.Parameters.AddWithValue("$id", run);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Running", reader.GetString(0));

        // A status/SSE reader must not acquire a shared-cache table lock that
        // prevents startup recovery from updating runs.
        var recovery = Task.Run(() => repository.RecoverAsync());
        var finished = await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(3)));
        await reader.DisposeAsync();
        var recovered = await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(recovery, finished);
        Assert.Contains(run, recovered);
        Assert.Equal(RunState.Queued, (await repository.GetAsync(run))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryAndCleanupCanStartTogetherWithoutLosingRecoverableScience(bool cleanupFirst)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var active = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        var expired = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        await repository.SaveCheckpointAsync(active, new AgentRunCheckpoint([new("user", "Saved research")], 1, 1, 10, 5));
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var seed = connection.CreateCommand();
            seed.CommandText = """
                UPDATE runs SET state='Interrupted', error_code='run.gateway_restarted', updated_at=$old WHERE run_id=$active;
                UPDATE runs SET state='Completed', updated_at=$old WHERE run_id=$expired;
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<1000)
                INSERT INTO run_events(run_id,event_type,data_json,created_at)
                    SELECT $expired, 'text.delta', '{"delta":"old history"}', $old FROM seq;
                """;
            seed.Parameters.AddWithValue("$old", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddHours(-25)));
            seed.Parameters.AddWithValue("$active", active);
            seed.Parameters.AddWithValue("$expired", expired);
            await seed.ExecuteNonQueryAsync();
        }
        var originalHistory = await repository.GetEventsAfterAsync(expired, 0);
        using var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        var hold = await context.Database.EnterMaintenanceAsync(CancellationToken.None);
        Task cleaning;
        Task<IReadOnlyList<string>> recovering;
        try
        {
            if (cleanupFirst) { cleaning = cleanup.CleanupExpiredAsync(); recovering = repository.RecoverAsync(); }
            else { recovering = repository.RecoverAsync(); cleaning = cleanup.CleanupExpiredAsync(); }
            Assert.False(cleaning.IsCompleted);
            Assert.False(recovering.IsCompleted);
        }
        finally { hold.Dispose(); }
        await Task.WhenAll(cleaning, recovering).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(active, await recovering);
        Assert.Equal(RunState.Queued, (await repository.GetAsync(active))!.State);
        Assert.NotNull(await repository.GetCheckpointAsync(active));
        Assert.Equal(RunState.Completed, (await repository.GetAsync(expired))!.State);
        Assert.Equal(originalHistory.Select(item => item.Id),
            (await repository.GetEventsAfterAsync(expired, 0)).Select(item => item.Id));
    }

    [Fact]
    public async Task MaintenanceWaitCanBeCancelledAndDoesNotLeakTheGate()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        using var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        var hold = await context.Database.EnterMaintenanceAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var recovery = repository.RecoverAsync(cancellation.Token);
        var cleaning = cleanup.CleanupExpiredAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cleaning);
        }
        finally { hold.Dispose(); }
        Assert.Empty(await repository.RecoverAsync());
        await cleanup.CleanupExpiredAsync();
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Research fixture")])], ClientCapabilities: ["research.deliverables"],
        ResearchOptions: new(ProjectId: "science-fixture", ProtocolVersion: 2));
}

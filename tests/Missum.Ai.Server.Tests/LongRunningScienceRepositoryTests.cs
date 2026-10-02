using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Storage;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class LongRunningScienceRepositoryTests
{
    [Theory]
    [InlineData(RunMode.General, RunState.Running, null)]
    [InlineData(RunMode.Auto, RunState.Running, null)]
    [InlineData(RunMode.General, RunState.Interrupted, "run.gateway_stopped")]
    [InlineData(RunMode.Auto, RunState.Interrupted, "run.gateway_restarted")]
    public async Task GatewayRecoveryKeepsWeekOldScienceRunAndCheckpoint(
        RunMode mode, RunState state, string? error)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var request = ScienceRequest() with { Mode = mode };
        var (run, _) = await repository.CreateAsync(request, "science-week-old");
        var checkpoint = new AgentRunCheckpoint([new("assistant", "Der erste Grenzfall wurde geprüft.")],
            120, 83, 240_000, 45_000, DeepResearchCompleted: true);
        await repository.SaveCheckpointAsync(run.RunId, checkpoint);
        await repository.UpdateStateAsync(run.RunId, state, errorCode: error);
        await AgeRunAsync(context, run.RunId);

        var restored = Repository(context);
        Assert.Contains(run.RunId, await restored.RecoverAsync());
        Assert.Equal(RunState.Queued, (await restored.GetAsync(run.RunId))!.State);
        var retained = (await restored.GetCheckpointAsync(run.RunId))!;
        Assert.Equal(checkpoint.RoundCount, retained.RoundCount);
        Assert.Equal(checkpoint.ToolCallCount, retained.ToolCallCount);
        Assert.Equal(checkpoint.InputTokens, retained.InputTokens);
        Assert.Equal(checkpoint.OutputTokens, retained.OutputTokens);
        Assert.Equal(checkpoint.DeepResearchCompleted, retained.DeepResearchCompleted);
        Assert.Equal(Assert.Single(checkpoint.Messages).Content, Assert.Single(retained.Messages).Content);
        var same = await restored.CreateAsync(request, "science-week-old");
        Assert.False(same.Created);
        Assert.Equal(run.RunId, same.Snapshot.RunId);
    }

    [Fact]
    public async Task RecoveryDoesNotRestartCancelledOrUserInterruptedScience()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var (cancelled, _) = await repository.CreateAsync(ScienceRequest(), null);
        var (interrupted, _) = await repository.CreateAsync(ScienceRequest(), null);
        Assert.True(await repository.CancelAsync(cancelled.RunId));
        await repository.UpdateStateAsync(interrupted.RunId, RunState.Interrupted, errorCode: "run.user_paused");

        var recovered = await repository.RecoverAsync();
        Assert.DoesNotContain(cancelled.RunId, recovered);
        Assert.DoesNotContain(interrupted.RunId, recovered);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(cancelled.RunId))!.State);
        Assert.Equal(RunState.Interrupted, (await repository.GetAsync(interrupted.RunId))!.State);
    }

    [Fact]
    public async Task WaitingScienceIgnoresLegacyOverallDeadlineAfterSevenDays()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreatePendingAsync(context, repository, DateTimeOffset.MaxValue);
        await AgeRunAsync(context, run.RunId);

        Assert.Empty(await repository.QueueExpiredWaitingRunsAsync(DateTimeOffset.UtcNow.AddDays(7)));
        Assert.Equal(RunState.WaitingForClient, (await repository.GetAsync(run.RunId))!.State);
        Assert.NotNull(await repository.GetCheckpointAsync(run.RunId));
    }

    [Fact]
    public async Task LegacyOneHourHandoffIsUpgradedAndResultRemainsIdempotent()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var old = DateTimeOffset.UtcNow.AddDays(-7);
        var run = await CreatePendingAsync(context, repository, old.AddHours(1));
        await SetProposalCreatedAsync(context, run.Proposal.ProposalId, old);
        await AgeRunAsync(context, run.RunId);
        var original = await repository.AppendEventAsync(run.RunId, RunEventTypes.ClientToolProposed, run.Proposal);

        Assert.Empty(await repository.QueueExpiredWaitingRunsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(DateTimeOffset.MaxValue,
            (await repository.GetToolProposalAsync(run.Proposal.ProposalId, run.RunId))!.ExpiresAt);
        var replay = Assert.Single(await repository.GetEventsAfterAsync(run.RunId, 0));
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal(DateTimeOffset.MaxValue, replay.Data.GetProperty("expiresAt").GetDateTimeOffset());
        var result = new ClientToolResult(run.Proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { verified = true }));
        Assert.True(await repository.SaveClientToolResultAsync(run.RunId, result));
        Assert.False(await repository.SaveClientToolResultAsync(run.RunId, result));
        Assert.True(await repository.TryQueueClientToolContinuationAsync(run.RunId, run.Proposal.ProposalId));
        Assert.False(await repository.TryQueueClientToolContinuationAsync(run.RunId, run.Proposal.ProposalId));
    }

    [Fact]
    public async Task ExpiredIndividualProposalStillQueuesOnceWithoutRevivingCancellation()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var expired = await CreatePendingAsync(context, repository, DateTimeOffset.UtcNow.AddMinutes(-1));
        var cancelled = await CreatePendingAsync(context, repository, DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(await repository.CancelAsync(cancelled.RunId));

        Assert.Equal(expired.RunId, Assert.Single(await repository.QueueExpiredWaitingRunsAsync(DateTimeOffset.UtcNow)));
        Assert.Empty(await repository.QueueExpiredWaitingRunsAsync(DateTimeOffset.UtcNow));
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(cancelled.RunId))!.State);
        Assert.True((await repository.GetToolProposalAsync(expired.Proposal.ProposalId, expired.RunId))!.ExpiresAt < DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveClientToolResultAsync(cancelled.RunId,
            new(cancelled.Proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { }))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedLegacyResultCanBeRepostedAfterExpiryWithoutReactivationOrForeignOwnership(bool cancelled)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var run = await CreatePendingAsync(context, repository, DateTimeOffset.UtcNow.AddMinutes(10));
        var receipt = new ClientToolResult(run.Proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { verified = true }));
        Assert.True(await repository.SaveClientToolResultAsync(run.RunId, receipt));
        if (cancelled) Assert.True(await repository.CancelAsync(run.RunId));
        var old = DateTimeOffset.UtcNow.AddDays(-7);
        await using (var connection = await context.Database.OpenConnectionAsync())
        {
            await using var expire = connection.CreateCommand();
            expire.CommandText = """
                UPDATE client_tool_proposals
                SET created_at = $created, expires_at = $expires,
                    proposal_json = json_set(proposal_json, '$.expiresAt', $expires)
                WHERE proposal_id = $proposal;
                """;
            expire.Parameters.AddWithValue("$created", MissumAiDatabase.FormatTimestamp(old));
            expire.Parameters.AddWithValue("$expires", MissumAiDatabase.FormatTimestamp(old.AddHours(1)));
            expire.Parameters.AddWithValue("$proposal", run.Proposal.ProposalId);
            _ = await expire.ExecuteNonQueryAsync();
        }

        var restored = Repository(context);
        var state = (await restored.GetAsync(run.RunId))!.State;
        Assert.False(await restored.SaveClientToolResultAsync(run.RunId,
            receipt with { Result = JsonSerializer.SerializeToElement(new { verified = false }) }));
        Assert.Equal(state, (await restored.GetAsync(run.RunId))!.State);
        Assert.True((await restored.GetClientToolResultAsync(run.Proposal.ProposalId))!.Result.GetProperty("verified").GetBoolean());
        Assert.Equal(old.AddHours(1), (await restored.GetToolProposalAsync(run.Proposal.ProposalId, run.RunId))!.ExpiresAt);
        var (other, _) = await restored.CreateAsync(ScienceRequest(), null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restored.SaveClientToolResultAsync(other.RunId, receipt));
        Assert.Equal(state, (await restored.GetAsync(run.RunId))!.State);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("event")]
    [InlineData("checkpoint")]
    [InlineData("metadata")]
    public async Task CleanupKeepsWeekOldArtifactsNeededByAnActiveScienceRun(string reference)
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var (run, _) = await repository.CreateAsync(ScienceRequest(), null);
        await repository.UpdateStateAsync(run.RunId, RunState.Running);
        await AgeRunAsync(context, run.RunId);
        var artifacts = new ArtifactService(context.Database, context.WrappedOptions);
        var artifact = await CreateArtifactAsync(context, artifacts,
            reference == "metadata" ? new Dictionary<string, string> { ["runId"] = run.RunId } : null);
        if (reference == "event")
            await repository.AppendEventAsync(run.RunId, RunEventTypes.ArtifactCreated, artifact);
        if (reference == "checkpoint")
            await repository.SaveCheckpointAsync(run.RunId,
                new([new("tool", JsonSerializer.Serialize(new { artifactId = artifact.ArtifactId }))], 100, 50, 0, 0));
        if (reference == "request")
            await UpdateRequestAsync(context, run.RunId, ScienceRequest() with { ArtifactIds = [artifact.ArtifactId] });
        await ExpireArtifactAsync(context, artifact.ArtifactId);

        using var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();

        Assert.NotNull(await artifacts.ResolveAsync(artifact.ArtifactId));
        Assert.NotNull(await repository.GetAsync(run.RunId));
        await repository.UpdateStateAsync(run.RunId, RunState.Completed);
        await cleanup.CleanupExpiredAsync();
        Assert.Null(await artifacts.ResolveAsync(artifact.ArtifactId));
        Assert.False(File.Exists(Path.Combine(context.Options.ArtifactDirectory, artifact.ArtifactId + ".bin")));
    }

    [Fact]
    public async Task CleanupPreservesGatewayInterruptedScienceUntilRecoveryAfterAWeek()
    {
        using var context = new TestServerContext();
        var repository = Repository(context);
        var (run, _) = await repository.CreateAsync(ScienceRequest(), null);
        var artifacts = new ArtifactService(context.Database, context.WrappedOptions);
        var artifact = await CreateArtifactAsync(context, artifacts);
        await repository.AppendEventAsync(run.RunId, RunEventTypes.ArtifactCreated, artifact);
        await repository.SaveCheckpointAsync(run.RunId, new([], 100, 50, 0, 0));
        await repository.UpdateStateAsync(run.RunId, RunState.Interrupted, errorCode: "run.gateway_stopped");
        await AgeRunAsync(context, run.RunId);
        await ExpireArtifactAsync(context, artifact.ArtifactId);

        using var cleanup = new StorageCleanupService(context.Database, context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();
        Assert.NotNull(await artifacts.ResolveAsync(artifact.ArtifactId));
        Assert.NotNull(await repository.GetCheckpointAsync(run.RunId));
        Assert.Contains(run.RunId, await repository.RecoverAsync());
        Assert.Equal(RunState.Queued, (await repository.GetAsync(run.RunId))!.State);
    }

    private static RunRepository Repository(TestServerContext context) => new(context.Database, new RunEventNotifier());
    private static RunRequest ScienceRequest() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Prüfe den relativistischen Grenzfall mit Einheiten.")])],
        Limits: new(TimeoutSeconds: 3600), DeepResearch: true,
        ResearchOptions: new(AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));

    private static async Task<(string RunId, ToolProposal Proposal)> CreatePendingAsync(
        TestServerContext context, RunRepository repository, DateTimeOffset expires)
    {
        var (run, _) = await repository.CreateAsync(ScienceRequest(), null);
        var proposal = new ToolProposal("proposal-" + Guid.NewGuid().ToString("N"), run.RunId,
            ClientToolNames.ResearchCodeExecute, JsonSerializer.SerializeToElement(new { path = "simulation.py", timeoutSeconds = 600 }),
            ToolRiskClass.Process, "Simulation ausführen", expires);
        await repository.SaveToolProposalAsync(proposal);
        await repository.SaveCheckpointAsync(run.RunId, new([], 100, 50, 0, 0,
            PendingProposalId: proposal.ProposalId, PendingToolCallId: "simulation-call"));
        await repository.UpdateStateAsync(run.RunId, RunState.WaitingForClient);
        return (run.RunId, proposal);
    }

    private static async Task AgeRunAsync(TestServerContext context, string runId)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET created_at = $old, updated_at = $old WHERE run_id = $run;";
        command.Parameters.AddWithValue("$old", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-7)));
        command.Parameters.AddWithValue("$run", runId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task SetProposalCreatedAsync(TestServerContext context, string proposalId, DateTimeOffset created)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE client_tool_proposals SET created_at = $created WHERE proposal_id = $proposal;";
        command.Parameters.AddWithValue("$created", MissumAiDatabase.FormatTimestamp(created));
        command.Parameters.AddWithValue("$proposal", proposalId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task UpdateRequestAsync(TestServerContext context, string runId, RunRequest request)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET request_json = $request WHERE run_id = $run;";
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, context.Database.JsonOptions));
        command.Parameters.AddWithValue("$run", runId);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<ArtifactDescriptor> CreateArtifactAsync(TestServerContext context, ArtifactService artifacts,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var source = Path.Combine(context.Root, "simulation.py");
        await File.WriteAllTextAsync(source, "print('verified')");
        return await artifacts.ImportAsync(source, "simulation.py", "text/x-python", metadata);
    }

    private static async Task ExpireArtifactAsync(TestServerContext context, string artifactId)
    {
        await using var connection = await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE artifacts SET expires_at = $old WHERE artifact_id = $artifact;";
        command.Parameters.AddWithValue("$old", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow.AddDays(-7)));
        command.Parameters.AddWithValue("$artifact", artifactId);
        _ = await command.ExecuteNonQueryAsync();
    }
}

using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ClientToolProgressTests
{
    private static JsonElement J(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    [Fact]
    public async Task ProgressIsRevisionedIdempotentAndBoundToItsOwnedPendingCommand()
    {
        using var environment = new TestServerContext();
        var repository = new RunRepository(environment.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        var other = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        var proposal = Proposal(run);
        await repository.SaveToolProposalAsync(proposal);
        var first = new ClientToolProgress(proposal.ProposalId, 1, 5000, "Schritt eins", "", false, 123, 4000, 1024, 1000, 12);

        Assert.False((await repository.SaveClientToolProgressAsync(run, first)).StopRequested);
        Assert.False((await repository.SaveClientToolProgressAsync(run, first)).StopRequested);
        var latest = first with { Revision = 2, ElapsedMilliseconds = 10000, Stdout = "Schritt zwei" };
        await repository.SaveClientToolProgressAsync(run, latest);
        await repository.SaveClientToolProgressAsync(run, first); // delayed heartbeat is ignored
        Assert.Equal(latest, await repository.GetClientToolProgressAsync(run, proposal.ProposalId));
        Assert.Null(await repository.GetClientToolProgressAsync(other, proposal.ProposalId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveClientToolProgressAsync(other, latest));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RequestClientToolStopAsync(other, proposal.ProposalId, "Fremder Prozess"));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, latest with { Stdout = "anderer Inhalt" }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, latest with { Revision = 3, ElapsedMilliseconds = 9000 }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, latest with { Revision = 3, ProcessId = 456 }));
    }

    [Fact]
    public async Task StopIsDurableAndAppliesOnlyToTheCommandWithoutCancellingItsRun()
    {
        using var environment = new TestServerContext();
        var repository = new RunRepository(environment.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        var proposal = Proposal(run);
        await repository.SaveToolProposalAsync(proposal);
        var first = new ClientToolProgress(proposal.ProposalId, 1, 5000, "", "", false);
        await repository.SaveClientToolProgressAsync(run, first);
        Assert.True(await repository.RequestClientToolStopAsync(run, proposal.ProposalId, "Der Prozess meldet einen Fehler."));
        var reopened = new RunRepository(environment.Database, new RunEventNotifier());
        var response = await reopened.SaveClientToolProgressAsync(run, first with { Revision = 2, ElapsedMilliseconds = 10000 });
        Assert.True(response.StopRequested);
        Assert.Equal("Der Prozess meldet einen Fehler.", response.Reason);
        Assert.Equal(RunState.Queued, (await reopened.GetAsync(run))!.State);
        await reopened.SaveClientToolResultAsync(run, new(proposal.ProposalId, "failed", J("""{"success":false,"cancelled":true}""")));
        Assert.False(await reopened.RequestClientToolStopAsync(run, proposal.ProposalId, "Abschlussrennen"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveClientToolProgressAsync(run, first with { Revision = 3 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HumanSteeringStopsOnlyThePendingCommandBeforeApplyingTheNewPrompt(bool progressBeforeSteering)
    {
        using var environment = new TestServerContext();
        var repository = new RunRepository(environment.Database, new RunEventNotifier());
        var request = Request() with { SessionId = "steering-command" };
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var proposal = Proposal(run);
        await repository.SaveToolProposalAsync(proposal);
        await repository.SaveCheckpointAsync(run, new([], 0, 0, 0, 0,
            PendingProposalId: proposal.ProposalId, PendingToolCallId: "command-call"));
        await repository.UpdateStateAsync(run, RunState.WaitingForClient);
        var first = new ClientToolProgress(proposal.ProposalId, 1, 5000, "", "", false, ProcessId: 123);
        if (progressBeforeSteering)
            Assert.False((await repository.SaveClientToolProgressAsync(run, first)).StopRequested);

        await repository.AcceptSteeringAsync(run,
            new("steering-command", "human-input", "Schreibe das Python-Skript effizienter und starte es anschließend neu."));
        if (progressBeforeSteering)
            Assert.True(await repository.RequestPendingCommandStopForSteeringAsync(run));

        var response = await repository.SaveClientToolProgressAsync(run,
            progressBeforeSteering ? first : first with { Revision = 1 });
        Assert.True(response.StopRequested);
        Assert.Contains("umgelenkt", response.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RunState.WaitingForClient, (await repository.GetAsync(run))!.State);
        Assert.Single(await repository.GetPendingSteeringAsync(run));
    }

    [Fact]
    public async Task ProgressCannotControlOtherToolsOrTerminalRunsAndRejectsInvalidMetrics()
    {
        using var environment = new TestServerContext();
        var repository = new RunRepository(environment.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        await repository.SaveToolProposalAsync(Proposal(run) with { Name = ClientToolNames.CodingRead });
        var observation = new ClientToolProgress("command", 1, 5000, "", "", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveClientToolProgressAsync(run, observation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RequestClientToolStopAsync(run, observation.ProposalId, "Falsches Werkzeug"));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, observation with { Stdout = new string('x', RunRepository.MaximumCommandProgressCharacters + 1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, observation with { CpuMilliseconds = double.NaN }));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveClientToolProgressAsync(run, observation with { LastOutputElapsedMilliseconds = 6000 }));
    }

    [Fact]
    public async Task NewHeartbeatWakesOnlyTheMatchingWaitingProposal()
    {
        using var environment = new TestServerContext();
        var repository = new RunRepository(environment.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(Request(), null)).Snapshot.RunId;
        var proposal = Proposal(run);
        await repository.SaveToolProposalAsync(proposal);
        await repository.SaveCheckpointAsync(run, new([], 0, 0, 0, 0, PendingProposalId: proposal.ProposalId));
        await repository.UpdateStateAsync(run, RunState.WaitingForClient);
        await repository.SaveClientToolProgressAsync(run, new(proposal.ProposalId, 1, 5000, "", "", false));
        Assert.False(await repository.TryQueueClientToolProgressAsync(run, "unrelated"));
        Assert.False(await repository.TryQueueClientToolProgressAsync(run, proposal.ProposalId));
        await repository.SaveClientToolProgressAsync(run, new(proposal.ProposalId, 2, 30000, "", "", false));
        Assert.True(await repository.TryQueueClientToolProgressAsync(run, proposal.ProposalId));
        Assert.False(await repository.TryQueueClientToolProgressAsync(run, proposal.ProposalId));
        Assert.Equal(RunState.Queued, (await repository.GetAsync(run))!.State);
        await repository.UpdateStateAsync(run, RunState.WaitingForClient);
        await repository.SaveCheckpointAsync(run, new([], 0, 0, 0, 0, PendingProposalId: proposal.ProposalId,
            CommandMonitoring: new(proposal.ProposalId, 2, DateTimeOffset.UtcNow, [])));
        await repository.SaveClientToolProgressAsync(run, new(proposal.ProposalId, 3, 90000, "", "", false));
        Assert.False(await repository.TryQueueClientToolProgressAsync(run, proposal.ProposalId));
        await repository.SaveCheckpointAsync(run, new([], 0, 0, 0, 0, PendingProposalId: proposal.ProposalId,
            CommandMonitoring: new(proposal.ProposalId, 2, DateTimeOffset.UtcNow.AddSeconds(-61), [])));
        Assert.True(await repository.TryQueueClientToolProgressAsync(run, proposal.ProposalId));
        await repository.UpdateStateAsync(run, RunState.Cancelled);
        Assert.False(await repository.RequestClientToolStopAsync(run, proposal.ProposalId, "Lauf bereits beendet"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveClientToolProgressAsync(run,
            new(proposal.ProposalId, 4, 100000, "", "", false)));
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.Coding,
        [new("user", [new("text", Text: "Prüfe den Prozess.")])], ClientCapabilities: ["coding", "coding.process"], CodingOptions: new());
    private static ToolProposal Proposal(string run) => new("command", run, ClientToolNames.CodingCommand,
        J("""{"executable":"python","arguments":["check.py"]}"""), ToolRiskClass.Process, "Prüfung", DateTimeOffset.UtcNow.AddDays(1));
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class RunModelSelectionTests
{
    [Fact]
    public async Task LatestSelectionIsDurableAndNeverChangesTheOriginalTaskOrWorkspace()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", "Arbeite im vorhandenen Projekt")])], SessionId: "session-selection", CodingOptions: new(WorkspacePath: "C:/project"));
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        await repository.RequestModelSelectionAsync(run, new("session-selection", "model-first", "off"));
        var latest = await repository.RequestModelSelectionAsync(run, new("session-selection", "model-next", "on"));
        var reopened = new RunRepository(context.Database, new RunEventNotifier());
        var pending = (await reopened.GetLatestModelSelectionAsync(run, 0, CancellationToken.None))!.Value;
        Assert.Equal(latest.Id, pending.EventId); Assert.Equal("model-next", pending.Selection.ModelId);
        Assert.Equal("on", pending.Selection.ReasoningEffort);
        Assert.Null(await reopened.GetLatestModelSelectionAsync(run, latest.Id, CancellationToken.None));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(request), System.Text.Json.JsonSerializer.Serialize(await reopened.GetRequestAsync(run)));
    }

    [Fact]
    public async Task SelectionCannotLeakToAnotherSessionOrBeAcceptedAfterCompletion()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var run = (await repository.CreateAsync(new(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Auftrag")])], SessionId: "session-selection"), null)).Snapshot.RunId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RequestModelSelectionAsync(run, new("foreign", "model")));
        await repository.UpdateStateAsync(run, RunState.Completed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.RequestModelSelectionAsync(run, new("session-selection", "model")));
        Assert.Empty(await repository.GetEventsAfterAsync(run, 0));
    }
}

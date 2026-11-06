using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class PlanEvidenceArchiveTests
{
    [Fact]
    public async Task OlderReceiptCanBeQueriedAndUsedAfterCheckpointEvictionWithoutLoadingTheHistory()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", Text: "Prüfe die Publikation.")])]);
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Prüfe die Publikation."),
            J("""{"steps":[{"id":"s","title":"Phase 1: Publikation prüfen","status":"in_progress"}]}""")) with
        {
            Sequence = 231,
            Evidence = Enumerable.Range(1, 230).Select(index => new CodingToolEvidence("receipt-" + index, "coding.read",
                true, index == 1 ? "Publikation.md" : "other.txt", null, "Originaltext", index)).ToArray(),
        };
        await repository.SaveCheckpointAsync(run, new([], 1, 1, 0, 0, WorkingState: state));
        var bounded = state with { Evidence = state.Evidence.TakeLast(CodingWorkingStateReducer.MaximumEvidence).ToArray() };
        await repository.SaveCheckpointAsync(run, new([], 1, 1, 0, 0, WorkingState: bounded));
        var restoredRepository = new RunRepository(context.Database, new RunEventNotifier());
        var restored = (await restoredRepository.GetCheckpointAsync(run))!.WorkingState!;
        var query = await restoredRepository.QueryPlanEvidenceAsync(run, restored,
            J("""{"evidenceQuery":{"kind":"publication","path":"./Publikation.md"}}"""), CancellationToken.None);
        Assert.Equal("receipt-1", Assert.Single(query.GetProperty("candidates").EnumerateArray()).GetProperty("planEvidenceId").GetString());
        Assert.Equal(CodingWorkingStateReducer.MaximumEvidence, restored.Evidence.Count);

        var update = J("""{"steps":[{"id":"s","status":"completed","evidenceIds":["receipt-1"]}]}""");
        var hydrated = await restoredRepository.RestorePlanEvidenceAsync(run, restored, update, CancellationToken.None);
        var completed = CodingWorkingStateReducer.ApplyPlanUpdate(hydrated, update);
        Assert.Equal("completed", Assert.Single(completed.Plan).Status);
        Assert.Equal("receipt-1", Assert.Single(completed.Plan[0].EvidenceIds));
        Assert.Equal("in_progress", restored.Plan[0].Status);
        var foreignRun = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var foreign = await restoredRepository.RestorePlanEvidenceAsync(foreignRun, restored, update, CancellationToken.None);
        Assert.DoesNotContain(foreign.Evidence, receipt => receipt.Id == "receipt-1");
    }

    private static JsonElement J(string text) => JsonSerializer.Deserialize<JsonElement>(text);
}

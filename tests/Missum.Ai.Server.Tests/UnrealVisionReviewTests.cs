using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class UnrealVisionReviewTests
{
    [Fact]
    public async Task CompletionRequiresReviewOfTheLatestVisualChange()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new RunMessage("user", [new ContentPart("text", "Prüfe die Szene.")])])
        {
            CodingOptions = new CodingRunOptions(WorkspacePath: "C:/project"),
        };
        var runId = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        async Task RecordAsync(string name, object receipt)
        {
            var id = Guid.NewGuid().ToString("N");
            await repository.SaveToolProposalAsync(new ToolProposal(id, runId, name, JsonSerializer.SerializeToElement(new { }),
                ToolRiskClass.LocalMutation, name, DateTimeOffset.UtcNow.AddMinutes(1)));
            Assert.True(await repository.SaveClientToolResultAsync(runId,
                new ClientToolResult(id, "completed", JsonSerializer.SerializeToElement(receipt))));
        }
        Task EditAsync(string id, bool requiresReview) => RecordAsync("unreal.blueprint.edit",
            new { operationId = id, visualCheck = new { required = requiresReview } });
        Task ReviewAsync(string id, string verdict) => RecordAsync("unreal.status",
            new { visionReview = new { operationId = id, verdict, matchesCapture = true } });
        Task<string?> RepairAsync() => repository.GetUnrealCompletionRepairAsync(runId, CancellationToken.None);

        await EditAsync("logic", false);
        Assert.Null(await RepairAsync());
        await EditAsync("first", true);
        await ReviewAsync("old", "verified");
        Assert.Contains("fehlt", await RepairAsync());
        await ReviewAsync("first", "deviation");
        Assert.Contains("nach", await RepairAsync());
        await EditAsync("second", true);
        Assert.Contains("fehlt", await RepairAsync());
        await ReviewAsync("second", "verified");
        Assert.Null(await RepairAsync());
        await RecordAsync("unreal.camera.create", new { operationId = "camera", visualCheck = new { required = true } });
        Assert.Contains("fehlt", await RepairAsync());
        await ReviewAsync("camera", "verified");
        Assert.Null(await RepairAsync());
    }
}

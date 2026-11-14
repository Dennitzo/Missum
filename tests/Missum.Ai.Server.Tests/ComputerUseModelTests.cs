using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Gateway;

namespace Missum.Ai.Server.Tests;

public sealed class ComputerUseModelTests
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3];
    private static readonly string[] ExpectedTools = ["computer.act", "computer.observe"];

    [Fact]
    public void OldComputerObservationsAreRemovedWithoutLosingActionsOrLatestImage()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        var messages = new LmChatMessage[]
        {
            new("system", "policy"), new("user", "task"),
            new("assistant", ToolCalls: [new("old", "computer.observe", args)]),
            new("tool", "large stale window list", ToolCallId: "old"),
            new("user", "old frame", ComputerObservationId: "frame-old", ComputerObservationRunId: "run-1"),
            new("assistant", ToolCalls: [new("action", "computer.act", args)]),
            new("tool", "action completed", ToolCallId: "action"),
            new("assistant", ToolCalls: [new("new", "computer.observe", args)]),
            new("tool", "new window list", ToolCallId: "new"),
            new("user", "new frame", ComputerObservationId: "frame-new", ComputerObservationRunId: "run-1"),
        };
        var projected = ComputerPromptProjection.KeepLatestObservation(messages, "run-1");
        Assert.Equal(7, projected.Count);
        Assert.DoesNotContain(projected, item => item.ComputerObservationId == "frame-old");
        Assert.DoesNotContain(projected, item => item.ToolCallId == "old");
        Assert.Contains(projected, item => item.ToolCallId == "action" && item.Content == "action completed");
        Assert.Contains(projected, item => item.ComputerObservationId == "frame-new");
        Assert.Equal(10, ComputerPromptProjection.KeepLatestObservation(messages, "another-run").Count);
        Assert.Equal(10, messages.Length); // Projection never mutates the stored messages.
    }

    [Fact]
    public void ProjectionLeavesUnmatchedToolExchangesIntact()
    {
        var args = JsonSerializer.SerializeToElement(new { });
        var messages = new LmChatMessage[]
        {
            new("assistant", ToolCalls: [new("open", "computer.observe", args), new("other", "computer.act", args)]),
            new("tool", "result", ToolCallId: "open"),
            new("user", "old", ComputerObservationId: "old", ComputerObservationRunId: "run-1"),
            new("user", "new", ComputerObservationId: "new", ComputerObservationRunId: "run-1"),
        };
        Assert.Equal(messages, ComputerPromptProjection.KeepLatestObservation(messages, "run-1"));
    }

    [Fact]
    public void TransientObservationIsBoundToRunAndProposalAndNeverStoredInCheckpoint()
    {
        using var store = new ComputerObservationStore();
        var id = store.Put("run-1", "proposal-1", Png);
        Assert.Equal(Png, store.Get("run-1", id, "proposal-1"));
        Assert.Null(store.Get("run-2", id));
        Assert.Null(store.Get("run-1", id, "proposal-2"));
        Assert.Throws<InvalidOperationException>(() => store.Put("run-1", "proposal-1", Png));
        var message = new LmChatMessage("user", "screen", ComputerObservationId: id, ComputerObservationRunId: "run-1") { ComputerImage = Png };
        var checkpointJson = JsonSerializer.Serialize(message);
        Assert.DoesNotContain("ComputerImage", checkpointJson);
        Assert.DoesNotContain(Convert.ToBase64String(Png), checkpointJson);
        var projected = ModelRuntimeClient.PrepareNativeRequestMessages([message]);
        var nativeMessage = JsonSerializer.Serialize(ModelRuntimeClient.ToOpenAiMessage(projected[^1]));
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(Png), nativeMessage);
        store.RemoveRun("run-1");
        Assert.Null(store.Get("run-1", id));
    }

    [Fact]
    public void ComputerRunExposesOnlyTheTwoDesktopTools()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false });
        var request = new RunRequest("1", RunMode.Coding, [], ClientCapabilities: ["computer.use", "coding.process", "workspace", "workspace.open"],
            ClientTools: [new("computer.observe", "Observe", ToolRiskClass.ReadOnly, schema, 300, 10000),
                new("computer.act", "Act", ToolRiskClass.LocalMutation, schema, 300, 10000)]);
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(request);
        Assert.Equal(ExpectedTools, tools.Select(tool => tool.Name));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("coding.command", tools));
        Assert.Throws<ArgumentException>(() => catalog.GetAvailableTools(request with { ClientCapabilities = [] }));
        Assert.Throws<ArgumentException>(() => catalog.GetAvailableTools(request with { Mode = RunMode.General }));

        var realRequest = request with { ProtocolVersion = MissumAiProtocol.Version,
            Messages = [new RunMessage("user", [new ContentPart("text", "Test")])],
            ClientCapabilities = ["coding", "computer.use"], CodingOptions = new(WorkspacePath: @"C:\work") };
        RunRequestValidator.Validate(realRequest);
        Assert.False(ScienceInitialPlanGuard.Applies(realRequest));
    }
}

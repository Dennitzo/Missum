using System.Text.Json;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Tests;

public sealed class ComputerObservationGuardTests
{
    [Theory]
    [InlineData(false, "computer.act", true, true)]
    [InlineData(false, "computer.act", false, true)]
    [InlineData(true, "computer.observe", false, true)]
    [InlineData(true, "computer.observe", true, false)]
    [InlineData(true, "other", true, true)]
    public void EachActionRequiresSuccessfulObservation(bool required, string tool, bool succeeded, bool expected) =>
        Assert.Equal(expected, ComputerObservationGuard.RequiresAfterResult(required, tool, succeeded));

    [Fact]
    public void OnlyOneObservationCallIsAcceptedBeforeNextActionOrCompletion()
    {
        var observe = Call("observe", "computer.observe");
        var act = Call("act", "computer.act");
        Assert.True(ComputerObservationGuard.IsRequiredObserveCall([observe]));
        Assert.False(ComputerObservationGuard.IsRequiredObserveCall([]));
        Assert.False(ComputerObservationGuard.IsRequiredObserveCall([act]));
        Assert.False(ComputerObservationGuard.IsRequiredObserveCall([observe, act]));
    }

    [Fact]
    public void RequiredObservationTurnDoesNotStreamUnverifiedSuccessText()
    {
        var gate = new IncrementalVisibleTextGate(enabled: false);
        Assert.Null(gate.Push("Die Aktion war erfolgreich."));
        Assert.Null(gate.Flush());
        Assert.False(gate.HasStreamed);
    }

    [Fact]
    public void RecoveryReconstructsRequiredObservationAndIgnoresOtherRuns()
    {
        var messages = new List<LmChatMessage> { new("assistant", ToolCalls: [Call("act", "computer.act")]) };
        Assert.True(ComputerObservationGuard.RequiresFromHistory(messages, "run-a"));
        messages.Add(new("user", ComputerObservationId: "other", ComputerObservationRunId: "run-b"));
        Assert.True(ComputerObservationGuard.RequiresFromHistory(messages, "run-a"));
        messages.Add(new("user", ComputerObservationId: "fresh", ComputerObservationRunId: "run-a"));
        Assert.False(ComputerObservationGuard.RequiresFromHistory(messages, "run-a"));
        messages.Add(new("assistant", ToolCalls: [Call("act2", "computer.act")]));
        Assert.True(ComputerObservationGuard.RequiresFromHistory(messages, "run-a"));
    }

    [Fact]
    public void RequiredStateAndRetriesPersistWithoutScreenshotBytes()
    {
        var checkpoint = new AgentRunCheckpoint([], 0, 0, 0, 0, ComputerObservationRequired: true, ComputerObservationRetries: 2);
        var serialized = JsonSerializer.Serialize(checkpoint);
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(serialized)!;
        Assert.True(restored.ComputerObservationRequired);
        Assert.Equal(2, restored.ComputerObservationRetries);
        Assert.DoesNotContain("base64", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForcedCompactionPreservesLatestObservationAndMatchingToolExchange()
    {
        var observe = Call("capture", "computer.observe");
        var marker = new LmChatMessage("user", "Aktuelle Beobachtung", ComputerObservationId: "image", ComputerObservationRunId: "run-a");
        LmChatMessage[] history = [new("system", "policy"), new("user", "Bitte Aufgabe lösen"),
            new("assistant", "Frühere Arbeit"), new("assistant", ToolCalls: [observe]),
            new("tool", "Aufnahme", ToolCallId: observe.Id), marker];
        var plan = CodingContextCompactor.Plan(history, 4096, force: true);
        Assert.NotNull(plan);
        var compacted = CodingContextCompactor.Complete(plan, "Frühere Arbeit");
        Assert.Contains(compacted, message => message.ComputerObservationId == "image");
        Assert.Contains(compacted, message => message.ToolCalls?.Any(call => call.Id == "capture") == true);
        Assert.Contains(compacted, message => message.ToolCallId == "capture");
    }

    [Fact]
    public void ExhaustedContextFailsRatherThanDiscardingFreshObservation()
    {
        var observe = Call("capture", "computer.observe");
        LmChatMessage[] history = [new("system", new string('x', 80_000)), new("user", "Aufgabe"),
            new("assistant", ToolCalls: [observe]), new("tool", "Aufnahme", ToolCallId: observe.Id),
            new("user", "Aktuelles Bild", ComputerObservationId: "image", ComputerObservationRunId: "run-a")];
        Assert.Throws<ContextBudgetException>(() => ContextPlanner.Prepare(history, 4096, null,
            allowLossyCompaction: false, preserveConversationPrefix: true));
    }

    private static LmToolCall Call(string id, string name) => new(id, name, JsonSerializer.SerializeToElement(new { }));
}

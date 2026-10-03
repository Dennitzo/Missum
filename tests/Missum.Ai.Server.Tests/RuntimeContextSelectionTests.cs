using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class RuntimeContextSelectionTests
{
    [Fact]
    public void CodingSummaryIdentifiesTheRealTaskBeforeTrailingRuntimeMetadata()
    {
        var task = new LmChatMessage("user", "Implementiere den neuen Auftrag mit allen Anforderungen.");
        var runtime = Runtime();
        LmChatMessage[] messages = [Policy(), new("user", "Alter Auftrag"),
            new("assistant", new string('x', 100_000)), task, runtime];

        var plan = CodingContextCompactor.Plan(messages, 32_768);

        Assert.NotNull(plan);
        Assert.Same(task, plan.CurrentRequest);
        Assert.StartsWith("Ursprünglicher Auftrag:\n" + task.Content, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(CompactAgentContextPolicy.RuntimeMarker, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.Contains(runtime, plan.RecentMessages);
        var compacted = CodingContextCompactor.Complete(plan, "Belegte ältere Arbeit.");
        Assert.Contains(task, compacted);
        Assert.Equal(runtime, compacted[^1]);
        Assert.Equal(messages[0], compacted[0]);
    }

    [Fact]
    public void GeneralPlannerProtectsTheWholeRealTaskRatherThanADataBlock()
    {
        var task = new LmChatMessage("user", "BEGIN_REQUIREMENTS" + new string('r', 20_000) + "FINAL_REQUIREMENT");
        var runtime = Runtime();
        LmChatMessage[] messages = [Policy(), task, new("assistant", new string('h', 40_000)), runtime];

        var plan = ContextPlanner.Prepare(messages, 16_384, null, preserveConversationPrefix: true);

        Assert.True(plan.WasCompacted);
        Assert.Same(task, plan.Messages[1]);
        Assert.EndsWith("FINAL_REQUIREMENT", plan.Messages[1].Content, StringComparison.Ordinal);
        Assert.Equal(runtime, plan.Messages[^1]);
        Assert.Equal(messages[0], plan.Messages[0]);
    }

    [Fact]
    public void ChronologicalBudgetAndRepairInstructionsCannotBecomePartOfTheInitialSystemPrefix()
    {
        var policy = Policy();
        var task = new LmChatMessage("user", "Der aktuelle Auftrag.");
        var budget = new LmChatMessage("system", new CodingRunBudget(20, 40).Instruction(12, 8));
        var repair = new LmChatMessage("system", "Gezielten nächsten Schritt reparieren.");
        LmChatMessage[] messages = [policy, new("user", "Früherer Auftrag"),
            new("assistant", new string('x', 100_000)), task, Runtime(), budget, repair];

        var plan = CodingContextCompactor.Plan(messages, 32_768);

        Assert.NotNull(plan);
        Assert.Same(policy, Assert.Single(plan.SystemMessages));
        Assert.Contains(budget, plan.RecentMessages);
        Assert.Contains(repair, plan.RecentMessages);
        var completed = CodingContextCompactor.Complete(plan, "Alte Befunde.");
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(completed);
        Assert.Equal(policy.Content, Assert.Single(native, message => message.Role == "system").Content);
        Assert.Contains(native, message => message.Role == "user"
            && message.Content == "Missum-Laufanweisung:\n" + budget.Content);
        Assert.Contains(native, message => message.Role == "user"
            && message.Content == "Missum-Laufanweisung:\n" + repair.Content);
        Assert.Equal("Missum-Laufanweisung:\n" + repair.Content, native[^1].Content);
    }

    [Fact]
    public void SessionContinuationDoesNotReplaceItsNextTaskWithInitialRuntimeMetadata()
    {
        var previous = new AgentRunCheckpoint([Policy(), new("user", "Alter Auftrag"), new("assistant", "Altes Ergebnis")],
            1, 0, 0, 0, PreserveSessionPromptPrefix: true, ContextProfileVersion: "compact-v1");
        var task = new LmChatMessage("user", "Neue Nutzeraufgabe.");
        var next = CodingSessionContext.Continue(previous, [Policy(), task, Runtime()]);
        Assert.Same(task, next[^1]);
        Assert.Contains(next, message => message.Content == "Altes Ergebnis");
        Assert.Equal(CompactAgentContextPolicy.Marker + "\nStabile Kernregeln.", next[0].Content);
    }

    [Fact]
    public void RuntimeMetadataAloneCannotInventAnOriginalCodingTask()
    {
        LmChatMessage[] messages = [Policy(), new("assistant", new string('x', 100_000)), Runtime()];
        Assert.Null(CodingContextCompactor.Plan(messages, 32_768));
        Assert.True(ContextPlanner.IsRuntimeContext(messages[^1]));
        Assert.False(ContextPlanner.IsRuntimeContext(new("assistant", messages[^1].Content)));
        Assert.False(ContextPlanner.IsRuntimeContext(new("user", "Erkläre " + CompactAgentContextPolicy.RuntimeMarker)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompactionRetainsLatestRuntimeAndBudgetBeforeTheRecentCutExactlyOnce(bool normalizedInstruction)
    {
        var task = new LmChatMessage("user", "Behalte alle Anforderungen des tatsächlichen Auftrags.");
        var oldRuntime = Runtime();
        var latestRuntime = new LmChatMessage("user", CompactAgentContextPolicy.RuntimeMarker + "\n{\"subagentAvailable\":true}");
        var oldBudget = Instruction(new CodingRunBudget(30, 50).Instruction(1, 2), normalizedInstruction);
        var latestBudget = Instruction(new CodingRunBudget(30, 50).Instruction(8, 9), normalizedInstruction);
        var messages = new List<LmChatMessage>
            { Policy(), task, oldRuntime, new("assistant", new string('x', 100_000)), oldBudget, latestRuntime, latestBudget };
        for (var index = 0; index < 10; index++)
        {
            var call = new LmToolCall("call-" + index, "coding.command", JsonSerializer.SerializeToElement(new { executable = "python" }));
            messages.Add(new("assistant", ToolCalls: [call]));
            messages.Add(new("tool", "{\"status\":\"completed\"}", ToolCallId: call.Id));
        }

        var plan = CodingContextCompactor.Plan(messages, 32_768);

        Assert.NotNull(plan);
        Assert.Same(task, plan.CurrentRequest);
        Assert.Equal(new[] { latestRuntime, latestBudget }, plan.PreservedContextMessages);
        Assert.DoesNotContain(CompactAgentContextPolicy.RuntimeMarker, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(CodingRunBudget.PromptMarker, plan.SummaryRequest[1].Content, StringComparison.Ordinal);
        var completed = CodingContextCompactor.Complete(plan, "Ältere fachliche Arbeit.");
        Assert.Single(completed, message => message == latestRuntime);
        Assert.Single(completed, message => message == latestBudget);
        Assert.DoesNotContain(oldRuntime, completed);
        Assert.DoesNotContain(oldBudget, completed);
        var native = ModelRuntimeClient.PrepareLanguageBoundMessages(completed);
        Assert.Equal(messages[0].Content, Assert.Single(native, message => message.Role == "system").Content);
    }

    [Fact]
    public void TheLatestRuntimeBlockRemainsWholeEvenWhenOlderRuntimeDataIsReduced()
    {
        var task = new LmChatMessage("user", "REQ_BEGIN" + new string('r', 12_000) + "REQ_END");
        var oldRuntime = new LmChatMessage("user", CompactAgentContextPolicy.RuntimeMarker + "\n{\"workspacePath\":\"" + new string('o', 40_000) + "\"}");
        var latestRuntime = new LmChatMessage("user", CompactAgentContextPolicy.RuntimeMarker + "\n{\"workspacePath\":\"" + new string('p', 12_000) + "\"}");
        LmChatMessage[] messages = [Policy(), oldRuntime, task, new("assistant", new string('h', 40_000)), latestRuntime];

        var plan = ContextPlanner.Prepare(messages, 16_384, null, preserveConversationPrefix: true);

        Assert.True(plan.WasCompacted);
        Assert.Same(task, plan.Messages[2]);
        Assert.Same(latestRuntime, plan.Messages[^1]);
        using var json = JsonDocument.Parse(plan.Messages[^1].Content![(CompactAgentContextPolicy.RuntimeMarker.Length + 1)..]);
        Assert.Equal(new string('p', 12_000), json.RootElement.GetProperty("workspacePath").GetString());
        Assert.True(plan.Messages[1].Content!.Length < oldRuntime.Content!.Length);
    }

    private static LmChatMessage Instruction(string content, bool normalized) => normalized
        ? new("user", "Missum-Laufanweisung:\n" + content) : new("system", content);

    private static LmChatMessage Policy() => new("system", CompactAgentContextPolicy.Marker + "\nStabile Kernregeln.");

    private static LmChatMessage Runtime() => new("user", CompactAgentContextPolicy.RuntimeData(
        new(MissumAiProtocol.Version, RunMode.General, [new("user", [new("text", "Auftrag")])], WorkspacePath: "C:/workspace"),
        false, new(2026, 10, 3)));
}

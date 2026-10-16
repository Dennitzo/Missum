using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificRunContextPlannerTests
{
    private static readonly string[] ModelArguments = ["model.py"];

    [Theory]
    [InlineData(262_144)]
    [InlineData(1_048_576)]
    [InlineData(2_000_000)]
    public void EvaluatedPrefixBelowTheLoadedModelBudgetRemainsByteForByteIntact(int contextLength)
    {
        var messages = Initial();
        AddOldLogs(messages, 100);
        messages.Add(new("assistant", "Aktueller Befund.", ReasoningContent: "Bereits evaluierter Kontext."));
        var original = JsonSerializer.Serialize(messages);
        var plan = ScientificRunContextPlanner.Prepare(messages, contextLength, null, preserveConversationPrefix: true);
        Assert.False(plan.WasCompacted);
        Assert.Equal(original, JsonSerializer.Serialize(plan.Messages));
        Assert.Equal(original, JsonSerializer.Serialize(messages));
        Assert.Equal(ContextPlanner.ComputeInputTokenBudget(contextLength, null), plan.InputTokenBudget);
        AssertValidPairs(plan.Messages);
    }

    [Fact]
    public void CompactionPreservesCurrentProjectReadEditAndCommandEvidenceAsCompletePairs()
    {
        var messages = Initial();
        AddOldLogs(messages, 120);
        var currentRead = AddReceipt(messages, ClientToolNames.CodingRead, new { path = "Publikation.md" },
            new { success = true, content = "Aktuelle Publikation\n" + new string('p', 5000) });
        var currentEdit = AddReceipt(messages, ClientToolNames.CodingEdit, new { path = "Publikation.md", oldText = "alt", newText = "neu" },
            new { success = true, sha256 = new string('a', 64) });
        var currentCommand = AddReceipt(messages, ClientToolNames.CodingCommand, new { executable = "python", arguments = ModelArguments },
            new { success = true, exitCode = 0, stdout = "Gemessene Berechnungsdaten\n" + new string('n', 5000) });
        var original = JsonSerializer.Serialize(messages);
        var plan = ScientificRunContextPlanner.Prepare(messages, 32_768, null, preserveConversationPrefix: true);
        Assert.True(plan.WasCompacted);
        Assert.True(plan.EstimatedInputTokens <= plan.InputTokenBudget);
        foreach (var message in currentRead.Concat(currentEdit).Concat(currentCommand)) Assert.Contains(message, plan.Messages);
        Assert.Equal(original, JsonSerializer.Serialize(messages));
        AssertValidPairs(plan.Messages);
        Assert.DoesNotContain("research.read", plan.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingCallsCannotBeSplitOrReplayedByContextCompaction()
    {
        var messages = Initial();
        AddOldLogs(messages, 120);
        var pending = new LmChatMessage("assistant", ToolCalls:
            [new("pending", ClientToolNames.CodingWrite, JsonSerializer.SerializeToElement(new { path = "next.py", content = "print(1)" }))]);
        messages.Add(pending);
        var plan = ScientificRunContextPlanner.Prepare(messages, 32_768, null);
        Assert.True(plan.WasCompacted);
        Assert.Contains(pending, plan.Messages);
        AssertValidPairs(plan.Messages, allowPending: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoricalScienceDiagnosticsAreReadOnlyAndCanBeCompactedWithoutLosingLatestCause(bool native)
    {
        var messages = Initial();
        for (var index = 0; index < 80; index++)
        {
            var text = LegacyResearchContext.VerificationRepairMarker + "\ncause-" + index + "\n" + new string('x', 2000);
            messages.Add(new(native ? "user" : "system", native ? "Missum-Laufanweisung:\n" + text : text));
        }
        var latest = messages[^1];
        var original = JsonSerializer.Serialize(messages);
        var plan = ScientificRunContextPlanner.Prepare(messages, 32_768, null, preserveConversationPrefix: true);
        Assert.True(plan.WasCompacted);
        Assert.Contains(latest, plan.Messages);
        Assert.Contains(messages[1], plan.Messages);
        Assert.Single(plan.Messages, message => message.Content?.Contains(LegacyResearchContext.VerificationRepairMarker, StringComparison.Ordinal) == true);
        Assert.Equal(original, JsonSerializer.Serialize(messages));
    }

    [Fact]
    public void AProtectedUserTaskIsNeverTruncatedToForceAContextFit()
    {
        LmChatMessage[] messages = [new("system", "Unveränderliche Anweisung."), new("user", new string('q', 100_000))];
        var original = JsonSerializer.Serialize(messages);
        Assert.Throws<ContextBudgetException>(() => ScientificRunContextPlanner.Prepare(messages, 8192, null));
        Assert.Equal(original, JsonSerializer.Serialize(messages));
    }

    [Fact]
    public void CurrentLeanKernelReceiptRemainsAvailableWithoutBecomingACompletionGate()
    {
        var messages = Initial();
        AddOldLogs(messages, 120);
        var proof = AddReceipt(messages, ClientToolNames.MathFormalProof, new { source = "theorem identity : 1 = 1 := rfl" },
            new { success = true, formalVerification = "KernelAccepted", axiomAudit = new { passed = true } });
        var plan = ScientificRunContextPlanner.Prepare(messages, 32_768, null);
        Assert.True(plan.WasCompacted);
        foreach (var message in proof) Assert.Contains(message, plan.Messages);
        AssertValidPairs(plan.Messages);
        Assert.DoesNotContain(plan.Messages, message => message.Content?.Contains("research.deliverables.verify", StringComparison.Ordinal) == true);
    }

    private static List<LmChatMessage> Initial() =>
        [new("system", "Bearbeite das Projekt selbstständig."), new("user", "Prüfe die Gleichungen und bearbeite die Publikation direkt.")];

    private static void AddOldLogs(List<LmChatMessage> messages, int count)
    {
        for (var index = 0; index < count; index++)
            AddReceipt(messages, "coding.command", new { executable = "python", iteration = index },
                new { success = true, stdout = new string('l', 2200) });
    }

    private static LmChatMessage[] AddReceipt(List<LmChatMessage> messages, string name, object arguments, object result)
    {
        var id = "tool-" + Guid.NewGuid().ToString("N");
        LmChatMessage[] pair =
        [
            new("assistant", ToolCalls: [new(id, name, JsonSerializer.SerializeToElement(arguments))]),
            new("tool", JsonSerializer.Serialize(new { status = "completed", result }), ToolCallId: id),
        ];
        messages.AddRange(pair);
        return pair;
    }

    private static void AssertValidPairs(IReadOnlyList<LmChatMessage> messages, bool allowPending = false)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? []) Assert.True(pending.Add(call.Id));
            if (message.Role == "tool") Assert.True(message.ToolCallId is { } id && pending.Remove(id));
        }
        if (!allowPending) Assert.Empty(pending);
    }
}

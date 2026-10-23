using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingEvidenceContextTests
{
    [Fact]
    public void WorkingContextKeepsTheEntireOrderedPlanAndCriteriaBeforeBudgetedDetails()
    {
        var title = new string('ü', 400);
        var state = CodingWorkingState.Create("A long staged task") with
        {
            Plan = Enumerable.Range(1, 16).Select(index => new CodingPlanItem($"s{index}", title,
                index < 8 ? "completed" : index == 8 ? "in_progress" : "pending", [])).ToArray(),
            AcceptanceCriteria = Enumerable.Range(1, 16).Select(index => new CodingPlanItem($"a{index}", title, "pending", [])).ToArray(),
            Facts = [new("OPTIONAL_EVIDENCE_DETAIL", [])],
        };
        var context = CodingEvidenceContext.Build(state, 2048).Content!;
        var sections = context.Split('\n').Where(static line => line.StartsWith('{')).Select(static line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var steps = sections.Where(static section => section.GetProperty("section").GetString() == "plan").ToArray();
        var criteria = sections.Where(static section => section.GetProperty("section").GetString() == "acceptance").ToArray();
        Assert.Equal(16, steps.Length);
        Assert.Equal(16, criteria.Length);
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal($"s{index + 1}", steps[index].GetProperty("data").GetProperty("Id").GetString());
            Assert.Equal($"a{index + 1}", criteria[index].GetProperty("data").GetProperty("Id").GetString());
            Assert.Equal(title, steps[index].GetProperty("data").GetProperty("Title").GetString());
        }
        Assert.Equal("completed", steps[0].GetProperty("data").GetProperty("Status").GetString());
        Assert.DoesNotContain("OPTIONAL_EVIDENCE_DETAIL", context, StringComparison.Ordinal);
        Assert.Contains("Weitere gespeicherte Details", context, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedLargeArgumentsShrinkButOpenCallsAndTheirOriginalJournalRemainExact()
    {
        var completed = new LmToolCall("done", "coding.write", JsonSerializer.SerializeToElement(new { path = "done.py", content = new string('x', 32_000) }));
        var pending = new LmToolCall("open", "coding.edit", JsonSerializer.SerializeToElement(new { path = "pending.py", oldText = new string('y', 32_000), newText = "replacement" }));
        var receipt = new LmChatMessage("tool", "{\"success\":true,\"sha256\":\"actual journal receipt\"}", ToolCallId: "done");
        LmChatMessage[] source = [new("user", "Perform the original task"), new("assistant", ToolCalls: [completed, pending]), receipt];
        var result = CodingEvidenceContext.CompactCompletedCalls(source);
        Assert.True(result[1].ToolCalls![0].Arguments.GetRawText().Length < 2000);
        Assert.Equal("done.py", result[1].ToolCalls![0].Arguments.GetProperty("path").GetString());
        Assert.Equal(pending.Arguments.GetRawText(), result[1].ToolCalls![1].Arguments.GetRawText());
        Assert.Equal(32_000, source[1].ToolCalls![0].Arguments.GetProperty("content").GetString()!.Length);
        Assert.Same(receipt, result[2]);
        var context = ContextPlanner.Prepare(source, 32_768, null, allowLossyCompaction: false);
        Assert.Equal(pending.Arguments.GetRawText(), context.Messages[1].ToolCalls![1].Arguments.GetRawText());
        Assert.Equal(result[1].ToolCalls![0].Arguments.GetRawText(), CodingEvidenceContext.CompactCompletedCalls(result)[1].ToolCalls![0].Arguments.GetRawText());
    }

    [Fact]
    public void AReceiptBeforeItsCallDoesNotAuthorizeArgumentCompaction()
    {
        var call = new LmToolCall("same", "coding.command", JsonSerializer.SerializeToElement(new { executable = "pwsh", arguments = new[] { new string('x', 20_000) } }));
        LmChatMessage[] source = [new("tool", "earlier unrelated receipt", ToolCallId: "same"), new("assistant", ToolCalls: [call])];
        Assert.Equal(call.Arguments.GetRawText(), CodingEvidenceContext.CompactCompletedCalls(source)[1].ToolCalls![0].Arguments.GetRawText());
    }

    [Fact]
    public void RollingSummaryPreservesAnOpenLargeToolAndRecognizesTheOriginalTaskBeforeWorkingMemory()
    {
        var task = new LmChatMessage("user", "The authoritative task remains unchanged");
        var messages = new List<LmChatMessage> { new("system", "Trusted policy"), task };
        for (var index = 0; index < 70; index++)
        {
            var call = new LmToolCall("done-" + index, "coding.read", JsonSerializer.SerializeToElement(new { path = "file-" + index }));
            messages.Add(new("assistant", ToolCalls: [call]));
            messages.Add(new("tool", "{\"content\":\"1: read evidence\"}", ToolCallId: call.Id));
        }
        var state = CodingWorkingState.Create(task.Content!);
        messages.Add(CodingEvidenceContext.Build(state));
        var open = new LmToolCall("still-open", "coding.edit", JsonSerializer.SerializeToElement(new { oldText = new string('x', 20_000), newText = "safe" }));
        messages.Add(new("assistant", ToolCalls: [open]));
        var plan = CodingContextCompactor.Plan(messages, 8192, state)!;
        Assert.NotNull(plan);
        Assert.Same(task, plan.CurrentRequest);
        var compacted = CodingContextCompactor.Complete(plan, "Confirmed prior reads. The edit remains pending.");
        Assert.Equal(open.Arguments.GetRawText(), Assert.Single(compacted.SelectMany(static message => message.ToolCalls ?? []), call => call.Id == "still-open").Arguments.GetRawText());
        Assert.DoesNotContain(compacted, message => message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true);
        Assert.NotNull(plan.WorkingStateMessage);
        Assert.Contains(plan.SummaryRequest, message => message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true);
        Assert.Contains(compacted, message => ReferenceEquals(message, task));
        Assert.Equal("Trusted policy", Assert.Single(compacted, message => message.Role == "system").Content);
    }
}

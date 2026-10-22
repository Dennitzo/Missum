using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeActivityProjectionTests
{
    private static JsonElement Step(string id, string tool, string status = "completed", int offset = 0) =>
        JsonSerializer.SerializeToElement(new { id, tool, status, contentOffset = offset, detail = "PRIVATE REASONING 20000 tokens" });

    [Fact]
    public void PrefillReasoningAndCompactionUseOneNonDisclosureSlot()
    {
        var initial = Assert.Single(NativeActivityProjection.Build([], true, 0));
        var thinking = Assert.Single(NativeActivityProjection.Build([
            Step("r", "assistant.reasoning", "running"), Step("p", "assistant.progress"), Step("c", "context.compact")], true, 0));
        Assert.Equal(initial.Key, thinking.Key);
        Assert.Equal(initial.Offset, thinking.Offset);
        Assert.Equal("assistant.activity", thinking.Step.GetProperty("tool").GetString());
        Assert.False(thinking.Step.TryGetProperty("detail", out _));
        Assert.False(thinking.Step.TryGetProperty("generatedTokens", out _));
    }

    [Theory]
    [InlineData("coding.read")]
    [InlineData("subagent")]
    public void NextToolReusesTheThinkingSlotAndOriginalPosition(string tool)
    {
        var before = Assert.Single(NativeActivityProjection.Build([Step("r", "assistant.reasoning", "running", 3)], true, 3));
        var after = Assert.Single(NativeActivityProjection.Build([
            Step("r", "assistant.reasoning", offset: 3), Step("call", tool, "running", 3)], true, 10));
        Assert.Equal(before.Key, after.Key);
        Assert.Equal(before.Offset, after.Offset);
        Assert.Equal("call", after.Step.GetProperty("id").GetString());
    }

    [Fact]
    public void FinishingAndAnswerStreamingDoNotRemoveOrMoveTheTerminalSlot()
    {
        var steps = new[] { Step("read", "coding.read", offset: 5) };
        var shortAnswer = NativeActivityProjection.Build(steps, true, 10);
        var longAnswer = NativeActivityProjection.Build(steps, true, 10000);
        var done = NativeActivityProjection.Build(steps, false, 10000);
        Assert.Equal(shortAnswer.Select(x => (x.Key, x.Offset)), longAnswer.Select(x => (x.Key, x.Offset)));
        Assert.Equal(longAnswer.Select(x => (x.Key, x.Offset)), done.Select(x => (x.Key, x.Offset)));
        Assert.Equal("completed", done[^1].Step.GetProperty("status").GetString());
    }

    [Fact]
    public void PagingOlderStepsDoesNotRenumberTheCurrentSlot()
    {
        var tail = new[] { Step("last", "coding.read"), Step("r", "assistant.reasoning", "running") };
        var recent = NativeActivityProjection.Build(tail, true, 0);
        var history = NativeActivityProjection.Build(new[] { Step("older", "coding.write") }.Concat(tail), true, 0);
        Assert.Equal(recent[^1].Key, history[^1].Key);
    }

    [Fact]
    public void NarrationAndSourceTextAreNotReplacedByReasoning()
    {
        var result = NativeActivityProjection.Build([Step("r", "assistant.reasoning"), Step("n", "assistant.narration"),
            Step("t", "coding.read", "running")], true, 20);
        Assert.Equal(2, result.Count);
        Assert.Equal("coding.read", result[0].Step.GetProperty("tool").GetString());
        Assert.Equal("assistant.narration", result[1].Step.GetProperty("tool").GetString());
    }
    [Fact]
    public void VirtualizedChatUsesTheSameSlotAndNeverProjectsReasoningDetails()
    {
        var message = JsonSerializer.SerializeToElement(new { role = "assistant", status = "streaming" });
        var steps = Enumerable.Range(0, 150).Select(i => Step("read-" + i, "coding.read"))
            .Append(Step("r", "assistant.reasoning", "running")).ToArray();
        var before = Missum.App.Pages.NativeAssistantPage.BuildLogicalBlocks(message, "Answer", steps, [], false, []);
        var thinking = Assert.Single(before, block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
        var after = Missum.App.Pages.NativeAssistantPage.BuildLogicalBlocks(message, "Answer", [.. steps, Step("next", "coding.read", "running")], [], false, []);
        var next = Assert.Single(after, block => block.Kind == "tool" && block.Data.TryGetProperty("id", out var id) && id.GetString() == "next");
        Assert.Equal(thinking.Key, next.Key);
        Assert.DoesNotContain(before, block => block.Kind == "thinking" || block.Text.Contains("PRIVATE REASONING", StringComparison.Ordinal));
        Assert.False(thinking.Data.TryGetProperty("detail", out _));
        Assert.Equal("Answer", string.Concat(after.Where(block => block.Kind == "text").Select(block => block.Text)));
    }
}

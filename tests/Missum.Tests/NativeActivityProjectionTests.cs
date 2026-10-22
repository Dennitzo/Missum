using System.Text.Json;
using Missum.App.Pages;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeActivityProjectionTests
{
    private static readonly string[] ExpectedNarrationAnswerThinking = ["assistant.narration", "assistant.answer", "assistant.activity"];
    private static readonly string[] ExpectedNarrationTool = ["assistant.narration", "coding.read"];
    private static JsonElement Step(string id, string tool, string status = "completed", int offset = 0) =>
        JsonSerializer.SerializeToElement(new { id, tool, status, contentOffset = offset, detail = "PRIVATE REASONING 20000 tokens" });
    private static string Tool(NativeActivityProjection.Entry item) => item.Step.GetProperty("tool").GetString()!;

    [Fact]
    public void PrefillReasoningAndCompactionUseOneNonDisclosureTailSlot()
    {
        var initial = Assert.Single(NativeActivityProjection.Build([], true, 0));
        var thinking = Assert.Single(NativeActivityProjection.Build([
            Step("r", "assistant.reasoning", "running"), Step("p", "assistant.progress"), Step("c", "context.compact")], true, 0));
        Assert.Equal(initial.Key, thinking.Key);
        Assert.Equal(initial.Offset, thinking.Offset);
        Assert.Equal("assistant.activity", Tool(thinking));
        Assert.False(thinking.Step.TryGetProperty("detail", out _));
        Assert.False(thinking.Step.TryGetProperty("generatedTokens", out _));
    }

    [Theory]
    [InlineData("coding.read")]
    [InlineData("subagent")]
    [InlineData("assistant.narration")]
    public void AnyVisibleStepConsumesTheThinkingSlot(string tool)
    {
        var before = Assert.Single(NativeActivityProjection.Build([Step("r", "assistant.reasoning", "running")], true, 0));
        var after = Assert.Single(NativeActivityProjection.Build([
            Step("r", "assistant.reasoning"), Step("call", tool, "running")], true, 0));
        Assert.Equal(before.Key, after.ReplacesKey);
        Assert.Equal((tool == "assistant.narration" ? "narration:" : "tool:") + "call", after.Key);
        Assert.Equal(before.Offset, after.Offset);
        Assert.Equal("call", after.Step.GetProperty("id").GetString());
        Assert.NotEqual("assistant.activity", Tool(after));
    }

    [Fact]
    public void AnswerStreamingConsumesThePlaceholderAndKeepsItsSourceKey()
    {
        var steps = new[] { Step("r", "assistant.reasoning", "running") };
        var thinking = Assert.Single(NativeActivityProjection.Build(steps, true, 0));
        var answer = Assert.Single(NativeActivityProjection.Build(steps, true, 100));
        var longer = Assert.Single(NativeActivityProjection.Build(steps, true, 1000));
        Assert.Equal(thinking.Key, answer.ReplacesKey);
        Assert.Equal("source:0", answer.Key);
        Assert.Equal(answer.Key, longer.Key);
        Assert.Equal("assistant.answer", Tool(answer));
        Assert.Equal(0, answer.Offset);
        Assert.Equal(100, answer.EndOffset);
        Assert.Equal(1000, longer.EndOffset);
    }

    [Fact]
    public void ThinkingAlwaysFollowsTheLatestVisibleOutput()
    {
        var result = NativeActivityProjection.Build([
            Step("r1", "assistant.reasoning"), Step("n", "assistant.narration"),
            Step("r2", "assistant.reasoning", "running", 20)], true, 20);
        Assert.Equal(ExpectedNarrationAnswerThinking, result.Select(Tool));
        Assert.Equal(20, result[^1].Offset);
        Assert.Single(result, entry => Tool(entry) == "assistant.activity");
    }

    [Fact]
    public void NarrationPreservesChronologyInsteadOfMovingTheFollowingToolBeforeIt()
    {
        var result = NativeActivityProjection.Build([Step("r", "assistant.reasoning"), Step("n", "assistant.narration"),
            Step("t", "coding.read", "running")], true, 0);
        Assert.Equal(ExpectedNarrationTool, result.Select(Tool));
        Assert.DoesNotContain(result, item => Tool(item) == "assistant.activity");
    }

    [Fact]
    public void CompletedMessagesNeverContainThinkingPlaceholders()
    {
        var result = NativeActivityProjection.Build([Step("r", "assistant.reasoning", "running")], false, 100);
        Assert.Equal("assistant.answer", Tool(Assert.Single(result)));
        Assert.Empty(NativeActivityProjection.Build([Step("r", "assistant.reasoning")], false, 0));
    }

    [Fact]
    public void NewThinkingAfterExistingAnswerHasAnIndependentTailSlot()
    {
        var result = NativeActivityProjection.Build([Step("r", "assistant.reasoning", "running", 50)], true, 50);
        Assert.Equal("assistant.answer", Tool(result[0]));
        Assert.Equal("assistant.activity", Tool(result[1]));
        Assert.Equal(50, result[0].EndOffset);
        Assert.Equal(50, result[1].Offset);
        var next = NativeActivityProjection.Build([Step("r", "assistant.reasoning", "running", 50)], true, 60);
        Assert.Equal(result[^1].Key, next[^1].ReplacesKey);
        Assert.Equal(result[0].Key, next[^1].Key);
        Assert.Single(next);
        Assert.Equal("assistant.answer", Tool(next[^1]));
    }

    [Fact]
    public void PagingOlderStepsDoesNotRenumberTheCurrentTailSlot()
    {
        var tail = new[] { Step("last", "coding.read"), Step("r", "assistant.reasoning", "running") };
        var recent = NativeActivityProjection.Build(tail, true, 0);
        var history = NativeActivityProjection.Build(new[] { Step("older", "coding.write") }.Concat(tail), true, 0);
        Assert.Equal(recent[^1].Key, history[^1].Key);
    }

    [Fact]
    public void VirtualizedAnswerReplacesTheSameSlotAndPreservesAllSourceText()
    {
        var message = JsonSerializer.SerializeToElement(new { role = "assistant", status = "streaming" });
        var steps = Enumerable.Range(0, 150).Select(i => Step("read-" + i, "coding.read"))
            .Append(Step("r", "assistant.reasoning", "running")).ToArray();
        var before = NativeAssistantPage.BuildLogicalBlocks(message, "", steps, [], false, []);
        Assert.Single(before, block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
        var source = new string('x', 10000);
        var after = NativeAssistantPage.BuildLogicalBlocks(message, source, steps, [], false, []);
        var answer = Assert.Single(after, block => block.Key == "source:0");
        Assert.Equal("text", answer.Kind);
        Assert.Equal(source, string.Concat(after.Where(block => block.Kind == "text").Select(block => block.Text)));
        Assert.DoesNotContain(after, block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
    }

    [Fact]
    public void PagingOlderReceiptsKeepsAllExistingCanonicalBlockIds()
    {
        var tail = new[] { Step("last", "coding.read", offset: 20), Step("narration", "assistant.narration", offset: 20),
            Step("thinking", "assistant.reasoning", "running", 40) };
        var before = NativeActivityProjection.Build(tail, true, 40);
        var after = NativeActivityProjection.Build(new[] { Step("older", "coding.write") }.Concat(tail), true, 40);

        Assert.Equal("tool:last", before.Single(entry => Tool(entry) == "coding.read").Key);
        Assert.Equal("narration:narration", before.Single(entry => Tool(entry) == "assistant.narration").Key);
        Assert.Equal(before.Select(entry => entry.Key), after.Where(entry => entry.Step.GetProperty("tool").GetString() != "coding.write").Select(entry => entry.Key));
    }

    [Fact]
    public void HiddenReceiptsCannotSplitCompletedSourceText()
    {
        var result = NativeActivityProjection.Build([
            Step("r1", "assistant.reasoning", offset: 20), Step("p", "assistant.progress", offset: 40),
            Step("c", "context.compact", offset: 60)], false, 100);

        var answer = Assert.Single(result);
        Assert.Equal("source:0", answer.Key);
        Assert.Equal(0, answer.Offset);
        Assert.Equal(100, answer.EndOffset);
    }

    [Fact]
    public void PrefillAfterExistingTextAddsOnlyTransientTailWithoutReplacingHistory()
    {
        var original = NativeActivityProjection.Build([Step("read", "coding.read")], true, 100);
        var waiting = NativeActivityProjection.Build([Step("read", "coding.read")], true, 100, waitingForModel: true);

        Assert.Equal(original.Select(entry => entry.Key), waiting.Take(original.Count).Select(entry => entry.Key));
        Assert.Equal("assistant.activity", Tool(waiting[^1]));
        Assert.Equal("activity:after:text:0", waiting[^1].Key);
        Assert.Null(waiting[^1].ReplacesKey);
        Assert.DoesNotContain(NativeActivityProjection.Build([], false, 100, waitingForModel: true), entry => Tool(entry) == "assistant.activity");
    }
}

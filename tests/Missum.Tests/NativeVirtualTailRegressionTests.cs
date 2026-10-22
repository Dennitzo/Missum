using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Pages;

namespace Missum.Tests;

/// <summary>Long messages must exercise the real virtual projection rather than only compact chat rows.</summary>
public sealed class NativeVirtualTailRegressionTests
{
    private static JsonElement Message(string status = "streaming") =>
        JsonSerializer.SerializeToElement(new { role = "assistant", status });

    private static JsonElement Step(string id, string tool, int offset = 0, string detail = "", string status = "completed") =>
        JsonSerializer.SerializeToElement(new { id, tool, contentOffset = offset, detail, status });

    private static IEnumerable<JsonElement> HistoricalSteps() => Enumerable.Range(0, 150)
        .Select(index => Step("read-" + index, "coding.read"));

    [Fact]
    public void WhitespaceBetweenContinuationAndNarrationDoesNotCreateAnEmptyChatRow()
    {
        var steps = HistoricalSteps().Concat(new[]
        {
            Step("continued", "assistant.continuation"),
            Step("narration", "assistant.narration", 2, "Ich prüfe die erfolgreichen Testergebnisse."),
            Step("reasoning", "assistant.reasoning", 2, status: "running"),
        }).ToArray();

        var blocks = NativeAssistantPage.BuildLogicalBlocks(Message(), "\n\n", steps, [], false, [], waitingForModel: true);

        Assert.Single(blocks, block => block.Kind == "continuationReceipt");
        Assert.DoesNotContain(blocks, block => block.Kind == "text" && string.IsNullOrWhiteSpace(block.Text));
        Assert.Equal("Ich prüfe die erfolgreichen Testergebnisse.",
            Assert.Single(blocks, block => block.Key == "narration:narration").Text);
        Assert.Equal("assistant.activity", blocks[^1].Data.GetProperty("tool").GetString());
    }

    [Fact]
    public void NewlinesAtTheStartOfTheResumedAnswerDoNotBecomeAnEmptyVirtualBlock()
    {
        const string source = "\n\nIch aktualisiere die Etappen und markiere sie als abgeschlossen.\n";
        var steps = HistoricalSteps().Append(Step("continued", "assistant.continuation")).ToArray();
        var blocks = NativeAssistantPage.BuildLogicalBlocks(Message(), source, steps, [], false, [], waitingForModel: true);
        Assert.DoesNotContain(blocks, block => block.Kind == "text" && string.IsNullOrWhiteSpace(block.Text));
        Assert.Contains("Ich aktualisiere", string.Concat(blocks.Where(block => block.Kind == "text").Select(block => block.Text)), StringComparison.Ordinal);
        Assert.Equal("activity:after:continued", blocks.First(block => block.Kind == "text").ReplacesKey);
    }

    [Fact]
    public void MoreThan128ToolsKeepTheCompleteSourceDuringRapidReasoningUpdates()
    {
        var indices = new Dictionary<string, NativeTextBlockIndex>(StringComparer.Ordinal);
        var prepared = new Dictionary<string, NativeStreamingMarkdown.PreparedSource>(StringComparer.Ordinal);
        var source = string.Join("\n\n", Enumerable.Range(0, 180)
            .Select(index => $"Abschnitt {index}: Eine vollständige Antwort mit ihrer ursprünglichen Reihenfolge und allen Wörtern."));

        for (var update = 0; update < 40; update++)
        {
            var current = source + new string('x', update * 11);
            var steps = HistoricalSteps().Append(Step("reasoning", "assistant.reasoning", current.Length,
                detail: new string('r', update * 101), status: "running")).ToArray();
            var blocks = NativeAssistantPage.BuildLogicalBlocks(Message(), current, steps, [], false,
                indices, waitingForModel: true, preparedSources: prepared);

            Assert.Equal(current, string.Concat(blocks.Where(block => block.Kind == "text").Select(block => block.Text)));
            Assert.All(blocks.Where(block => block.Kind == "text"), block =>
            {
                Assert.NotNull(block.Prepared);
                Assert.Equal(block.Text, block.Prepared!.Source);
            });
            Assert.Single(blocks, block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
            Assert.Equal("assistant.activity", blocks[^1].Data.GetProperty("tool").GetString());
        }
    }

    [Fact]
    public void ContextProcessingAfterNarrationNeedsNoReasoningPayload()
    {
        var steps = HistoricalSteps().Append(Step("narration", "assistant.narration",
            detail: "Ich aktualisiere jetzt die Etappen und prüfe die Publikation.")).ToArray();
        var blocks = NativeAssistantPage.BuildLogicalBlocks(Message(), "", steps, [], false, [], waitingForModel: true);

        var narration = blocks.FindIndex(block => block.Key == "narration:narration");
        var pending = blocks.FindIndex(block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
        Assert.Equal(narration + 1, pending);
        Assert.Equal(blocks.Count - 1, pending);
        Assert.DoesNotContain(blocks, block => block.Kind == "text" && block.Text.Contains("PRIVATE", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedVirtualMessageDoesNotKeepTheTransientThinkingTail()
    {
        var steps = HistoricalSteps().Append(Step("reasoning", "assistant.reasoning", 10, status: "running")).ToArray();
        var blocks = NativeAssistantPage.BuildLogicalBlocks(Message("completed"), "Ergebnis.", steps, [], false, [], waitingForModel: true);

        Assert.Equal("Ergebnis.", string.Concat(blocks.Where(block => block.Kind == "text").Select(block => block.Text)));
        Assert.DoesNotContain(blocks, block => block.Kind == "tool" && block.Data.GetProperty("tool").GetString() == "assistant.activity");
    }
}

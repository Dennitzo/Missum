using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeLongSessionProjectionTests
{
    [Fact]
    public async Task CancelledFormulaPreparationDoesNotEnterTheRasterizationWorker()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeMathRenderer.RenderAsync("x+y", false,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void IncrementalEventsKeepOldReceiptsAndRejectDuplicateRevisions()
    {
        var initial = JsonSerializer.SerializeToElement(new { id = "message", content = "Anfang.",
            toolSteps = new[] { new { id = "read-1", tool = "coding.read", outputJson = new string('x', 20_000) } } });
        var state = new NativeAssistantPage.NativeMessageProjection(initial, 0);
        var receipt = state.Steps[0];
        var delta = JsonSerializer.SerializeToElement(new { revision = 1, contentAppend = " Weiter.",
            toolStep = new { id = "read-2", tool = "coding.read" }, toolStepOrdinal = 1, toolStepCount = 2 });
        Assert.True(state.Apply(delta));
        Assert.False(state.Apply(delta));
        Assert.Equal("Anfang. Weiter.", state.Content);
        Assert.Equal(2, state.Steps.Length);
        Assert.True(receipt.Equals(state.Steps[0]));
        Assert.Equal(2, state.TotalSteps);
    }

    [Fact]
    public void ResumedGenerationAcceptsItsNewClockAndRejectsLatePreviousFragments()
    {
        var initial = JsonSerializer.SerializeToElement(new { id = "message", content = "Start" });
        var state = new NativeAssistantPage.NativeMessageProjection(initial, 0);
        state.BeginGeneration("first");
        Assert.True(state.Apply(JsonSerializer.SerializeToElement(new { streamGeneration = "first", revision = 500, contentAppend = " alt" })));
        state.BeginGeneration("resumed");
        Assert.True(state.Apply(JsonSerializer.SerializeToElement(new { streamGeneration = "resumed", revision = 1, contentAppend = " neu" })));
        Assert.False(state.Apply(JsonSerializer.SerializeToElement(new { streamGeneration = "first", revision = 501, contentAppend = " verspätet" })));
        Assert.Equal("Start alt neu", state.Content);
    }

    [Fact]
    public void LiveHeaderWindowCanEvictAndRehydrateWithoutLosingLatestToolUpdates()
    {
        var initial = JsonSerializer.SerializeToElement(new { id = "message", content = "Antwort", toolSteps = Array.Empty<object>() });
        var state = new NativeAssistantPage.NativeMessageProjection(initial, 0);
        for (var index = 0; index < 300; index++)
            state.Apply(JsonSerializer.SerializeToElement(new { revision = index + 1,
                toolStep = new { id = "step-" + index, tool = "coding.command", status = "completed" },
                toolStepOrdinal = index, toolStepCount = index + 1 }));
        Assert.Equal(172, state.TrimHead(128));
        Assert.Equal(128, state.Steps.Length);
        state.Prepend(Enumerable.Range(108, 64).Select(index => JsonSerializer.SerializeToElement(new { id = "step-" + index })));
        Assert.Equal(192, state.Steps.Length);
        Assert.Equal("step-108", state.Steps[0].GetProperty("id").GetString());
        Assert.Equal("step-299", state.Steps[^1].GetProperty("id").GetString());
        Assert.Equal(300, state.TotalSteps);
        Assert.Equal("Antwort", state.Content);
        var terminal = JsonSerializer.SerializeToElement(new { id = "message", content = "Antwort fertig", status = "completed",
            toolSteps = Enumerable.Range(236, 64).Select(index => new { id = "step-" + index }).ToArray() });
        state.Adopt(terminal, 236, preserveEarlier: true);
        Assert.Equal(192, state.Steps.Length);
        Assert.Equal(108, state.FirstOrdinal);
        Assert.Equal("step-108", state.Steps[0].GetProperty("id").GetString());
        Assert.Equal("Antwort fertig", state.Content);
        state.Adopt(JsonSerializer.SerializeToElement(new { id = "message", content = "Neuer Versuch", toolSteps = Array.Empty<object>() }), 0);
        Assert.Empty(state.Steps);
        Assert.Equal(0, state.FirstOrdinal);
        Assert.Equal(0, state.TotalSteps);
    }

    [Fact]
    public void TextBlocksPreserveOriginalLatexFencesAndStableCompletedParagraphs()
    {
        var index = new NativeTextBlockIndex();
        var prefix = "Erster Absatz mit $\\frac{a}{b}$ und Text.\n\n";
        var source = prefix + "Zweiter Absatz.\n\n```lean\ntheorem test : True := by\n  trivial\n```\n";
        index.Update(source);
        var first = index.Blocks[0];
        index.Update(source + "\nErgänzung mit $$x^2+y^2=z^2$$.");
        Assert.Same(first, index.Blocks[0]);
        Assert.Equal(source + "\nErgänzung mit $$x^2+y^2=z^2$$.", string.Concat(index.Blocks.Select(block => block.Source)));
        Assert.Contains(index.Blocks, block => block.Source.Contains("```lean\ntheorem test : True := by\n  trivial\n```", StringComparison.Ordinal));
        foreach (var block in index.Blocks)
            Assert.Equal(block.Source, (source + "\nErgänzung mit $$x^2+y^2=z^2$$.").Substring(block.Start, block.Source.Length));
    }

    [Fact]
    public void ViewportWindowRemainsBoundedForTenThousandBlocks()
    {
        var offsets = Enumerable.Range(0, 10_001).Select(index => index * 44d).ToArray();
        var (first, last) = NativeBlockWindow.Select(offsets, 220_000, 600);
        Assert.InRange(last - first, 67, 71);
        Assert.InRange(first, 4970, 4975);
        Assert.Equal((0, 0), NativeBlockWindow.Select(offsets, -5000, 600));
        Assert.Equal((10_000, 10_000), NativeBlockWindow.Select(offsets, 450_000, 600));
    }

    [Fact]
    public void ReplacingTextInvalidatesOldSourceBlocksWithoutChangingTheReplacement()
    {
        var index = new NativeTextBlockIndex();
        index.Update("Alt.\n\nAlt.");
        var next = "Neu mit $a=b$.\n\nEnde.";
        index.Update(next);
        Assert.Equal(next, string.Concat(index.Blocks.Select(block => block.Source)));
        Assert.Equal(0, index.Blocks[0].Start);
    }

    [Fact]
    public void ExplicitlyExpandedLargeReasoningHasLosslessBoundedLogicalParagraphs()
    {
        var source = string.Join("\n\n", Enumerable.Range(0, 5000).Select(index =>
            $"Beweisschritt {index}: Es gilt $a=b$ unter den festgehaltenen Voraussetzungen."));
        var index = new NativeTextBlockIndex();
        index.Update(source);
        Assert.Equal(5000, index.Blocks.Count);
        Assert.All(index.Blocks, block => Assert.InRange(block.Source.Length, 1, 2048));
        Assert.Equal(source, string.Concat(index.Blocks.Select(block => block.Source)));
    }
}

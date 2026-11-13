using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeMarkdownPreparationTests
{
    [Fact]
    public void ContinuousAppendPublishesFinishedPrefixesAndCoalescesOnlyOneSuccessor()
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        var firstSource = new string('a', 1200) + "\n\n";
        Assert.True(queue.Request(firstSource));
        var first = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        for (var index = 1; index <= 100; index++)
        {
            queue.Request(firstSource + new string('b', index));
            Assert.Null(queue.TryStart());
            Assert.True(queue.CanPublish(first));
        }

        var firstPrepared = NativeStreamingMarkdown.PrepareSource(first.Source);
        Assert.Equal(firstSource, firstPrepared.Source);
        Assert.NotEmpty(firstPrepared.Sections);
        queue.Complete(first);
        var successor = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        Assert.Equal(firstSource + new string('b', 100), successor.Source);
        Assert.Null(queue.TryStart());
        Assert.True(queue.CanPublish(successor));
        queue.Complete(successor);
        Assert.False(queue.HasWork);
        Assert.Null(queue.TryStart());
    }

    [Theory]
    [InlineData("Replacement")]
    [InlineData("First")]
    public void ReplacementOrTruncationInvalidatesPendingPublication(string replacement)
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        queue.Request("First complete paragraph.");
        var first = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        queue.Request(replacement);
        Assert.False(queue.CanPublish(first));
        Assert.Null(queue.TryStart());
        queue.Complete(first);
        var current = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        Assert.Equal(replacement, current.Source);
        Assert.True(queue.CanPublish(current));
    }

    [Fact]
    public void ReturningToAnEarlierPrefixDoesNotReviveAnInvalidatedResult()
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        queue.Request("Original");
        var original = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        queue.Request("Replacement");
        queue.Request("Original and continuation");
        Assert.False(queue.CanPublish(original));
    }

    [Fact]
    public void UnloadInvalidatesWorkAndReloadRestartsOnlyLatestSource()
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        queue.Request("First paragraph");
        var first = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        queue.Suspend();
        queue.Request("First paragraph and latest continuation");
        Assert.False(queue.HasWork);
        Assert.False(queue.CanPublish(first));
        Assert.Null(queue.TryStart());
        queue.Complete(first);
        queue.Resume();
        var resumed = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        Assert.Equal(queue.Source, resumed.Source);
        Assert.True(queue.CanPublish(resumed));
        Assert.False(queue.CanPublish(first));
    }

    [Fact]
    public void PreparedPublicationInvalidatesOlderPrefixAndCancelsQueuedParser()
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        queue.Request("First paragraph");
        var first = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        queue.Request("First paragraph and second paragraph");
        queue.AdoptPrepared("First paragraph and second paragraph");
        Assert.False(queue.CanPublish(first));
        Assert.False(queue.HasWork);
        queue.Complete(first);
        Assert.Null(queue.TryStart());
    }

    [Fact]
    public void SynchronousReplacementDoesNotWaitForObsoleteLargeParse()
    {
        var queue = new NativeStreamingMarkdown.PreparationQueue();
        queue.Request(new string('a', 2000));
        var first = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TryStart());
        queue.Request("Short corrected text");
        var synchronous = Assert.IsType<NativeStreamingMarkdown.PreparationQueue.RequestState>(queue.TakeSynchronous());
        Assert.Equal("Short corrected text", synchronous.Source);
        Assert.True(queue.CanPublish(synchronous));
        Assert.False(queue.CanPublish(first));
        queue.Complete(first);
        Assert.False(queue.HasWork);
        Assert.Null(queue.TryStart());
    }

    [Fact]
    public void PreparedSourceRetainsMathAndCodeWithoutUiConstruction()
    {
        const string source = "Einleitung.\n\n$$x^2+y^2=z^2$$\n\n```python\nprint(1)\n```\n\nErgebnis.";
        var prepared = NativeStreamingMarkdown.PrepareSource(source);
        Assert.Equal(source, prepared.Source);
        Assert.Equal(NativeStreamingMarkdown.ParseSections(source), prepared.Sections);
        Assert.Contains(prepared.Sections, section => section.Kind == NativeStreamingMarkdown.SectionKind.Math);
        Assert.Contains(prepared.Sections, section => section.Kind == NativeStreamingMarkdown.SectionKind.Code);
    }

    [Theory]
    [InlineData("Erster [Link](https://example.org). **Zweiter Satz.** Letzter.", "**Zweiter Satz.**", 0, 13, 13)]
    [InlineData("Gleich. Gleich. Gleich.", "Gleich. Gleich.", 0, 8, 15)]
    [InlineData("- Erster. Zweiter.\r\n\r\n## Anfang. Ende. ##", "Ende.", 1, 8, 5)]
    [InlineData("- Erster. Zweiter.", "Erster.", 0, 3, 7)]
    [InlineData("Vor $x^2$ und weiter. Noch ein Satz.", "Noch ein Satz.", 0, 17, 14)]
    [InlineData("Vor $$x^2$$ danach. Letzter.", "Letzter.", 2, 9, 8)]
    public void MapsSpokenSourceToVisibleText(string source, string sentence, int section, int visibleStart, int visibleLength)
    {
        var prepared = NativeStreamingMarkdown.PrepareSource(source);
        var start = source.LastIndexOf(sentence, StringComparison.Ordinal);
        var range = NativeStreamingMarkdown.ProjectSpeechRange(prepared.Sections[section], prepared.SpeechSections[section]!,
            start, start + sentence.Length);

        Assert.NotNull(range);
        Assert.Equal(visibleStart, range.Value.StartIndex);
        Assert.Equal(visibleLength, range.Value.Length);
    }

    [Theory]
    [InlineData("Eins \\| hier.", 0, 12)]
    [InlineData("**Zweiter Satz.**", 13, 13)]
    public void MapsOnlyTheSpokenPartOfATableCell(string sentence, int visibleStart, int visibleLength)
    {
        const string source = "| Titel | Zweite Spalte |\r\n| --- | --- |\r\n| Eins \\| hier. **Zweiter Satz.** | Kein Treffer. |";
        var prepared = NativeStreamingMarkdown.PrepareSource(source);
        var table = Assert.Single(prepared.Sections);
        var location = Assert.Single(prepared.SpeechSections)!;
        var (markup, row) = location.Rows![1];
        var start = source.IndexOf(sentence, StringComparison.Ordinal);
        var ranges = NativeStreamingMarkdown.ProjectTableRowSpeechRanges(markup, table.MathTokenPrefix, row,
            start, start + sentence.Length).ToArray();

        Assert.Equal(2, ranges.Length);
        var range = Assert.IsType<Microsoft.UI.Xaml.Documents.TextRange>(ranges[0]);
        Assert.Equal(visibleStart, range.StartIndex);
        Assert.Equal(visibleLength, range.Length);
        Assert.Null(ranges[1]);
    }
}

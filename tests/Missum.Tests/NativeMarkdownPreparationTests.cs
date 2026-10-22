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
}

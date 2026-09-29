using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativePromptTimelineStateTests
{
    [Fact]
    public void PreviewNormalizesWhitespaceAndTruncatesAtRequestedLength()
    {
        var preview = NativePromptTimelineState.PreviewText("  Erste Zeile\r\n zweite   Zeile  ", 20);
        Assert.Equal("Erste Zeile zweite …", preview);
        Assert.Equal(20, preview.Length);
    }

    [Fact]
    public void ActivePromptTracksViewportAndSelectsLastAtBottom()
    {
        (string Id, double Offset)[] prompts = [("one", 40), ("two", 420), ("three", 900)];
        Assert.Equal("one", NativePromptTimelineState.SelectActive(prompts, 0, 300, 1000));
        Assert.Equal("two", NativePromptTimelineState.SelectActive(prompts, 360, 300, 1000));
        Assert.Equal("three", NativePromptTimelineState.SelectActive(prompts, 995, 300, 1000));
    }

    [Fact]
    public void EmptyTimelineHasNoActivePrompt() =>
        Assert.Null(NativePromptTimelineState.SelectActive([], 0, 0, 0));
}

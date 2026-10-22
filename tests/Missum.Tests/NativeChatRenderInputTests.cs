using System.Text.Json;
using Missum.App.Services;
using Xunit;

namespace Missum.Tests;

public sealed class NativeChatRenderInputTests
{
    private static JsonElement Step(string id, string tool, string detail = "", int offset = 0) =>
        JsonSerializer.SerializeToElement(new { id, tool, detail, contentOffset = offset, status = "running" });
    private static NativeChatRenderInput Input(string text, params JsonElement[] steps) => new(text, steps, [], "streaming", false, false);

    [Fact]
    public void HiddenReasoningUpdatesDoNotInvalidatePreparedText()
    {
        var first = Input("Vollständige Antwort", Step("r", "assistant.reasoning", "eins", 20));
        var latest = Input("Vollständige Antwort", Step("r", "assistant.reasoning", new string('x', 50000), 20));
        Assert.True(first.Equivalent(latest));
    }

    [Fact]
    public void PreparedAppendPrefixMayPublishWhileMoreTokensArrive()
    {
        var read = Step("read", "file.read");
        var first = Input("Erster Absatz.", read);
        var latest = Input("Erster Absatz. Zweiter Absatz.", read, Step("r", "assistant.reasoning", "neu", 29));
        Assert.True(first.CanPublishBefore(latest));
        Assert.False(first.Equivalent(latest));
    }

    [Theory]
    [InlineData("Erster", "Anderer")]
    [InlineData("Erster Absatz", "Erster")]
    public void ReplacementAndCutDiscardObsoletePreparation(string old, string current) =>
        Assert.False(Input(old).CanPublishBefore(Input(current)));

    [Fact]
    public void RemovedOrMovedReceiptCannotPublish()
    {
        var first = Input("Antwort", Step("read", "file.read", offset: 3));
        Assert.False(first.CanPublishBefore(Input("Antwort")));
        Assert.False(first.CanPublishBefore(Input("Antwort", Step("read", "file.read", offset: 7))));
    }

    [Fact]
    public void PrefillStateIsVisibleEvenWhenSourceRevisionIsUnchanged()
    {
        var first = Input("Antwort");
        Assert.False(first.Equivalent(new("Antwort", [], [], "streaming", false, true)));
    }
}

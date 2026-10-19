using System.Text.Json;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeSteeringPromptTests
{
    [Theory]
    [InlineData("running")]
    [InlineData("completed")]
    public void OnlyTheMatchingRenderableReceiptReplacesTheOptimisticPrompt(string status)
    {
        var pending = JsonSerializer.SerializeToElement(new { steeringInputId = "one", content = "Prüfe\r\ndie Gleichung." });
        JsonElement Receipt(string? detail, string id = "steering-one", string tool = "assistant.steering") =>
            JsonSerializer.SerializeToElement(new { id, tool, status, detail });
        Assert.False(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt(null)));
        Assert.False(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt("  ")));
        Assert.False(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt("Anderer Prompt")));
        Assert.False(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt("Prüfe\ndie Gleichung.", "steering-other")));
        Assert.False(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt("Prüfe\ndie Gleichung.", tool: "coding.read")));
        Assert.True(NativeAssistantPage.HasVisibleSteeringPrompt(pending, Receipt("Prüfe\ndie Gleichung.")));
    }

    [Fact]
    public void VirtualizedSteeringIsAChronologicalUserBubbleWithAStableKey()
    {
        var before = new string('a', 9000) + "\n\n";
        var prompt = "Prüfe jetzt den anderen Ansatz.\n\nErhalte die vollständigen Formeln.";
        var message = JsonSerializer.SerializeToElement(new { role = "assistant", status = "streaming" });
        var steps = Enumerable.Range(0, 130).Select(index => JsonSerializer.SerializeToElement(new
        { id = "read-" + index, tool = "coding.read", status = "completed", contentOffset = 0 }))
            .Append(JsonSerializer.SerializeToElement(new { id = "steering-one", tool = "assistant.steering",
                status = "running", detail = prompt, contentOffset = before.Length })).ToArray();
        var indices = new Dictionary<string, Missum.App.Controls.NativeTextBlockIndex>();
        var blocks = NativeAssistantPage.BuildLogicalBlocks(message, before + "Neue Antwort.", steps, [], false, indices);
        var user = Assert.Single(blocks, block => block.Kind == "steering");
        Assert.Equal("steering:steering-one", user.Key);
        Assert.Equal(prompt, user.Text);
        Assert.Equal(before.Length, user.Start);
        Assert.Equal(before, string.Concat(blocks.TakeWhile(block => block != user).Where(block => block.Kind == "text").Select(block => block.Text)));
        Assert.Equal("Neue Antwort.", string.Concat(blocks.SkipWhile(block => block != user).Where(block => block.Kind == "text").Select(block => block.Text)));
        Assert.DoesNotContain(blocks, block => block.Kind == "tool" && block.Key == "tool:steering-one");
        var updated = NativeAssistantPage.BuildLogicalBlocks(message, before + "Neue Antwort. Weiterer Text.", steps, [], false, indices);
        Assert.Equal(user, Assert.Single(updated, block => block.Kind == "steering"));
    }

    [Fact]
    public void AnEmptyDeferredHeaderDoesNotCreateABlankUserBubble()
    {
        var message = JsonSerializer.SerializeToElement(new { role = "assistant", status = "streaming" });
        var step = JsonSerializer.SerializeToElement(new { id = "steering-one", tool = "assistant.steering", detailsDeferred = true });
        var blocks = NativeAssistantPage.BuildLogicalBlocks(message, "Antwort", [step], [], false, []);
        Assert.DoesNotContain(blocks, block => block.Kind is "steering" or "tool");
    }
}

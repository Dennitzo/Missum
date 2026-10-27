using System.Text.Json;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeSteeringPromptTests
{
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
        // Whitespace-only layout blocks are omitted; the original source anchor
        // above must still retain the exact position after both newline bytes.
        Assert.Equal(before.TrimEnd(), string.Concat(blocks.TakeWhile(block => block != user)
            .Where(block => block.Kind == "text").Select(block => block.Text)).TrimEnd());
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

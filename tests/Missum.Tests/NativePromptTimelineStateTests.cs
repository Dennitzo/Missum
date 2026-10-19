using Missum.App.Pages;
using Missum.Core.Contracts;
using Missum.Core.Models;

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

    [Fact]
    public void UnloadedPromptOffsetsDoNotBecomeFalseScrollTargets()
    {
        Assert.Equal("visible", NativePromptTimelineState.SelectActive([("unloaded", double.NaN), ("visible", 400)], 0, 300, 1000));
        Assert.Null(NativePromptTimelineState.SelectActive([("unloaded", double.NaN)], 0, 300, 1000));
    }

    [Fact]
    public void TargetIsInvalidatedByNewClickScrollOrConversation()
    {
        var target = new NativePromptNavigationTarget("prompt", "session:parent", 7);
        Assert.True(target.IsCurrent("session:parent", 7));
        Assert.False(target.IsCurrent("session:parent", 8));
        Assert.False(target.IsCurrent("session:child", 7));
        Assert.False(target.IsCurrent("other:parent", 7));
    }

    [Fact]
    public async Task PromptIndexAndDirectPageStayBoundedAndSessionScoped()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Timeline");
        var messages = new List<ChatMessage>();
        for (var index = 0; index < 70; index++)
            messages.Add(await chats.AddMessageAsync(session.Id, index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                index == 4 ? new string('p', 10_000) : "Message " + index, MessageStatus.Completed));
        await chats.SaveToolStepAsync(messages[5].Id, new("large-tool", "coding.command", "completed",
            OutputJson: System.Text.Json.JsonSerializer.Serialize(new { stdout = new string('x', 300_000) })));
        var revision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        var latest = (await snapshots.GetPageAsync(session.Id))!;
        Assert.Equal(24, latest.Conversation.Messages.Count);
        Assert.DoesNotContain(latest.Conversation.Messages, message => message.Id == messages[4].Id);

        var prompts = await snapshots.GetPromptsAsync(session.Id);
        Assert.Equal(35, prompts.Count);
        Assert.All(prompts, prompt => Assert.InRange(prompt.Preview.Length, 1, 256));
        Assert.Equal(new string('p', 256), prompts.Single(prompt => prompt.Id == messages[4].Id).Preview);
        var target = (await snapshots.GetPromptPageAsync(session.Id, messages[4].Id))!;
        Assert.Equal(24, target.Conversation.Messages.Count);
        Assert.Equal(messages[4].Id, target.Conversation.Messages[0].Id);
        Assert.All(target.Conversation.Messages.SelectMany(message => message.ToolSteps ?? []),
            step => { Assert.Null(step.OutputJson); Assert.True(step.DetailsDeferred); });
        Assert.Contains((await snapshots.GetPromptPageAsync(session.Id, messages[68].Id))!.Conversation.Messages,
            message => message.Id == messages[68].Id);
        Assert.Null(await snapshots.GetPromptPageAsync(session.Id, messages[5].Id));
        Assert.Null(await snapshots.GetPromptPageAsync(Guid.NewGuid(), messages[4].Id));
        Assert.Empty(await snapshots.GetPromptsAsync(Guid.NewGuid()));
        Assert.Equal(revision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
    }
}

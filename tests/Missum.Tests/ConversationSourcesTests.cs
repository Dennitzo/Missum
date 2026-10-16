using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ConversationSourcesTests
{
    [Fact]
    public async Task SourceIndexFindsOlderNestedReceiptsWithoutReturningToolBodiesOrChangingHistory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Sources");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Original answer", MessageStatus.Completed);
        var nested = JsonSerializer.Serialize(new { results = new[] { new { title = "Primary source", url = "https://example.org/paper", thumbnailUrl = "https://example.org/thumbnail" } }, body = new string('x', 400_000) });
        await chats.SaveToolStepAsync(message.Id, new("web-1", "web.search", "completed",
            InputJson: "{\"url\":\"https://example.org/paper\"}", OutputJson: JsonSerializer.Serialize(new { result = nested })));
        for (var i = 0; i < 30; i++) await chats.AddMessageAsync(session.Id, ChatRole.User, "Later " + i, MessageStatus.Completed);
        var before = JsonSerializer.Serialize(await chats.ListMessagesAsync(session.Id));
        var revision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        Assert.DoesNotContain((await snapshots.GetPageAsync(session.Id))!.Conversation.Messages, item => item.Id == message.Id);

        var source = Assert.Single(await snapshots.GetSourcesAsync(session.Id));

        Assert.Equal("https://example.org/paper", source.Url);
        Assert.Equal("Primary source", source.Title);
        Assert.Equal("web-1", source.Id);
        Assert.True(JsonSerializer.Serialize(source).Length < 1024);
        Assert.Empty(await snapshots.GetSourcesAsync(Guid.NewGuid()));
        Assert.Equal(before, JsonSerializer.Serialize(await chats.ListMessagesAsync(session.Id)));
        Assert.Equal(revision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
    }

    [Fact]
    public async Task ExternalChildSourcesRemainAvailableWithoutLoadingTheChildTab()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("External sources");
        var parent = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Parent", MessageStatus.Completed);
        var at = DateTimeOffset.UtcNow;
        var child = SubagentChatState.Create(new("parent-run", "child-run", "agent", "Read original", "model", RunState.Running), session.Id, at);
        child = child with { AssistantMessage = child.AssistantMessage with
        {
            Content = new string('x', AssistantToolStep.MaximumStructuredJsonCharacters + 4096),
            ToolSteps = [new("child-web", "web.fetch", "completed", OutputJson: "{\"title\":\"Child source\",\"url\":\"https://example.org/child\"}")],
        } };
        await chats.SaveToolStepAsync(parent.Id, new(child.ReceiptId, "subagent", "completed", OutputJson: await child.PersistAsync(environment.Directory)));

        var source = Assert.Single(await environment.Get<IConversationSnapshotRepository>().GetSourcesAsync(session.Id));

        Assert.Equal("Child source", source.Title);
        Assert.Equal("https://example.org/child", source.Url);
        Assert.True(JsonSerializer.Serialize(source).Length < 1024);
    }
}

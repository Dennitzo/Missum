using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ConversationPagingTests
{
    [Fact]
    public async Task CompactContinuationHeaderPreservesActiveTimeWithoutLoadingPayload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Continuation timing");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Answer", MessageStatus.Completed);
        var resumed = message.CreatedAt.AddHours(1);
        await chats.SaveToolStepAsync(message.Id, new("resume", "assistant.continuation", "completed",
            Detail: new string('x', 100_000), StartedAt: resumed, OutputJson: JsonSerializer.Serialize(new
            {
                sessionId = session.Id, messageId = message.Id, localRunId = Guid.NewGuid(),
                priorActiveMilliseconds = 60_000, unusedPayload = new string('y', 100_000),
            })));
        foreach (var read in new[] { 1, 2 })
        {
            var page = (await snapshots.GetPageAsync(session.Id))!;
            var header = Assert.Single(Assert.Single(page.Conversation.Messages).ToolSteps!);
            Assert.True(header.DetailsDeferred);
            Assert.Null(header.Detail);
            Assert.True(header.OutputJson!.Length < 400);
            Assert.Equal(90_000, MissumAiAssistantService.ContinuationActiveMilliseconds(session.Id, message.Id,
                message.CreatedAt, resumed.AddSeconds(30), [header]));
        }
    }

    [Fact]
    public async Task PagesAreBoundedLosslessAndDoNotChangeDurableHistoryOrRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Paged history");
        for (var index = 0; index < 73; index++)
            await chats.AddMessageAsync(session.Id, index % 2 == 0 ? ChatRole.User : ChatRole.Assistant,
                "Original message " + index, MessageStatus.Completed);
        var original = await chats.ListMessagesAsync(session.Id);
        var revision = (await chats.GetSessionAsync(session.Id))!.ConversationRevision;
        var actual = new List<ChatMessage>();
        ConversationCursor? cursor = null;
        do
        {
            var page = (await snapshots.GetPageAsync(session.Id, 12, cursor))!;
            Assert.InRange(page.Conversation.Messages.Count, 1, 12);
            Assert.Equal(73, page.TotalMessages);
            actual.InsertRange(0, page.Conversation.Messages);
            cursor = page.HasMore ? page.Before : null;
        } while (cursor is not null);
        Assert.Equal(original.Select(message => (message.Id, message.Content)), actual.Select(message => (message.Id, message.Content)));
        Assert.Equal(73, actual.Select(message => message.Id).Distinct().Count());
        Assert.Equal(revision, (await chats.GetSessionAsync(session.Id))!.ConversationRevision);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(await chats.ListMessagesAsync(session.Id)));
    }

    [Fact]
    public async Task ClosedToolPayloadIsFetchedOnDemandAndCachedHeadersInvalidateAfterChanges()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Large receipt");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Answer", MessageStatus.Completed);
        var body = new string('x', 600_000);
        var step = new AssistantToolStep("proof", "math.formalProof", "completed", body,
            InputJson: "{\"source\":\"example : 1 = 1 := rfl\"}", OutputJson: JsonSerializer.Serialize(new { stdout = body }),
            UpdatedAt: DateTimeOffset.UtcNow);
        await chats.SaveToolStepAsync(message.Id, step);
        var page = (await snapshots.GetPageAsync(session.Id))!;
        var header = Assert.Single(Assert.Single(page.Conversation.Messages).ToolSteps!);
        Assert.True(header.DetailsDeferred);
        Assert.Null(header.Detail);
        Assert.Null(header.InputJson);
        Assert.Null(header.OutputJson);
        Assert.True(JsonSerializer.Serialize(page).Length < 5000);
        Assert.Equal(step, await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id));
        Assert.Null(await snapshots.GetToolStepAsync(Guid.NewGuid(), message.Id, step.Id));
        await chats.SaveToolStepAsync(message.Id, step with { Detail = "Changed", UpdatedAt = step.UpdatedAt!.Value.AddSeconds(1) });
        var changed = Assert.Single(Assert.Single((await snapshots.GetPageAsync(session.Id))!.Conversation.Messages).ToolSteps!);
        Assert.True(changed.DetailsDeferred);
        Assert.Equal(step.UpdatedAt.Value.AddSeconds(1), changed.UpdatedAt);
        Assert.Equal("Changed", (await snapshots.GetToolStepAsync(session.Id, message.Id, step.Id))!.Detail);
        Assert.Equal(body, (await chats.GetMessageAsync(message.Id))!.ToolSteps![0].OutputJson is { } output
            ? JsonDocument.Parse(output).RootElement.GetProperty("stdout").GetString() : null);
    }

    [Fact]
    public async Task LoadingOlderPageDuringNewMessageDoesNotDuplicateOrSkipCursorBoundary()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var snapshots = environment.Get<IConversationSnapshotRepository>();
        var session = await chats.CreateSessionAsync("Live paging");
        for (var index = 0; index < 30; index++)
            await chats.AddMessageAsync(session.Id, ChatRole.User, index.ToString(System.Globalization.CultureInfo.InvariantCulture), MessageStatus.Completed);
        var latest = (await snapshots.GetPageAsync(session.Id, 10))!;
        var newMessage = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Streaming", MessageStatus.Streaming);
        var older = (await snapshots.GetPageAsync(session.Id, 10, latest.Before))!;
        Assert.Empty(latest.Conversation.Messages.Select(m => m.Id).Intersect(older.Conversation.Messages.Select(m => m.Id)));
        Assert.DoesNotContain(older.Conversation.Messages, m => m.Id == newMessage.Id);
        Assert.Equal(31, (await chats.ListMessagesAsync(session.Id)).Count);
    }
}

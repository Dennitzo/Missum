using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ConversationStreamBufferTests
{
    [Fact]
    public async Task DetachmentFlushesOnlyTheLastAppliedEventAndResumeDoesNotDuplicateThePartialNextEvent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Detach mid-event");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Before", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            null, "detach-test", "run-detach-test", 41, "running", null, null, now, now));
        await using (var buffer = new ConversationStreamBuffer(chats, runs, message, run))
        {
            buffer.SetContent("Before event42", MessageStatus.Streaming, buffer.ToolSteps);
            await buffer.AcknowledgeAsync(run.ServerRunId!, 42, "running", null);
            buffer.SetContent("Before event42 event43", MessageStatus.Streaming, buffer.ToolSteps);
            await buffer.UpsertAsync(new("unacknowledged", "coding.read", "running", ContentOffset: 22));
            // Detachment does not create a fresh acknowledgement from mutable state.
            await buffer.FlushAsync();
        }
        var durable = (await chats.GetMessageAsync(message.Id))!;
        var checkpoint = (await runs.GetAsync(run.Id))!;
        Assert.Equal("Before event42", durable.Content);
        Assert.Empty(durable.ToolSteps!);
        Assert.Equal(42, checkpoint.LastEventId);
        Assert.Equal("running", checkpoint.State);

        await using (var resumed = new ConversationStreamBuffer(chats, runs, durable, checkpoint))
        {
            resumed.SetContent(durable.Content + " event43", MessageStatus.Streaming, resumed.ToolSteps);
            await resumed.AcknowledgeAsync(checkpoint.ServerRunId!, 43, "running", null, force: true);
        }
        Assert.Equal("Before event42 event43", (await chats.GetMessageAsync(message.Id))!.Content);
        Assert.Equal(43, (await runs.GetAsync(run.Id))!.LastEventId);
    }

    [Fact]
    public async Task ExplicitStopDiscardsPartialEventBeforeClosingOnlyDurableToolSteps()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Stop mid-event");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Before", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            null, "stop-test", "run-stop-test", 41, "running", null, null, now, now));
        await using (var buffer = new ConversationStreamBuffer(chats, runs, message, run))
        {
            buffer.SetContent("Before event42", MessageStatus.Streaming, buffer.ToolSteps);
            await buffer.UpsertAsync(new("durable", "coding.read", "running", "Acknowledged detail", ContentOffset: 3));
            await buffer.AcknowledgeAsync(run.ServerRunId!, 42, "running", null);
            buffer.SetContent("Before event42 event43", MessageStatus.Streaming, buffer.ToolSteps);
            await buffer.UpsertAsync(new("durable", "coding.read", "completed", "Unacknowledged result"));
            await buffer.UpsertAsync(new("unacknowledged", "coding.read", "running", ContentOffset: 22));
            await buffer.FlushAsync();
            var durable = (await chats.GetMessageAsync(message.Id))!;
            var checkpoint = (await runs.GetAsync(run.Id))!;
            await buffer.ResetToDurableAsync(durable, checkpoint);
            var step = Assert.Single(buffer.ToolSteps);
            Assert.Equal("running", step.Status);
            Assert.Equal("Acknowledged detail", step.Detail);
            await buffer.UpsertAsync(step with { Status = "cancelled" });
            buffer.SetContent(durable.Content, MessageStatus.Cancelled, buffer.ToolSteps);
            await buffer.AcknowledgeAsync(checkpoint.ServerRunId!, checkpoint.LastEventId, "cancelled", null, force: true);
        }
        var stopped = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal("Before event42", stopped.Content);
        Assert.Equal(MessageStatus.Cancelled, stopped.Status);
        Assert.Equal("cancelled", Assert.Single(stopped.ToolSteps!).Status);
        Assert.Equal("Acknowledged detail", Assert.Single(stopped.ToolSteps!).Detail);
        var stoppedRun = (await runs.GetAsync(run.Id))!;
        Assert.Equal(42, stoppedRun.LastEventId);
        Assert.Equal("cancelled", stoppedRun.State);
    }

    [Fact]
    public async Task WriteBehindNeverCommitsUnacknowledgedTextOrLosesLaterChanges()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Durable stream");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, message.Id,
            null, "stream-test", "run-stream-test", 0, "running", null, null, now, now));
        await using var buffer = new ConversationStreamBuffer(chats, runs, message, run);
        buffer.SetContent("Acknowledged", MessageStatus.Streaming, buffer.ToolSteps);
        await buffer.UpsertAsync(new("step", "coding.read", "running", "Initial"));
        await buffer.AcknowledgeAsync(run.ServerRunId!, 1, "running", null);
        // A timer may flush this captured event while the next SSE event is still applying.
        buffer.SetContent("Acknowledged plus pending", MessageStatus.Streaming, buffer.ToolSteps);
        await buffer.UpsertAsync(new("step", "coding.read", "completed", "Later result"));
        await buffer.FlushAsync();
        var durable = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal("Acknowledged", durable.Content);
        Assert.Equal("Initial", Assert.Single(durable.ToolSteps!).Detail);
        Assert.Equal(1, (await runs.GetAsync(run.Id))!.LastEventId);
        await buffer.AcknowledgeAsync(run.ServerRunId!, 2, "completed", null, force: true);
        durable = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal("Acknowledged plus pending", durable.Content);
        Assert.Equal("Later result", Assert.Single(durable.ToolSteps!).Detail);
        Assert.Equal(2, (await runs.GetAsync(run.Id))!.LastEventId);
    }
}

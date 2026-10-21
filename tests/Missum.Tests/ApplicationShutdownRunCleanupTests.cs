using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ApplicationShutdownRunCleanupTests
{
    [Theory]
    [InlineData(ChatMode.General, null)]
    [InlineData(ChatMode.Coding, PromptTriggerAction.Coding)]
    [InlineData(ChatMode.ClaudeScience, PromptTriggerAction.Coding)]
    public async Task ClosingStopsDurableJobsWithoutLosingHistoryOrEventCursor(ChatMode mode, PromptTriggerAction? action)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Wiederaufnehmbare Aufgabe", mode);
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant,
            "Die Grundlagen und der bisherige Arbeitsstand bleiben erhalten.", MessageStatus.Streaming);
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new MissumAiRunRecord(Guid.NewGuid(), session.Id, message.Id,
            action, "shutdown-test", "server-job", 8765, "waitingForClient", "local-model", null, now, now));

        var cancelled = await MissumAiAssistantService.StopPersistedRunsLocallyAsync(runs, chats,
            applicationShutdown: true);

        Assert.Equal(["server-job"], cancelled);
        Assert.Empty(await runs.ListResumableAsync());
        var storedRun = (await runs.GetAsync(run.Id))!;
        Assert.Equal("cancelled", storedRun.State);
        Assert.Equal("client.run_stopped_on_close", storedRun.ErrorCode);
        Assert.Equal(8765, storedRun.LastEventId);
        var storedMessage = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(message.Content, storedMessage.Content);
        Assert.Equal(MessageStatus.Cancelled, storedMessage.Status);
        Assert.Contains("Beenden von Missum", storedMessage.Error);
        Assert.Empty(await MissumAiAssistantService.StopPersistedRunsLocallyAsync(runs, chats,
            applicationShutdown: true));
    }
}

using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Missum.Tests;

public sealed class SessionPromptTitleTests
{
    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task LatestSubmittedPromptIsPersistedAndRestoredWithoutTouchingHistoryOrDraftTyping(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Alter Titel", mode);
        if (mode != ChatMode.General)
            await chats.SetCodingWorkspacePathAsync(session.Id, environment.Directory, activateCoding: false);
        var original = await chats.AddMessageAsync(session.Id, ChatRole.User, "Der ursprüngliche Auftrag", MessageStatus.Completed);
        var answer = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Die bisherige Antwort", MessageStatus.Completed);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { SelectedChatMode = mode, ActiveSessionId = session.Id });
        var coordinator = Create(environment, settings);
        var events = new List<JsonElement>();
        foreach (var prompt in new[] { "Prüfe nun die Randbedingungen.", "Bitte erkläre die korrigierte Lösung im Detail.", "Fortsetzen" })
        {
            // No AI service: the real admission path persists the name before
            // attempting a model connection, including explicit short prompts.
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.HandleAsync(
                Send(session.Id, prompt), (type, payload, _) =>
                {
                    if (type == "session.grouped") events.Add(JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web));
                    return Task.CompletedTask;
                }));
            Assert.Equal(prompt, (await chats.GetSessionAsync(session.Id))!.Title);
            Assert.Equal(prompt, events[^1].GetProperty("sessions").EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == session.Id).GetProperty("title").GetString());
        }

        await chats.SaveDraftAsync(session.Id, "Ein ungesendeter Entwurf verändert keinen Titel");
        using var reopenedSettings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await reopenedSettings.InitializeAsync();
        var reopened = Create(environment, reopenedSettings);
        var snapshot = JsonSerializer.SerializeToElement(await reopened.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal("Fortsetzen", snapshot.GetProperty("sessions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == session.Id).GetProperty("title").GetString());
        Assert.Equal(original.Content, (await chats.GetMessageAsync(original.Id))!.Content);
        Assert.Equal(answer.Content, (await chats.GetMessageAsync(answer.Id))!.Content);
        Assert.Equal("Ein ungesendeter Entwurf verändert keinen Titel", (await chats.GetSessionAsync(session.Id))!.Draft);
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task QueuedPromptsRenameImmediatelyAndOlderExecutionCannotRollBackTheLatestTitle(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Alter Titel", mode);
        if (mode != ChatMode.General)
            await chats.SetCodingWorkspacePathAsync(session.Id, environment.Directory, activateCoding: false);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { SelectedChatMode = mode, ActiveSessionId = session.Id });
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = scheduler.Enqueue(Guid.NewGuid(), "blocker", async token =>
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return true;
        }, deadline.Token);
        await started.Task.WaitAsync(deadline.Token);
        var coordinator = Create(environment, settings, scheduler);
        var snapshots = new ConcurrentQueue<JsonElement>();
        var errors = new ConcurrentQueue<string>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = 2;
        Task Capture(string type, object payload, string? requestId)
        {
            var data = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
            if (type == "session.grouped") snapshots.Enqueue(data);
            if (type == "host.error")
            {
                errors.Enqueue(data.GetProperty("message").GetString()!);
                if (Interlocked.Decrement(ref remaining) == 0) finished.TrySetResult();
            }
            return Task.CompletedTask;
        }

        await coordinator.HandleAsync(Send(session.Id, "Erster wartender Auftrag"), Capture, deadline.Token);
        Assert.Equal("Erster wartender Auftrag", (await chats.GetSessionAsync(session.Id))!.Title);
        await coordinator.HandleAsync(Send(session.Id, "Neuester wartender Auftrag"), Capture, deadline.Token);
        Assert.Equal("Neuester wartender Auftrag", (await chats.GetSessionAsync(session.Id))!.Title);
        Assert.Equal(2, snapshots.Count);
        Assert.DoesNotContain(snapshots, snapshot => snapshot.TryGetProperty("messages", out _));

        release.SetResult();
        await blocker.Completion.WaitAsync(deadline.Token);
        await finished.Task.WaitAsync(deadline.Token);
        Assert.All(errors, error => Assert.Contains("AI-Clientdienst", error, StringComparison.Ordinal));
        Assert.Equal("Neuester wartender Auftrag", (await chats.GetSessionAsync(session.Id))!.Title);
        Assert.Equal(2, snapshots.Count); // Execution never emits an old title.
    }

    [Fact]
    public void PromptNamesAreBoundedWithoutBreakingWordsOrUnicode()
    {
        Assert.Equal("Bitte prüfe x² = y. Weitere Details", AssistantCoordinator.DerivePromptSessionTitle(
            "  Bitte prüfe **x² = y.**\r\nWeitere  Details "));
        var boundary = new string('a', 159) + "😀z";
        Assert.Equal(new string('a', 159), AssistantCoordinator.DerivePromptSessionTitle(boundary));
        var words = string.Join(' ', Enumerable.Repeat("Beispiel", 30));
        var title = AssistantCoordinator.DerivePromptSessionTitle(words)!;
        Assert.True(title.Length <= 160);
        Assert.EndsWith("Beispiel", title, StringComparison.Ordinal);
    }

    private static AssistantCoordinator Create(TestEnvironment environment, SettingsCoordinator settings,
        ProfileAssistantRunScheduler? scheduler = null) => new(environment.Get<IChatRepository>(),
            environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(), null,
            settings, new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance),
            runScheduler: scheduler);

    private static WebBridgeEnvelope Send(Guid session, string prompt) => new(AssistantProtocol.ProtocolVersion,
        "chat.send", Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(new { sessionId = session, prompt }));
}

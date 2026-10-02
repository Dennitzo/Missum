using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Missum.Tests;

public sealed class AssistantCancellationRoutingTests
{
    public static TheoryData<ChatMode, string, bool> Routes
    {
        get
        {
            var cases = new TheoryData<ChatMode, string, bool>();
            foreach (var mode in new[] { ChatMode.General, ChatMode.Coding, ChatMode.ClaudeScience })
                foreach (var route in new[] { "reattached", "scheduled", "other-session" })
                    foreach (var offline in new[] { false, true }) cases.Add(mode, route, offline);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task StopReachesTheActualSessionAndPersistsCancellationWithOrWithoutAQueueTicket(
        ChatMode mode, string route, bool offline)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Langer Auftrag", mode);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var turn = await chats.AddTurnAsync(session.Id, "Ursprünglichen Auftrag bearbeiten");
        await chats.UpdateMessageAsync(turn.AssistantMessage.Id, "Bereits gespeicherter Fortschritt.", MessageStatus.Streaming);
        await chats.SaveToolStepAsync(turn.AssistantMessage.Id, new("read", "coding.read", "completed", "Vorhandener Beleg"));
        var now = DateTimeOffset.UtcNow;
        var run = await runs.CreateAsync(new(Guid.NewGuid(), session.Id, turn.AssistantMessage.Id,
            mode == ChatMode.Coding ? PromptTriggerAction.Coding : null, Guid.NewGuid().ToString("N"),
            "run-stop", 27, "running", "fixture", null, now, now, WorkspacePath: workspace));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { MissumAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var requests = new ConcurrentQueue<string>();
        var streaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgeCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new Handler(requests, streaming, cancellationRequested, acknowledgeCancellation, offline));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runs, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<MissumAiAssistantService>.Instance);
        await using var scheduler = new ProfileAssistantRunScheduler(NullLogger<ProfileAssistantRunScheduler>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), service, settings, recent, runScheduler: scheduler);
        var updates = new ConcurrentQueue<MissumAiAssistantUpdate>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task Resume(CancellationToken token) => service.ResumePendingAsync(update =>
        {
            updates.Enqueue(update);
            return Task.CompletedTask;
        }, token);
        Task resume;
        if (route == "scheduled")
            resume = scheduler.Enqueue<object?>(session.Id, "scheduled", async token =>
            {
                await Resume(token);
                return null;
            }, deadline.Token).Completion;
        else resume = Resume(deadline.Token);
        await streaming.Task.WaitAsync(deadline.Token);

        if (route == "other-session")
        {
            var foreignSession = Guid.NewGuid();
            var foreignStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var foreignRun = scheduler.Enqueue<object?>(foreignSession, "waiting-behind-resumed-run", async token =>
            {
                foreignStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }, deadline.Token).Completion;
            await foreignStarted.Task.WaitAsync(deadline.Token);
            await coordinator.CancelCurrentAsync(foreignSession).WaitAsync(deadline.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreignRun.WaitAsync(deadline.Token));
            Assert.True(service.IsRunning);
            Assert.Equal(session.Id, service.ActiveSessionId);
            Assert.DoesNotContain(requests, request => request.EndsWith("/cancel", StringComparison.Ordinal));
        }

        var stop = coordinator.CancelCurrentAsync(session.Id);
        await cancellationRequested.Task.WaitAsync(deadline.Token);
        await resume.WaitAsync(deadline.Token);
        // A delayed HTTP acknowledgement cannot keep the finished local run
        // alive or make Stop depend on a subsequent session's completion.
        Assert.False(service.IsRunning);
        acknowledgeCancellation.TrySetResult();
        await stop.WaitAsync(deadline.Token);
        Assert.False(service.IsRunning);
        Assert.Null(service.ActiveRunId);
        Assert.Null(service.ActiveSessionId);
        var stored = (await runs.GetAsync(run.Id))!;
        Assert.Equal(run.LastEventId, stored.LastEventId);
        var message = (await chats.GetMessageAsync(turn.AssistantMessage.Id))!;
        Assert.Equal(MessageStatus.Cancelled, message.Status);
        Assert.Equal("Bereits gespeicherter Fortschritt.", message.Content);
        Assert.Contains(message.ToolSteps!, step => step.Id == "read" && step.Detail == "Vorhandener Beleg");
        Assert.Contains(updates, update => update.Kind == MissumAiAssistantUpdateKind.Cancelled);
        Assert.DoesNotContain(updates, update => update.Kind == MissumAiAssistantUpdateKind.Failed);
        Assert.Single(requests, request => request == "POST /v1/runs/run-stop/cancel");
        // Reopening after an offline stop sends the durable cancellation again;
        // it must never reattach the cancelled message to the inference stream.
        await Resume(deadline.Token).WaitAsync(deadline.Token);
        Assert.Equal("cancelled", (await runs.GetAsync(run.Id))!.State);
        Assert.Empty(await runs.ListResumableAsync());
        Assert.Equal(2, requests.Count(request => request == "POST /v1/runs/run-stop/cancel"));
        Assert.Single(requests, request => request.EndsWith("/events", StringComparison.Ordinal));
        Assert.Equal(JsonSerializer.Serialize(message), JsonSerializer.Serialize(await chats.GetMessageAsync(message.Id)));
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task StopWaitsForCapturedExecutionWhenAnotherSessionAlreadyWaitsToReattach(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var runRepository = new SnapshotRunRepository(runs);
        var first = await chats.CreateSessionAsync("Gestoppte Sitzung", mode);
        var second = await chats.CreateSessionAsync("Andere offene Sitzung", mode);
        var firstTurn = await chats.AddTurnAsync(first.Id, "Erster Auftrag");
        var secondTurn = await chats.AddTurnAsync(second.Id, "Zweiter Auftrag");
        await chats.UpdateMessageAsync(firstTurn.AssistantMessage.Id, "Erster Fortschritt", MessageStatus.Streaming);
        await chats.UpdateMessageAsync(secondTurn.AssistantMessage.Id, "Zweiter Fortschritt", MessageStatus.Streaming);
        await chats.SaveToolStepAsync(firstTurn.AssistantMessage.Id, new("cleanup", "coding.read", "running", "Lesen"));
        var now = DateTimeOffset.UtcNow;
        await runs.CreateAsync(new(Guid.NewGuid(), first.Id, firstTurn.AssistantMessage.Id, null,
            "first-key", "run-first", 0, "running", "fixture", null, now, now));
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { MissumAiServerUrl = "http://127.0.0.1:65000" });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var requests = new ConcurrentQueue<string>();
        var firstStreaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStreaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => new ReattachHandler(requests, firstStreaming, secondStreaming));
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats, environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), runRepository, environment.Get<IClientToolExecutionRepository>(),
            environment.Get<IBinaryObjectStore>(), environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!,
            settings, recent, NullLogger<MissumAiAssistantService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var oldPage = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var newPage = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var original = service.ResumePendingAsync(async update =>
        {
            if (update.ToolStep is { Id: "cleanup", Status: "cancelled" })
            {
                cleanupReached.TrySetResult();
                await releaseCleanup.Task.WaitAsync(deadline.Token);
            }
        }, oldPage.Token);
        await firstStreaming.Task.WaitAsync(deadline.Token);
        // This older saved run is next in the normal updated_at ordering. It is
        // created after the original reader took its own repository snapshot.
        await runs.CreateAsync(new(Guid.NewGuid(), second.Id, secondTurn.AssistantMessage.Id, null,
            "second-key", "run-second", 0, "running", "fixture", null, now.AddDays(-1), now.AddDays(-1)));
        oldPage.Cancel();
        await cleanupReached.Task.WaitAsync(deadline.Token);
        runRepository.ResumableSnapshot = await runs.ListResumableAsync(deadline.Token);
        Assert.Equal(second.Id, runRepository.ResumableSnapshot[0].SessionId);
        // The completed repository task makes this call run synchronously up to
        // its blocked gate acquisition, before Stop registers any waiter.
        var reattached = service.ResumePendingAsync(static _ => Task.CompletedTask, newPage.Token);
        var stop = service.CancelCurrentAndWaitAsync(first.Id, deadline.Token);
        releaseCleanup.TrySetResult();
        try
        {
            await secondStreaming.Task.WaitAsync(deadline.Token);
            await stop.WaitAsync(deadline.Token);
            await original.WaitAsync(deadline.Token);
            Assert.True(service.IsRunning);
            Assert.Equal(second.Id, service.ActiveSessionId);
            Assert.Equal("run-second", service.ActiveRunId);
            Assert.False(reattached.IsCompleted);
            Assert.Equal(MessageStatus.Cancelled, (await chats.GetMessageAsync(firstTurn.AssistantMessage.Id))!.Status);
            Assert.Equal(MessageStatus.Streaming, (await chats.GetMessageAsync(secondTurn.AssistantMessage.Id))!.Status);
            Assert.Single(requests, request => request == "POST /v1/runs/run-first/cancel");
            Assert.DoesNotContain("POST /v1/runs/run-second/cancel", requests);
            // A stale Stop click from the first session cannot stop the second.
            await service.CancelCurrentAndWaitAsync(first.Id, deadline.Token);
            Assert.Equal(second.Id, service.ActiveSessionId);
            Assert.DoesNotContain("POST /v1/runs/run-second/cancel", requests);
        }
        finally
        {
            newPage.Cancel();
            try { await reattached.WaitAsync(deadline.Token); }
            catch (OperationCanceledException) { }
            releaseCleanup.TrySetResult();
        }
    }

    private sealed class SnapshotRunRepository(IMissumAiRunRepository inner) : IMissumAiRunRepository
    {
        public IReadOnlyList<MissumAiRunRecord>? ResumableSnapshot { get; set; }
        public Task<MissumAiRunRecord> CreateAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.CreateAsync(run, cancellationToken);
        public Task<MissumAiRunRecord> BeginAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.BeginAttemptAsync(run, cancellationToken);
        public Task<MissumAiRunRecord> BeginContinuationAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.BeginContinuationAttemptAsync(run, cancellationToken);
        public Task<MissumAiRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<MissumAiRunRecord?> GetByServerRunIdAsync(string serverRunId, CancellationToken cancellationToken = default) => inner.GetByServerRunIdAsync(serverRunId, cancellationToken);
        public Task<MissumAiRunRecord?> GetByAssistantMessageIdAsync(Guid assistantMessageId, CancellationToken cancellationToken = default) => inner.GetByAssistantMessageIdAsync(assistantMessageId, cancellationToken);
        public Task<IReadOnlyList<MissumAiRunRecord>> ListResumableAsync(CancellationToken cancellationToken = default) =>
            ResumableSnapshot is { } snapshot ? Task.FromResult(snapshot) : inner.ListResumableAsync(cancellationToken);
        public Task UpdateAsync(Guid id, string? serverRunId, long lastEventId, string state, string? selectedModel = null,
            string? errorCode = null, CancellationToken cancellationToken = default) => inner.UpdateAsync(id, serverRunId, lastEventId, state, selectedModel, errorCode, cancellationToken);
        public Task RewindEventsAsync(Guid id, long lastEventId, string state, string? errorCode = null,
            CancellationToken cancellationToken = default) => inner.RewindEventsAsync(id, lastEventId, state, errorCode, cancellationToken);
    }

    private sealed class ReattachHandler(ConcurrentQueue<string> requests, TaskCompletionSource firstStreaming,
        TaskCompletionSource secondStreaming) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(request.Method + " " + path);
            if (path == "/v1/runs/run-first/events") firstStreaming.TrySetResult();
            else if (path == "/v1/runs/run-second/events") secondStreaming.TrySetResult();
            else if (path.EndsWith("/cancel", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            else throw new InvalidOperationException("Unexpected request: " + request.Method + " " + path);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("The event fixture only stops through cancellation.");
        }
    }

    private sealed class Handler(ConcurrentQueue<string> requests, TaskCompletionSource streaming,
        TaskCompletionSource cancellationRequested, TaskCompletionSource acknowledgeCancellation, bool offline) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Enqueue(request.Method + " " + path);
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                streaming.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                cancellationRequested.TrySetResult();
                await acknowledgeCancellation.Task.WaitAsync(cancellationToken);
                if (offline && requests.Count(item => item.EndsWith("/cancel", StringComparison.Ordinal)) == 1)
                    throw new HttpRequestException("Gateway beim Stoppen kurzzeitig offline");
                return new(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + path);
        }
    }
}

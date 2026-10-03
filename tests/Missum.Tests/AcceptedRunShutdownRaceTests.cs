using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Tests;

public sealed class AcceptedRunShutdownRaceTests
{
    [Fact]
    public async Task StopDuringAcceptanceKeepsTheServerIdentityAndFinalShutdownRetiresIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var runs = environment.Get<IMissumAiRunRepository>();
        var session = await chats.CreateSessionAsync("Annahme während Beenden", ChatMode.General);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { MissumAiServerUrl = "http://127.0.0.1:65000" });
        var handler = new AcceptanceHandler();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance,
            () => handler);
        var pausedRuns = new PausedAcceptanceRepository(runs);
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        using var service = new MissumAiAssistantService(connection, chats,
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(), pausedRuns,
            environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(),
            environment.Get<IDocumentIngestor>(), null!, null!, null!, null!, null!, settings, recent,
            NullLogger<MissumAiAssistantService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var send = service.SendAsync(session.Id, "Ein Testbild",
            AssistantCoordinator.CreateToolMatch("imageGeneration", "Ein Testbild"),
            static _ => Task.CompletedTask, deadline.Token);
        try
        {
            await pausedRuns.AcceptanceReached.Task.WaitAsync(deadline.Token);
            Assert.Equal(AcceptanceHandler.RunId, service.ActiveRunId);
            Assert.False(pausedRuns.AcceptanceTokenCanBeCancelled);
            var stop = service.CancelCurrentAndWaitAsync(deadline.Token);
            await handler.CancelReached.Task.WaitAsync(deadline.Token);
            Assert.Single(handler.Requests, request => request.EndsWith("/cancel", StringComparison.Ordinal));
            // The first shutdown scan sees the old queued row without its ID.
            await service.StopPersistedRunsForShutdownAsync(deadline.Token);
            pausedRuns.ReleaseAcceptance.TrySetResult();
            var answer = await send.WaitAsync(deadline.Token);
            await stop.WaitAsync(deadline.Token);
            Assert.Equal(MessageStatus.Cancelled, answer.Status);
            var stored = (await runs.GetByAssistantMessageIdAsync(answer.Id))!;
            Assert.Equal(AcceptanceHandler.RunId, stored.ServerRunId);
            // Late acceptance cannot escape the scan after the execution drain.
            await service.StopPersistedRunsForShutdownAsync(deadline.Token);
            Assert.Equal("cancelled", (await runs.GetAsync(stored.Id))!.State);
            Assert.Empty(await runs.ListResumableAsync());
            Assert.DoesNotContain(handler.Requests, request => request.EndsWith("/events", StringComparison.Ordinal));
        }
        finally
        {
            pausedRuns.ReleaseAcceptance.TrySetResult();
            deadline.Cancel();
            try { await send; }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class AcceptanceHandler : HttpMessageHandler
    {
        internal const string RunId = "accepted-during-stop";
        internal ConcurrentQueue<string> Requests { get; } = new();
        internal TaskCompletionSource CancelReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Enqueue(path);
            if (path == "/v1/images/generations")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new RunAccepted(RunId, RunState.Queued,
                        DateTimeOffset.UtcNow, "/v1/runs/" + RunId + "/events"), MissumAiProtocol.CreateJsonOptions()),
                        Encoding.UTF8, "application/json"),
                });
            if (path == "/v1/runs/" + RunId + "/cancel")
            {
                CancelReached.TrySetResult();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            throw new InvalidOperationException("Unerwartete Anfrage: " + path);
        }
    }

    private sealed class PausedAcceptanceRepository(IMissumAiRunRepository inner) : IMissumAiRunRepository
    {
        private int _paused;
        internal TaskCompletionSource AcceptanceReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseAcceptance { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool AcceptanceTokenCanBeCancelled { get; private set; }

        public Task<MissumAiRunRecord> CreateAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.CreateAsync(run, cancellationToken);
        public Task<MissumAiRunRecord> BeginAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.BeginAttemptAsync(run, cancellationToken);
        public Task<MissumAiRunRecord> BeginContinuationAttemptAsync(MissumAiRunRecord run, CancellationToken cancellationToken = default) => inner.BeginContinuationAttemptAsync(run, cancellationToken);
        public Task<MissumAiRunRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);
        public Task<MissumAiRunRecord?> GetByServerRunIdAsync(string serverRunId, CancellationToken cancellationToken = default) => inner.GetByServerRunIdAsync(serverRunId, cancellationToken);
        public Task<MissumAiRunRecord?> GetByAssistantMessageIdAsync(Guid assistantMessageId, CancellationToken cancellationToken = default) => inner.GetByAssistantMessageIdAsync(assistantMessageId, cancellationToken);
        public Task<IReadOnlyList<MissumAiRunRecord>> ListResumableAsync(CancellationToken cancellationToken = default) => inner.ListResumableAsync(cancellationToken);
        public async Task UpdateAsync(Guid id, string? serverRunId, long lastEventId, string state, string? selectedModel = null,
            string? errorCode = null, CancellationToken cancellationToken = default)
        {
            if (serverRunId == AcceptanceHandler.RunId && state == "queued" && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                AcceptanceTokenCanBeCancelled = cancellationToken.CanBeCanceled;
                AcceptanceReached.TrySetResult();
                await ReleaseAcceptance.Task;
            }
            await inner.UpdateAsync(id, serverRunId, lastEventId, state, selectedModel, errorCode, cancellationToken);
        }
        public Task RewindEventsAsync(Guid id, long lastEventId, string state, string? errorCode = null,
            CancellationToken cancellationToken = default) => inner.RewindEventsAsync(id, lastEventId, state, errorCode, cancellationToken);
    }
}

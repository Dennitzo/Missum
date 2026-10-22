using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class HistoricalCompactionReceiptTests
{
    private const string Model = "fixture/history-compaction";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task HistoricalPreparationPersistsOneReceiptWithItsActualTerminalState(string outcome)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Outcome = outcome;
        var source = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var assistant = await fixture.NewAssistantAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Gateway.Cancellation = cancellation;

        if (outcome == "completed")
        {
            var result = await fixture.PrepareAsync(source, assistant, cancellation.Token);
            Assert.True(result.Context.Descriptor.PreparedByAi);
            Assert.False(result.Context.CacheHit);
            Assert.Equal("completed", Assert.Single(result.Message.ToolSteps!).Status);
        }
        else if (outcome == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareAsync(source, assistant, cancellation.Token));
        else
            Assert.Contains("fixture.summary_failed", (await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.PrepareAsync(source, assistant, cancellation.Token))).Message);

        var receipt = Assert.Single((await fixture.Chats.GetMessageAsync(assistant.Id))!.ToolSteps!);
        Assert.Equal("context.compact", receipt.Tool);
        Assert.Equal("history-compaction:" + assistant.Id.ToString("N"), receipt.Id);
        Assert.Equal(outcome, receipt.Status);
        Assert.NotNull(receipt.StartedAt);
        Assert.NotNull(receipt.CompletedAt);
        Assert.True(receipt.CompletedAt >= receipt.StartedAt);
        var updates = fixture.Updates.Where(update => update.ToolStep is not null).ToArray();
        Assert.Equal("running", updates[0].ToolStep!.Status);
        Assert.Null(updates[0].ToolStep!.CompletedAt);
        Assert.Equal(outcome, updates[^1].ToolStep!.Status);
        Assert.All(updates, update => Assert.Equal(receipt.Id, update.ToolStep!.Id));
        Assert.Equal(outcome == "cancelled" ? 1 : 0, fixture.Gateway.CancelledRuns);
        var saved = await fixture.PreparationsAsync();
        if (outcome == "completed") Assert.NotEmpty(saved);
        else
        {
            Assert.Empty(saved);
            Assert.DoesNotContain(updates, update => update.ToolStep!.Status == "completed");
            Assert.Contains("nicht übernommen", receipt.Detail);
        }
        await fixture.AssertHistoryUnchangedAsync(source);
    }

    [Fact]
    public async Task CachedPreparationDoesNotStartInferenceOrCreateAnotherCompletionReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var first = await fixture.PrepareAsync(history, await fixture.NewAssistantAsync());
        Assert.False(first.Context.CacheHit);
        var inferenceCount = fixture.Gateway.CreatedRuns;
        fixture.Updates.Clear();
        var secondAssistant = await fixture.NewAssistantAsync();

        var second = await fixture.PrepareAsync(history, secondAssistant);

        Assert.True(second.Context.CacheHit);
        Assert.True(second.Context.Descriptor.PreparedByAi);
        Assert.Equal(inferenceCount, fixture.Gateway.CreatedRuns);
        Assert.Empty(second.Message.ToolSteps ?? []);
        Assert.Empty((await fixture.Chats.GetMessageAsync(secondAssistant.Id))!.ToolSteps ?? []);
        Assert.DoesNotContain(fixture.Updates, update => update.ToolStep is not null);
        Assert.Equal(JsonSerializer.Serialize(first.Context.Messages, Json), JsonSerializer.Serialize(second.Context.Messages, Json));
        await fixture.AssertHistoryUnchangedAsync(history);
    }

    [Fact]
    public async Task CancellationAfterSavingAValidSummaryStillCannotPublishACompletedReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var assistant = await fixture.NewAssistantAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.ObserveUpdate = update =>
        {
            if (update.Status == "Sitzungsverlauf aufbereitet") cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareAsync(history, assistant, cancellation.Token));

        // The reusable summary was validly saved before cancellation, but this
        // request did not adopt it and must never claim a completed operation.
        Assert.NotEmpty(await fixture.PreparationsAsync());
        Assert.Equal("cancelled", Assert.Single((await fixture.Chats.GetMessageAsync(assistant.Id))!.ToolSteps!).Status);
        Assert.DoesNotContain(fixture.Updates, update => update.ToolStep?.Status == "completed");
        await fixture.AssertHistoryUnchangedAsync(history);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task ChangedHistoryCannotReuseAnOldSuccessfulSummaryOrReportFailedPreparationAsCompleted(string outcome)
    {
        await using var fixture = await Fixture.CreateAsync();
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        await fixture.PrepareAsync(history, await fixture.NewAssistantAsync());
        var cached = await fixture.PreparationsAsync();
        var inferenceCount = fixture.Gateway.CreatedRuns;
        var changed = history[^1];
        await fixture.Chats.UpdateMessageAsync(changed.Id, "MUTATED_HISTORY_NEW_RESULT " + new string('y', 5000), MessageStatus.Completed);
        var newHistory = new List<ChatMessage>();
        foreach (var message in history) newHistory.Add((await fixture.Chats.GetMessageAsync(message.Id))!);
        fixture.Gateway.Outcome = outcome;
        fixture.Updates.Clear();
        using var cancellation = new CancellationTokenSource();
        fixture.Gateway.Cancellation = cancellation;
        var assistant = await fixture.NewAssistantAsync();

        if (outcome == "cancelled")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.PrepareAsync(newHistory, assistant, cancellation.Token));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PrepareAsync(newHistory, assistant, cancellation.Token));

        Assert.Equal(inferenceCount + 1, fixture.Gateway.CreatedRuns);
        Assert.Contains("MUTATED_HISTORY_NEW_RESULT", JsonSerializer.Serialize(fixture.Gateway.Requests[^1], Json));
        Assert.Equal(outcome, Assert.Single((await fixture.Chats.GetMessageAsync(assistant.Id))!.ToolSteps!).Status);
        Assert.DoesNotContain(fixture.Updates, update => update.ToolStep?.Status == "completed");
        Assert.Equal(cached.Select(item => item.CacheKey).Order(), (await fixture.PreparationsAsync()).Select(item => item.CacheKey).Order());
        await fixture.AssertHistoryUnchangedAsync(newHistory);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestEnvironment _environment;
        private readonly SettingsCoordinator _settings;
        private readonly MissumAiConnectionService _connection;
        private readonly MissumAiAssistantService _service;
        private readonly MissumAiClient _client;
        internal IChatRepository Chats { get; }
        internal Guid SessionId { get; }
        internal GatewayHandler Gateway { get; } = new();
        internal List<MissumAiAssistantUpdate> Updates { get; } = [];
        internal Action<MissumAiAssistantUpdate>? ObserveUpdate { get; set; }

        private Fixture(TestEnvironment environment, SettingsCoordinator settings, Guid sessionId)
        {
            _environment = environment;
            _settings = settings;
            SessionId = sessionId;
            Chats = environment.Get<IChatRepository>();
            var documents = environment.Get<IDocumentIngestor>();
            _connection = new(settings, NullLogger<MissumAiConnectionService>.Instance);
            var broker = new LocalToolBroker(_connection, documents, null!, Chats);
            var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
            _service = new(_connection, Chats, environment.Get<IAssistantAttachmentRepository>(),
                environment.Get<IChatArtifactRepository>(), environment.Get<IMissumAiRunRepository>(),
                environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
                new DocumentContextPreparationService(documents), new SessionContextPreparationService(Chats),
                broker, null!, null!, settings, recent, NullLogger<MissumAiAssistantService>.Instance);
            _client = new(new HttpClient(Gateway) { BaseAddress = new("http://fixture.test/") }, ownsHttpClient: true);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var environment = await TestEnvironment.CreateAsync();
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            var chats = environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync("Historical compression receipt");
            await chats.AddMessageAsync(session.Id, ChatRole.User, "Merke dir das bestätigte Ergebnis und die offene Prüfung.", MessageStatus.Completed);
            await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "CONFIRMED_OLD_RESULT " + new string('x', 5000), MessageStatus.Completed);
            return new(environment, settings, session.Id);
        }

        internal Task<ChatMessage> NewAssistantAsync() => Chats.AddMessageAsync(SessionId, ChatRole.Assistant,
            "", MessageStatus.Streaming);

        internal Task<(SessionRunContext Context, ChatMessage Message)> PrepareAsync(IReadOnlyList<ChatMessage> history,
            ChatMessage assistant, CancellationToken token = default)
        {
            var method = typeof(MissumAiAssistantService).GetMethod("PrepareSessionHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task<(SessionRunContext, ChatMessage)>)method.Invoke(_service,
            [
                _client, SessionId, history, "Setze die offene Prüfung fort.", Model, SessionContextProfile.General,
                (int?)2048, (int?)1024, assistant,
                (Func<MissumAiAssistantUpdate, Task>)(update => { Updates.Add(update); ObserveUpdate?.Invoke(update); return Task.CompletedTask; }), token,
            ])!;
        }

        internal Task<IReadOnlyList<SessionContextPreparation>> PreparationsAsync() =>
            Chats.ListSessionContextPreparationsAsync(SessionId, Model, 100, SessionContextProfile.General);

        internal async Task AssertHistoryUnchangedAsync(IReadOnlyList<ChatMessage> history)
        {
            foreach (var original in history)
            {
                var persisted = (await Chats.GetMessageAsync(original.Id))!;
                Assert.Equal(original.Content, persisted.Content);
                Assert.Equal(original.Status, persisted.Status);
                Assert.Equal(original.UpdatedAt, persisted.UpdatedAt);
                Assert.Equal(original.Revision, persisted.Revision);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _service.Dispose();
            _connection.Dispose();
            _settings.Dispose();
            await _environment.DisposeAsync();
        }
    }

    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal string Outcome { get; set; } = "completed";
        internal CancellationTokenSource? Cancellation { get; set; }
        internal int CreatedRuns { get; private set; }
        internal int CancelledRuns { get; private set; }
        internal List<RunRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/runs" && request.Method == HttpMethod.Post)
            {
                var run = JsonSerializer.Deserialize<RunRequest>(await request.Content!.ReadAsStringAsync(cancellationToken), Json)!;
                Assert.Equal(ConversationProfile.ContextPreparation, run.ConversationProfile);
                Assert.Equal(Model, run.PreferredGeneralModelId);
                Requests.Add(run);
                var id = "history-summary-" + ++CreatedRuns;
                return Response(new RunAccepted(id, RunState.Running, DateTimeOffset.UtcNow, "/v1/runs/" + id + "/events"));
            }
            if (path.EndsWith("/cancel", StringComparison.Ordinal))
            {
                CancelledRuns++;
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                if (Outcome == "cancelled")
                {
                    Cancellation!.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var id = "history-summary-" + CreatedRuns;
                var events = Outcome == "failed"
                    ? new[] { Event(1, RunEventTypes.RunFailed, new RunFailedEvent("fixture.summary_failed", "fixture.summary_failed", false)) }
                    : new[] { Event(1, RunEventTypes.TextDelta, new TextDeltaEvent("Bestätigtes Ergebnis gespeichert. Die unabhängige Prüfung bleibt offen.")),
                        Event(2, RunEventTypes.RunCompleted, new RunCompletedEvent(null, Model, 1000, 20)) };
                return new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(item =>
                    "data: " + JsonSerializer.Serialize(item, Json) + "\n\n")), Encoding.UTF8, "text/event-stream") };

                RunEvent Event(long sequence, string type, object data) => new(sequence, id, type,
                    DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(data, Json));
            }
            throw new InvalidOperationException("Unexpected historical preparation fixture request: " + request.RequestUri);
        }

        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
    }
}

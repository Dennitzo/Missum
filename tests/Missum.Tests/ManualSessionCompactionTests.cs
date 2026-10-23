using System.Net;
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

public sealed class ManualSessionCompactionTests
{
    private const string Model = "fixture/manual-compaction";
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();
    private static readonly string[] FailedReceiptStatuses = ["running", "failed", "failed"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompactionPersistsOneTerminalReceiptWithoutChangingHistoryOrAnotherSession(bool changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Changed = changed;
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var otherHistory = await fixture.Chats.ListMessagesAsync(fixture.OtherSessionId);
        var otherSession = await fixture.Chats.GetSessionAsync(fixture.OtherSessionId);
        var draft = (await fixture.Chats.GetSessionAsync(fixture.SessionId))!.Draft;
        var durableStatuses = new List<string>();
        fixture.ObserveUpdate = async update =>
        {
            var persisted = (await fixture.Chats.GetMessageAsync(update.Message.Id))!;
            var step = Assert.Single(persisted.ToolSteps!);
            Assert.Equal(update.ToolStep!.Id, step.Id);
            Assert.Equal(update.ToolStep.Status, step.Status);
            Assert.Equal(update.Message.Status, persisted.Status);
            durableStatuses.Add(step.Status);
        };

        var result = await fixture.CompactAsync();

        var request = Assert.Single(fixture.Gateway.Requests);
        Assert.Equal(fixture.SessionId.ToString("D"), request.SessionId);
        Assert.Equal(Model, request.PreferredModelId);
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal(ChatRole.Assistant, result.Role);
        Assert.Empty(result.Content);
        var receipt = Assert.Single(result.ToolSteps!);
        Assert.Equal("context.compact", receipt.Tool);
        Assert.Equal("manual-compaction:" + result.Id.ToString("N"), receipt.Id);
        Assert.Equal("completed", receipt.Status);
        Assert.Equal(changed ? "Der Kontext wurde verdichtet."
            : MissumAiAssistantService.NoCompactionDetailPrefix + " Noch kein gespeicherter Modellkontext vorhanden.", receipt.Detail);
        Assert.NotNull(receipt.StartedAt);
        Assert.NotNull(receipt.CompletedAt);
        Assert.True(receipt.CompletedAt >= receipt.StartedAt);
        Assert.Equal(new[] { "running", receipt.Status, receipt.Status }, durableStatuses);
        Assert.Equal(new[] { MissumAiAssistantUpdateKind.Started, MissumAiAssistantUpdateKind.Status, MissumAiAssistantUpdateKind.Completed },
            fixture.Updates.Select(update => update.Kind));
        Assert.All(fixture.Updates, update => Assert.Equal(receipt.Id, update.ToolStep!.Id));
        Assert.Equal(340, fixture.Updates[^1].ContextUsed);
        Assert.Equal(changed, fixture.Updates[^1].ContextWasCompacted);
        Assert.False(fixture.Service.IsRunning);
        Assert.Null(fixture.Service.ActiveSessionId);
        var persistedMessages = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        Assert.Equal(history.Count + 1, persistedMessages.Count);
        Assert.Equal(history.Count(message => message.Role == ChatRole.User),
            persistedMessages.Count(message => message.Role == ChatRole.User));
        Assert.DoesNotContain(SessionContextPreparationService.SelectEligibleHistory(persistedMessages),
            message => message.Id == result.Id);
        Assert.Equal(draft, (await fixture.Chats.GetSessionAsync(fixture.SessionId))!.Draft);
        await fixture.AssertHistoryUnchangedAsync(history);
        await fixture.AssertHistoryUnchangedAsync(otherHistory);
        Assert.Equal(otherSession, await fixture.Chats.GetSessionAsync(fixture.OtherSessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CallerCancellationAndNativeStopPersistCancellationAndReleaseTheRunGate(bool nativeStop)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Hold = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = fixture.CompactAsync(token: cancellation.Token);
        await fixture.Gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(fixture.Service.IsRunning);
        Assert.Equal(fixture.SessionId, fixture.Service.ActiveSessionId);

        if (nativeStop)
            await fixture.Service.CancelCurrentAndWaitAsync(fixture.SessionId).WaitAsync(TimeSpan.FromSeconds(10));
        else cancellation.Cancel();
        var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(MessageStatus.Cancelled, cancelled.Status);
        var receipt = Assert.Single((await fixture.Chats.GetMessageAsync(cancelled.Id))!.ToolSteps!);
        Assert.Equal("cancelled", receipt.Status);
        Assert.NotNull(receipt.CompletedAt);
        Assert.DoesNotContain(fixture.Updates, update => update.Kind == MissumAiAssistantUpdateKind.Completed);
        Assert.False(fixture.Service.IsRunning);
        fixture.Gateway.Hold = false;
        var next = await fixture.CompactAsync();
        Assert.Equal(MessageStatus.Completed, next.Status);
        Assert.NotEqual(cancelled.Id, next.Id);
        Assert.Equal(2, fixture.Gateway.Requests.Count);
    }

    [Fact]
    public async Task FirstManualInteractionWaitsForTheColdGatewayBeforePostingCompaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Settings.UpdateAsync(settings => settings with { MissumAiServerUrl = "http://127.0.0.1:8080" });
        fixture.Gateway.StartupFailures = 2;
        var result = await fixture.CompactAsync();
        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal(3, fixture.Gateway.LiveProbes);
        Assert.Single(fixture.Gateway.Requests);
        Assert.Equal("completed", Assert.Single(result.ToolSteps!).Status);
    }

    [Fact]
    public async Task OldGatewayWithoutCompactionRouteShowsActionableVersionErrorAndRetainsHistory()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.MissingEndpoint = true;
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var result = await fixture.CompactAsync();
        Assert.Equal(MessageStatus.Failed, result.Status);
        Assert.Contains("Gateway-Aktualisierung", result.Error);
        Assert.Contains("HTTP 404", result.Error);
        Assert.DoesNotContain("Server returned", result.Error);
        await fixture.AssertHistoryUnchangedAsync(history);
        Assert.False(fixture.Service.IsRunning);
        Assert.Equal("failed", Assert.Single((await fixture.Chats.GetMessageAsync(result.Id))!.ToolSteps!).Status);
    }

    [Fact]
    public async Task HttpFailurePersistsItsErrorAndAllowsAnotherCompaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Fail = true;

        var failed = await fixture.CompactAsync();

        Assert.Equal(MessageStatus.Failed, failed.Status);
        Assert.Contains("503", failed.Error);
        var persisted = (await fixture.Chats.GetMessageAsync(failed.Id))!;
        Assert.Equal(MessageStatus.Failed, persisted.Status);
        Assert.Equal(failed.Error, persisted.Error);
        Assert.Equal("failed", Assert.Single(persisted.ToolSteps!).Status);
        Assert.Equal(FailedReceiptStatuses, fixture.Updates.Select(update => update.ToolStep!.Status));
        Assert.False(fixture.Service.IsRunning);
        fixture.Gateway.Fail = false;
        Assert.Equal(MessageStatus.Completed, (await fixture.CompactAsync()).Status);
    }

    [Fact]
    public async Task DuplicateTriggerDoesNotCreateAnotherMessageOrCompressAnotherSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Hold = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = fixture.CompactAsync(token: cancellation.Token);
        await fixture.Gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var history = await fixture.Chats.ListMessagesAsync(fixture.SessionId);
        var otherHistory = await fixture.Chats.ListMessagesAsync(fixture.OtherSessionId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CompactAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CompactAsync(sessionId: fixture.OtherSessionId));

        Assert.Single(fixture.Gateway.Requests);
        Assert.Equal(history.Count, (await fixture.Chats.ListMessagesAsync(fixture.SessionId)).Count);
        await fixture.AssertHistoryUnchangedAsync(otherHistory);
        cancellation.Cancel();
        Assert.Equal(MessageStatus.Cancelled, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }

    [Fact]
    public async Task MissingSessionDoesNotCreateAReceiptAndDoesNotKeepTheGateLocked()
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CompactAsync(sessionId: Guid.NewGuid()));

        Assert.Empty(fixture.Updates);
        Assert.Empty(fixture.Gateway.Requests);
        Assert.False(fixture.Service.IsRunning);
        Assert.Equal(MessageStatus.Completed, (await fixture.CompactAsync()).Status);
    }

    [Fact]
    public async Task SessionWithoutStoredContextReportsNoOpWithoutResettingUsageMetrics()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Gateway.Changed = false;
        fixture.Gateway.SourceRunId = null;

        var result = await fixture.CompactAsync();

        Assert.Equal(MessageStatus.Completed, result.Status);
        Assert.Equal("completed", Assert.Single(result.ToolSteps!).Status);
        Assert.Equal("Keine Kontextkomprimierung erforderlich", fixture.Updates[^1].Status);
        Assert.Null(fixture.Updates[^1].ContextUsed);
        Assert.False(fixture.Updates[^1].ContextWasCompacted);
    }

    [Fact]
    public async Task ModelAndRequestedContextAreCapturedBeforeWaitingForGatewayConnection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var connectionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.CreateClient = async token =>
        {
            connectionStarted.TrySetResult();
            await releaseConnection.Task.WaitAsync(token);
            return fixture.NewClient();
        };
        var pending = fixture.CompactAsync();
        await connectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.Settings.UpdateAsync(current => current with
        {
            SelectedModel = "fixture/later-model",
            ContextLimitsByModel = new() { [Model] = 262144, ["fixture/later-model"] = 1048576 },
        });
        releaseConnection.TrySetResult();

        Assert.Equal(MessageStatus.Completed, (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        var request = Assert.Single(fixture.Gateway.Requests);
        Assert.Equal(Model, request.PreferredModelId);
        Assert.Equal(65536, request.MaximumContextTokens);
        Assert.Equal("fixture/later-model", fixture.Settings.Current.SelectedModel);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly TestEnvironment _environment;
        private readonly MissumAiConnectionService _connection;
        internal SettingsCoordinator Settings { get; }
        internal MissumAiAssistantService Service { get; }
        internal IChatRepository Chats { get; }
        internal Guid SessionId { get; }
        internal Guid OtherSessionId { get; }
        internal GatewayHandler Gateway { get; } = new();
        internal List<MissumAiAssistantUpdate> Updates { get; } = [];
        internal Func<MissumAiAssistantUpdate, Task>? ObserveUpdate { get; set; }
        internal Func<CancellationToken, Task<MissumAiClient>> CreateClient { get; set; }

        private Fixture(TestEnvironment environment, SettingsCoordinator settings, Guid sessionId, Guid otherSessionId)
        {
            _environment = environment;
            Settings = settings;
            SessionId = sessionId;
            OtherSessionId = otherSessionId;
            Chats = environment.Get<IChatRepository>();
            var documents = environment.Get<IDocumentIngestor>();
            _connection = new(settings, NullLogger<MissumAiConnectionService>.Instance);
            var broker = new LocalToolBroker(_connection, documents, null!, Chats);
            var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
            Service = new(_connection, Chats, environment.Get<IAssistantAttachmentRepository>(),
                environment.Get<IChatArtifactRepository>(), environment.Get<IMissumAiRunRepository>(),
                environment.Get<IClientToolExecutionRepository>(), environment.Get<IBinaryObjectStore>(), documents,
                new DocumentContextPreparationService(documents), new SessionContextPreparationService(Chats),
                broker, null!, null!, settings, recent, NullLogger<MissumAiAssistantService>.Instance);
            CreateClient = _ => Task.FromResult(NewClient());
        }

        internal static async Task<Fixture> CreateAsync()
        {
            ModelContextProfiles.RegisterMaximum(Model, 131072);
            var environment = await TestEnvironment.CreateAsync();
            var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
            await settings.InitializeAsync();
            await settings.UpdateAsync(current => current with { SelectedModel = Model,
                ContextLimitsByModel = new() { [Model] = 65536 } });
            var chats = environment.Get<IChatRepository>();
            var session = await chats.CreateSessionAsync("Aktuelle Sitzung", ChatMode.ClaudeScience);
            var other = await chats.CreateSessionAsync("Andere Sitzung", ChatMode.Coding);
            await chats.SaveDraftAsync(session.Id, "Noch nicht gesendeter Prompt");
            await chats.SaveDraftAsync(other.Id, "Anderer Entwurf");
            await chats.AddMessageAsync(session.Id, ChatRole.User, "Untersuche $F=ma$ unverändert.", MessageStatus.Completed);
            var assistant = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Bestätigte Herleitung: $F=ma$.", MessageStatus.Completed);
            await chats.SaveToolStepDeltaAsync(assistant.Id, new("existing-read", "coding.read", "completed", "Originalinhalt"));
            await chats.AddMessageAsync(other.Id, ChatRole.User, "Diese Sitzung darf nicht verdichtet werden.", MessageStatus.Completed);
            await chats.AddMessageAsync(other.Id, ChatRole.Assistant, "Anderer Verlauf.", MessageStatus.Completed);
            return new(environment, settings, session.Id, other.Id);
        }

        internal MissumAiClient NewClient() => new(new HttpClient(Gateway, disposeHandler: false)
            { BaseAddress = new("http://fixture.test/") }, ownsHttpClient: true);

        internal Task<ChatMessage> CompactAsync(Guid? sessionId = null, CancellationToken token = default) =>
            Service.CompactSessionContextAsync(sessionId ?? SessionId, async update =>
            {
                Updates.Add(update);
                if (ObserveUpdate is not null) await ObserveUpdate(update);
            }, CreateClient, token);

        internal async Task AssertHistoryUnchangedAsync(IReadOnlyList<ChatMessage> history)
        {
            foreach (var original in history)
            {
                var persisted = (await Chats.GetMessageAsync(original.Id))!;
                Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(persisted, Json));
            }
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            Gateway.Dispose();
            _connection.Dispose();
            Settings.Dispose();
            await _environment.DisposeAsync();
        }
    }

    private sealed class GatewayHandler : HttpMessageHandler
    {
        internal bool Hold { get; set; }
        internal bool Fail { get; set; }
        internal bool MissingEndpoint { get; set; }
        internal int StartupFailures { get; set; }
        internal int LiveProbes { get; private set; }
        internal bool Changed { get; set; } = true;
        internal string? SourceRunId { get; set; } = "fixture-source-run";
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<SessionContextCompactionRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/health/live")
            {
                LiveProbes++;
                return LiveProbes <= StartupFailures
                    ? new(HttpStatusCode.BadGateway) { Content = new StringContent("") }
                    : new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                        new HealthSnapshot("live", MissumAiProtocol.Version, DateTimeOffset.UtcNow), Json), Encoding.UTF8, "application/json") };
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/sessions/context/compact", request.RequestUri!.AbsolutePath);
            Requests.Add(JsonSerializer.Deserialize<SessionContextCompactionRequest>(
                await request.Content!.ReadAsStringAsync(cancellationToken), Json)!);
            Started.TrySetResult();
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (MissingEndpoint) return new(HttpStatusCode.NotFound) { Content = new StringContent("") };
            if (Fail) return new(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            var result = new SessionContextCompactionResponse(SourceRunId, Changed, 1800, 340, 1,
                Changed ? "Der Kontext wurde verdichtet." : "Noch kein gespeicherter Modellkontext vorhanden.");
            return new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(result, Json), Encoding.UTF8, "application/json") };
        }
    }
}

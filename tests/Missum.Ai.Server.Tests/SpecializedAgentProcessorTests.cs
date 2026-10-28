using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Agents;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class SpecializedAgentProcessorTests
{
    private const string Goal = "Finde eine verallgemeinerte Gleichung und prüfe sie anhand der offenen Fälle.";
    private const string FollowUp = "Prüfe als nächste Etappe den noch offenen Grenzfall B und dokumentiere den unabhängigen Vergleich.";
    private const string FirstMainResult = "Teilmodell A ist geprüft. Die Verallgemeinerung auf Grenzfall B bleibt offen.";
    private const string FinalMainResult = "Grenzfall B ist ebenfalls geprüft. Die Verallgemeinerung und ihre Voraussetzungen sind dokumentiert.";
    private const string ReviewMarker = "[MISSUM_SPECIALIZED_AGENT_REVIEW]";
    private const string Session = "specialized-agent-session";
    private static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();

    [Fact]
    public async Task LoopReviewsThenHandsOffOnceAndCompletesOnlyAfterTheSameModelAcceptsTheGoal()
    {
        using var harness = new Harness();
        harness.Handler.Decisions.Enqueue(ContinueDecision());
        harness.Handler.Decisions.Enqueue(CompletedDecision());
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await harness.Processor.ProcessAsync(run, deadline.Token);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Equal(2, harness.Handler.MainBodies.Count);
        Assert.Equal(2, harness.Handler.ReviewBodies.Count);
        Assert.Equal(1, harness.Handler.ModelLoads);
        Assert.Contains(ReviewMarker, Messages(harness.Handler.ReviewBodies[0]).Last(message => message.Role == "user").Content!);
        Assert.All(harness.Handler.GenerationBodies, body => Assert.Equal(NativeHandler.ModelId, body.GetProperty("model").GetString()));
        Assert.All(harness.Handler.ReviewBodies, body =>
        {
            Assert.Equal("none", body.GetProperty("tool_choice").GetString());
            Assert.Equal(harness.Handler.MainBodies[0].GetProperty("tools").GetRawText(), body.GetProperty("tools").GetRawText());
            Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        });
        Assert.DoesNotContain(harness.Handler.Paths, path => path.StartsWith("/agents/", StringComparison.Ordinal)
            || path == "/models/unload");
        Assert.Empty(await harness.Repository.GetSubagentRunsAsync(run));
        var cacheKey = ModelRuntimeClient.BuildSessionCacheKey(Session, "coding", harness.Workspace);
        Assert.All(harness.Handler.CacheBodies, body => Assert.Equal(cacheKey, body.GetProperty("sessionKey").GetString()));
        Assert.All(harness.Handler.CacheBodies.Where(body => body.GetProperty("sessionId").ValueKind == JsonValueKind.String),
            body => Assert.Equal(Session, body.GetProperty("sessionId").GetString()));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var accepted = Assert.Single(events, item => item.Type == RunSteeringEventTypes.Accepted).Data.Deserialize<RunSteeringEvent>(JsonOptions)!;
        Assert.Equal(SpecializedAgentProfiles.Loop, accepted.AgentProfile);
        Assert.False(string.IsNullOrWhiteSpace(accepted.AgentId));
        Assert.Contains(FollowUp, accepted.Text);
        var applied = Assert.Single(events, item => item.Type == RunSteeringEventTypes.Applied).Data.Deserialize<RunSteeringEvent>(JsonOptions)!;
        Assert.Equal(accepted.InputId, applied.InputId);
        Assert.Equal(accepted.AgentId, applied.AgentId);
        Assert.Equal(SpecializedAgentProfiles.Loop, applied.AgentProfile);
        Assert.Single(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.Equal(2, events.Count(item => item.Type == RunEventTypes.SubagentStarted));
        Assert.Equal(2, events.Count(item => item.Type == RunEventTypes.SubagentCompleted));
        var visible = CodingTextReconciler.Project(events);
        Assert.Contains(FirstMainResult, visible);
        Assert.Contains(FinalMainResult, visible);
        Assert.DoesNotContain("\"outcome\"", visible);
        Assert.DoesNotContain(ReviewMarker, visible);
        Assert.Contains(Messages(harness.Handler.MainBodies[1]), message => message.Role == "user" && message.Content!.Contains(FollowUp, StringComparison.Ordinal));

        var next = (await harness.Repository.CreateAsync(harness.Request, null)).Snapshot.RunId;
        var archived = await harness.Repository.GetCodingSessionContextWithSourceAsync(next, harness.Request);
        Assert.NotNull(archived);
        Assert.Equal(Goal, archived.Value.Checkpoint.WorkingState!.OriginalTask);
        Assert.Equal(Goal, archived.Value.Checkpoint.SpecializedAgent!.OriginalGoal);
    }

    [Fact]
    public async Task MainRunWithoutExplicitOptInDoesNotInvokeASpecializedAgent()
    {
        using var harness = new Harness(enabled: false);
        var run = await harness.CreateAsync();

        await harness.Processor.ProcessAsync(run, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Single(harness.Handler.MainBodies);
        Assert.Empty(harness.Handler.ReviewBodies);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.SubagentStarted
            || item.Type == RunSteeringEventTypes.Accepted);
    }

    [Fact]
    public async Task RepeatedClaimOfSuccessCannotPassAnOpenSavedPlan()
    {
        using var harness = new Harness();
        harness.Handler.DefaultDecision = CompletedDecision();
        var run = await harness.CreateAsync();
        var state = CodingWorkingState.Create(Goal) with
        {
            Plan = [new("open-check", "Unabhängige Prüfung des Grenzfalls", "pending", [])],
        };
        await harness.Repository.SaveCheckpointAsync(run, new([new("user", Goal)], 0, 0, 0, 0,
            WorkingState: state, SelectedModelId: NativeHandler.ModelId));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var failure = await Record.ExceptionAsync(() => harness.Processor.ProcessAsync(run, deadline.Token));

        Assert.Equal("specialized_agent.invalid_decision", Assert.IsType<SpecializedAgentException>(failure).Code);
        Assert.Equal(3, harness.Handler.ReviewBodies.Count);
        Assert.Single(harness.Handler.MainBodies);
        Assert.NotEqual(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunEventTypes.RunCompleted
            || item.Type == RunSteeringEventTypes.Accepted);
        Assert.Equal("pending", Assert.Single((await harness.Repository.GetCheckpointAsync(run))!.WorkingState!.Plan).Status);
    }

    [Fact]
    public async Task StopDuringReviewDoesNotProduceANewMainPrompt()
    {
        using var harness = new Harness();
        harness.Handler.BlockFirstReview = true;
        var run = await harness.CreateAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = harness.Processor.ProcessAsync(run, stop.Token);
        await harness.Handler.ReviewEntered.Task.WaitAsync(stop.Token);

        await harness.Repository.CancelAsync(run);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(harness.Handler.FirstReviewCancelled);
        Assert.Equal(RunState.Cancelled, (await harness.Repository.GetAsync(run))!.State);
        Assert.Single(harness.Handler.MainBodies);
        Assert.Single(harness.Handler.ReviewBodies);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted
            || item.Type == RunEventTypes.RunCompleted);
        Assert.NotNull((await harness.Repository.GetCheckpointAsync(run))!.SpecializedAgent);
    }

    [Fact]
    public async Task ARealUserSteeringInputInvalidatesTheInFlightReviewBeforeAutomaticHandoff()
    {
        using var harness = new Harness();
        harness.Handler.BlockFirstReview = true;
        harness.Handler.DefaultDecision = CompletedDecision();
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = harness.Processor.ProcessAsync(run, deadline.Token);
        await harness.Handler.ReviewEntered.Task.WaitAsync(deadline.Token);
        const string userCorrection = "NUTZER_KORREKTUR: Verwende für Grenzfall B ausdrücklich die Randbedingung rho gleich null.";

        await harness.Repository.AcceptSteeringAsync(run, new(Session, "human-correction", userCorrection), deadline.Token);
        await processing.WaitAsync(deadline.Token);

        Assert.True(harness.Handler.FirstReviewCancelled);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Equal(2, harness.Handler.MainBodies.Count);
        Assert.Equal(2, harness.Handler.ReviewBodies.Count);
        Assert.Contains(Messages(harness.Handler.MainBodies[1]), message => message.Role == "user" && message.Content == userCorrection);
        Assert.Contains(Messages(harness.Handler.ReviewBodies[1]), message => message.Content?.Contains(userCorrection, StringComparison.Ordinal) == true);
        var accepted = Assert.Single(await harness.Repository.GetEventsAfterAsync(run, 0), item => item.Type == RunSteeringEventTypes.Accepted)
            .Data.Deserialize<RunSteeringEvent>(JsonOptions)!;
        Assert.Null(accepted.AgentProfile);
        Assert.Equal("human-correction", accepted.InputId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryOfAPersistedHandoffReusesItsExactPromptAndInputIdentity(bool alreadyAccepted)
    {
        using var harness = new Harness();
        harness.Handler.DefaultDecision = CompletedDecision();
        var run = await harness.CreateAsync();
        var agentId = "specialized-loop-" + run;
        const string invocation = "specialized-recovered-invocation";
        const string inputId = "specialized-recovered-followup";
        var decision = ContinueDecision();
        var saved = new SpecializedAgentCheckpoint(SpecializedAgentProfiles.Loop, "1", Goal, agentId,
            Phase: "handoff", InvocationId: invocation, LastMainContent: FirstMainResult,
            Decision: decision, FollowUpInputId: inputId);
        await harness.Repository.SaveCheckpointAsync(run, new(
            [new("system", "Gespeicherter unveränderter Kontext."), new("user", Goal), new("assistant", FirstMainResult),
             new("user", ReviewMarker + "\n" + Goal), new("assistant", JsonSerializer.Serialize(decision, JsonOptions))],
            1, 0, 0, 0, WorkingState: CodingWorkingState.Create(Goal), SelectedModelId: NativeHandler.ModelId,
            PreserveSessionPromptPrefix: true, SpecializedAgent: saved));
        if (alreadyAccepted)
            Assert.NotNull(await harness.Repository.TryAcceptSpecializedSteeringAsync(run,
                new(Session, inputId, "Loop-Agent – Folgeauftrag:\n\n" + FollowUp), agentId, SpecializedAgentProfiles.Loop));
        Assert.Contains(run, await harness.Repository.RecoverAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await harness.Processor.ProcessAsync(run, deadline.Token);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Single(harness.Handler.MainBodies);
        Assert.Single(harness.Handler.ReviewBodies); // The already persisted decision was not generated again.
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var accepted = Assert.Single(events, item => item.Type == RunSteeringEventTypes.Accepted).Data.Deserialize<RunSteeringEvent>(JsonOptions)!;
        Assert.Equal(inputId, accepted.InputId);
        Assert.Equal("Loop-Agent – Folgeauftrag:\n\n" + FollowUp, accepted.Text);
        Assert.Equal(agentId, accepted.AgentId);
        Assert.Single(events, item => item.Type == RunSteeringEventTypes.Applied);
        Assert.Equal(1, harness.Handler.ModelLoads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryOfAnUnfinishedReviewRestoresOnlyItsMainModelWhenNeeded(bool modelStillResident)
    {
        using var harness = new Harness();
        harness.Handler.IsLoaded = modelStillResident;
        var run = await harness.CreateAsync();
        var saved = new SpecializedAgentCheckpoint(SpecializedAgentProfiles.Loop, "1", Goal, "specialized-loop-" + run,
            Phase: "review", InvocationId: "specialized-interrupted-review", LastMainContent: FirstMainResult);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(FirstMainResult));
        await harness.Repository.SaveCheckpointAsync(run, new(
            [new("system", "Gespeicherter unveränderter Kontext."), new("user", Goal), new("assistant", FirstMainResult),
             new("user", ReviewMarker + "\n" + Goal)], 1, 0, 0, 0,
            WorkingState: CodingWorkingState.Create(Goal), SelectedModelId: NativeHandler.ModelId,
            PreserveSessionPromptPrefix: true, SpecializedAgent: saved));
        Assert.Contains(run, await harness.Repository.RecoverAsync());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await harness.Processor.ProcessAsync(run, deadline.Token);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(run))!.State);
        Assert.Empty(harness.Handler.MainBodies); // The saved main answer is never regenerated.
        var review = Assert.Single(harness.Handler.ReviewBodies);
        Assert.Equal(NativeHandler.ModelId, review.GetProperty("model").GetString());
        Assert.Equal("none", review.GetProperty("tool_choice").GetString());
        Assert.Equal(modelStillResident ? 0 : 1, harness.Handler.ModelLoads);
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Equal(FirstMainResult, CodingTextReconciler.Project(events));
        Assert.Equal(modelStillResident ? 0 : 2, events.Count(item => item.Type == RunEventTypes.ModelLoading));
        Assert.DoesNotContain(events, item => item.Type == RunSteeringEventTypes.Accepted);
        Assert.DoesNotContain(harness.Handler.Paths, path => path.StartsWith("/agents/", StringComparison.Ordinal)
            || path == "/models/unload");
        Assert.Empty(await harness.Repository.GetSubagentRunsAsync(run));
    }

    private static SpecializedAgentDecision ContinueDecision() => new("continue", "Der Grenzfall B ist noch offen.",
        [new("Verallgemeinerung", false, "Bisher wurde nur Teilmodell A geprüft.")], ["Grenzfall B unabhängig prüfen"], FollowUp);

    private static SpecializedAgentDecision CompletedDecision() => new("completed", "Alle angeforderten Punkte sind anhand des Arbeitsstands geprüft.",
        [new("Verallgemeinerung", true, "Herleitung und unabhängiger Vergleich beider Grenzfälle sind dokumentiert.")], []);

    private static LmChatMessage[] Messages(JsonElement body) => body.GetProperty("messages").EnumerateArray().Select(message =>
        new LmChatMessage(message.GetProperty("role").GetString()!, message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String ? content.GetString() : null)).ToArray();

    private sealed class Harness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal NativeHandler Handler { get; } = new();
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        internal RunRequest Request { get; }
        internal string Workspace { get; }

        internal Harness(bool enabled = true)
        {
            _context.Options.ModelRuntimeUri = new("http://native.test:8081");
            Workspace = Path.Combine(_context.Root, "workspace");
            Directory.CreateDirectory(Workspace);
            _http = new(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Request = new(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Text: Goal)])],
                ClientCapabilities: ["coding"], AllowedServerTools: [], SessionId: Session,
                PreferredGeneralModelId: NativeHandler.ModelId, PreferredCodingModelId: NativeHandler.ModelId,
                CodingOptions: new(WorkspacePath: Workspace, ContinueSessionContext: true),
                Limits: new(TimeoutSeconds: 0), SpecializedAgent: enabled ? new(SpecializedAgentProfiles.Loop, Goal) : null);
        }

        internal async Task<string> CreateAsync() => (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
        public void Dispose() { _services.Dispose(); _http.Dispose(); _context.Dispose(); }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        internal const string ModelId = "coding/qwen3.8-27b~specialized-agent-test";
        private static readonly string[] Tags = ["missum-context-train:32768"];
        internal bool IsLoaded { get; set; }
        internal int ModelLoads { get; private set; }
        internal bool BlockFirstReview { get; set; }
        internal bool FirstReviewCancelled { get; private set; }
        internal SpecializedAgentDecision DefaultDecision { get; set; } = CompletedDecision();
        internal Queue<SpecializedAgentDecision> Decisions { get; } = new();
        internal List<string> Paths { get; } = [];
        internal List<JsonElement> GenerationBodies { get; } = [];
        internal List<JsonElement> MainBodies { get; } = [];
        internal List<JsonElement> ReviewBodies { get; } = [];
        internal List<JsonElement> CacheBodies { get; } = [];
        internal TaskCompletionSource ReviewEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/v1/models") return Json(new { data = new[]
            {
                new { id = ModelId, tags = Tags, status = new { value = IsLoaded ? "loaded" : "unloaded" } },
            } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/models/load") { IsLoaded = true; ModelLoads++; return Json(new { success = true }); }
            if (path.StartsWith("/sessions/", StringComparison.Ordinal))
            {
                using var cache = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                CacheBodies.Add(cache.RootElement.Clone());
                return Json(new { status = path == "/sessions/save" ? "saved" : "resident" });
            }
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 128 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native endpoint: " + path);
            if (!IsLoaded) throw new InvalidOperationException("The selected primary model must be resident before inference.");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = json.RootElement.Clone();
            GenerationBodies.Add(body);
            // A format-repair turn appends its own user instruction after the
            // original marker. Classify it by the review protocol, while the
            // initial marker remains checked in the first review request.
            var isReview = body.TryGetProperty("response_format", out var format)
                && format.GetProperty("type").GetString() == "json_schema"
                && body.GetProperty("tool_choice").GetString() == "none";
            string content;
            if (isReview)
            {
                ReviewBodies.Add(body);
                if (BlockFirstReview && ReviewBodies.Count == 1)
                {
                    ReviewEntered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) { FirstReviewCancelled = true; throw; }
                }
                content = JsonSerializer.Serialize(Decisions.Count > 0 ? Decisions.Dequeue() : DefaultDecision, JsonOptions);
            }
            else
            {
                MainBodies.Add(body);
                content = MainBodies.Count == 1 ? FirstMainResult : FinalMainResult;
            }
            return Json(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 128, completion_tokens = 8 } });
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}

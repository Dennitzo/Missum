using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceInitialPlanTests
{
    private static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();
    private const string Introduction = "Ich prüfe die Herleitung in der Publikation und gliedere die Gegenprüfung in einem Arbeitsplan.";
    private static JsonElement J(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private static readonly LmToolCall Plan = new("plan", "coding.updatePlan", J("""
        {"steps":[{"id":"s1","title":"Herleitung prüfen","status":"in_progress"}],
         "scienceStages":[{"id":"s1","question":"Gilt die Herleitung?","strategy":"Unabhängige Gegenprobe"}]}
        """));
    private static readonly LmToolCall Read = new("read", "coding.read", J("""{"path":"Publikation.md"}"""));

    [Fact]
    public void EmptyPlanIsNotAFinishedScienceRunAndAdministrativeFieldsAreNotAPlan()
    {
        var state = CodingWorkingState.Create("Prüfe die Gleichung") with { Science = new(ScienceSkill.Profile, []) };
        Assert.Contains("coding.updatePlan", ScienceCompletionGuard.RepairPrompt(state));
        Assert.Throws<ArgumentException>(() => ScienceInitialPlanGuard.ValidateInitialUpdate(J("""{"phase":"planning","nextStep":"Lesen"}""")));
        Assert.Throws<ArgumentException>(() => ScienceInitialPlanGuard.ValidateInitialUpdate(J("""{"steps":[]}""")));
        ScienceInitialPlanGuard.ValidateInitialUpdate(Plan.Arguments);
        Assert.False(ScienceInitialPlanGuard.IsPlanOnly([Plan, Read]));
        Assert.True(ScienceInitialPlanGuard.IsPlanOnly([Plan]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("compact-v1")]
    public async Task FirstValidPlanPrecedesClientActionsAndMixedOrAdministrativeCallsCannotOpenTheGate(string? contextProfile)
    {
        using var harness = new Harness(contextProfile);
        harness.Handler.Responses.Enqueue([Plan, Read]);
        harness.Handler.Responses.Enqueue([new("admin", "coding.updatePlan", J("""{"phase":"planning","nextStep":"Publikation lesen"}"""))]);
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var proposals = events.Where(item => item.Type == RunEventTypes.ClientToolProposed).ToArray();
        Assert.Single(proposals);
        var completedPlans = events.Where(item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan").ToArray();
        Assert.Equal(2, completedPlans.Length);
        Assert.False(completedPlans[0].Data.GetProperty("success").GetBoolean());
        var successful = Assert.Single(completedPlans, item => item.Data.GetProperty("success").GetBoolean());
        Assert.True(successful.Id < proposals[0].Id);
        Assert.Single(successful.Data.GetProperty("result").GetProperty("storedSteps").EnumerateArray());
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.ScienceIntroductionPublished);
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Equal("s1", Assert.Single(checkpoint.WorkingState!.Plan).Id);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        var tools = harness.Handler.Bodies[0].GetProperty("tools").GetRawText();
        Assert.All(harness.Handler.Bodies, body => Assert.Equal(tools, body.GetProperty("tools").GetRawText()));
        Assert.Equal("none", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.All(harness.Handler.Bodies.Skip(1).Take(3), body => Assert.Equal("required", body.GetProperty("tool_choice").GetString()));
        var introduction = events.First(item => item.Type == RunEventTypes.TextDelta);
        Assert.True(introduction.Id < successful.Id);
        Assert.Contains(Introduction, CodingTextReconciler.Project(await harness.Repository.GetVisibleTextEventsAsync(run, 0)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task InheritedOpenPlanMustBeAcknowledgedWithoutErasingItsStagesOrCountingItAsANoOp(int previousNoOps)
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Alter Auftrag") with
            { Science = new(ScienceSkill.Profile, []) }, Plan.Arguments) with { ConsecutivePlanNoOps = previousNoOps };
        var initial = RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1");
        await harness.Repository.SaveCheckpointAsync(run, new(initial, 0, 0, 0, 0, WorkingState: state,
            SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Equal(0, checkpoint.WorkingState!.ConsecutivePlanNoOps);
        Assert.Equal("s1", Assert.Single(checkpoint.WorkingState.Plan).Id);
        Assert.Equal("Gilt die Herleitung?", Assert.Single(checkpoint.WorkingState.Science!.Stages).Question);
        var receipt = Assert.Single(await harness.Repository.GetServerToolCompletedEventsAsync(run));
        Assert.True(receipt.Data.GetProperty("success").GetBoolean());
        Assert.Single(receipt.Data.GetProperty("result").GetProperty("storedSteps").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntroductionIsASeparateVisibleTurnBeforePlanAndRestoresRequestedReasoning(bool supportsNone)
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.SupportsNone = supportsNone;
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.Contains(Introduction, CodingTextReconciler.Project(await harness.Repository.GetVisibleTextEventsAsync(run, 0)));
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.ScienceIntroductionPublished);
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Equal(0, checkpoint.InvalidToolTurnCount);
        Assert.Equal(3, harness.Handler.Bodies.Count);
        Assert.Equal("none", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("required", harness.Handler.Bodies[1].GetProperty("tool_choice").GetString());
        Assert.Equal("auto", harness.Handler.Bodies[2].GetProperty("tool_choice").GetString());
        Assert.Equal(supportsNone ? "none" : "low", harness.Handler.Bodies[0].GetProperty("reasoning_effort").GetString());
        Assert.All(harness.Handler.Bodies.Skip(1), body => Assert.Equal("high", body.GetProperty("reasoning_effort").GetString()));
        Assert.InRange(harness.Handler.Bodies[0].GetProperty("max_tokens").GetInt32(), 1, 256);
        Assert.Equal("high", (await harness.Repository.GetRequestAsync(run))!.ReasoningEffort);
        var plan = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan");
        var introduction = events.First(item => item.Type == RunEventTypes.TextDelta);
        Assert.True(introduction.Id < plan.Id);
        Assert.True(plan.Id < Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed).Id);
        var tools = harness.Handler.Bodies[0].GetProperty("tools").GetRawText();
        Assert.All(harness.Handler.Bodies, body => Assert.Equal(tools, body.GetProperty("tools").GetRawText()));
    }

    [Fact]
    public async Task ShortBufferedIntroductionIsPublishedExactlyOnce()
    {
        using var harness = new Harness("compact-v1");
        const string text = "Ich prüfe die Herleitung und plane die Gegenrechnung.";
        harness.Handler.FirstText = text;
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var visible = await harness.Repository.GetVisibleTextEventsAsync(run, 0);
        Assert.Equal(text, CodingTextReconciler.Project(visible));
        Assert.Single(visible, item => !string.IsNullOrEmpty(item.Data.GetProperty("delta").GetString()));
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var plan = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan");
        Assert.True(visible[0].Id < plan.Id);
        Assert.Equal(3, harness.Handler.Bodies.Count);
    }

    [Fact]
    public async Task InterruptedPartialIntroductionIsReconciledBeforePlanning()
    {
        using var harness = new Harness("compact-v1");
        const string text = "Ich prüfe die Herleitung und plane die Gegenrechnung.";
        const string partial = "Ich prüfe die Herleitung";
        harness.Handler.FirstText = text;
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        var turnStart = (await harness.Repository.GetAsync(run))!.LastEventId;
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(partial));
        var initial = RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1");
        var state = CodingWorkingState.Create("Prüfe die Herleitung") with { Science = new(ScienceSkill.Profile, []) };
        await harness.Repository.SaveCheckpointAsync(run, new(initial, 0, 0, 0, 0,
            VisibleTextLength: 0, StreamingTurnStartEventId: turnStart, WorkingState: state,
            SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1", ScienceIntroductionPublished: false));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        Assert.Equal(3, harness.Handler.Bodies.Count);
        Assert.Equal("none", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("required", harness.Handler.Bodies[1].GetProperty("tool_choice").GetString());
        Assert.Equal(text, CodingTextReconciler.Project(await harness.Repository.GetVisibleTextEventsAsync(run, 0)));
        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var plan = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan");
        Assert.Equal(text, CodingTextReconciler.Project(events.Where(item => item.Id < plan.Id).ToArray()));
        Assert.True((await harness.Repository.GetCheckpointAsync(run))!.ScienceIntroductionPublished);
    }

    [Fact]
    public async Task APlanReturnedWithTheIntroductionIsRejectedUntilTheFollowingTurn()
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.AutomaticallyIntroduce = false;
        harness.Handler.Responses.Enqueue([Plan with { Id = "premature-plan" }]);
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        var plans = events.Where(item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan").ToArray();
        Assert.True(Assert.Single(plans).Data.GetProperty("success").GetBoolean());
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Contains(Introduction, CodingTextReconciler.Project(await harness.Repository.GetVisibleTextEventsAsync(run, 0)));
        Assert.Equal(3, harness.Handler.Bodies.Count);
        Assert.Equal("none", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("required", harness.Handler.Bodies[1].GetProperty("tool_choice").GetString());
        Assert.True((await harness.Repository.GetCheckpointAsync(run))!.ScienceIntroductionPublished);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolsWithoutAnIntroductionDoNotExecuteBeforeTheCorrectedIntroductionAndPlan(bool planFirst)
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.AutomaticallyIntroduce = false;
        harness.Handler.FirstText = "";
        harness.Handler.TextByTurn[2] = Introduction;
        harness.Handler.Responses.Enqueue([planFirst ? Plan with { Id = "before-introduction" } : Read with { Id = "before-introduction" }]);
        harness.Handler.Responses.Enqueue([]);
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == "coding.updatePlan");
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        var introduction = events.First(item => item.Type == RunEventTypes.TextDelta);
        Assert.All(events.Where(item => item.Type is RunEventTypes.ServerToolCompleted or RunEventTypes.ClientToolProposed),
            item => Assert.True(introduction.Id < item.Id));
        Assert.Equal(4, harness.Handler.Bodies.Count);
        Assert.All(harness.Handler.Bodies.Take(2), body => Assert.Equal("none", body.GetProperty("tool_choice").GetString()));
        Assert.Equal("required", harness.Handler.Bodies[2].GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedIntroductionContinuesWithThePlanWithoutPublishingAnotherIntroduction(bool introductionFlagWasSaved)
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.AutomaticallyIntroduce = false;
        harness.Handler.FirstText = "";
        harness.Handler.Responses.Enqueue([Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        await harness.Repository.AppendEventAsync(run, RunEventTypes.TextDelta, new TextDeltaEvent(Introduction));
        var initial = RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1");
        initial.Add(new("assistant", Introduction));
        var state = CodingWorkingState.Create("Prüfe die Herleitung") with { Science = new(ScienceSkill.Profile, []) };
        await harness.Repository.SaveCheckpointAsync(run, new(initial, 1, 0, 128, 20, WorkingState: state,
            SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1", ScienceIntroductionPublished: introductionFlagWasSaved));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        Assert.Equal(2, harness.Handler.Bodies.Count);
        Assert.Equal("required", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("high", harness.Handler.Bodies[0].GetProperty("reasoning_effort").GetString());
        Assert.Equal(Introduction, CodingTextReconciler.Project(await harness.Repository.GetVisibleTextEventsAsync(run, 0)));
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.ScienceIntroductionPublished);
        Assert.True(checkpoint.SciencePlanInitialized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredSameRunWithAValidSavedPlanDoesNotPlanAgain(bool entranceFlagWasSaved)
    {
        using var harness = new Harness("compact-v1");
        harness.Handler.AutomaticallyIntroduce = false;
        harness.Handler.FirstText = "";
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Prüfe die Herleitung") with
            { Science = new(ScienceSkill.Profile, []) }, Plan.Arguments);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted, new
        {
            tool = "coding.updatePlan", success = true,
            result = CodingWorkingStateTools.CreatePlanReceipt(state, Plan.Arguments),
        });
        var initial = RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1");
        await harness.Repository.SaveCheckpointAsync(run, new(initial, 1, 1, 0, 0, WorkingState: state,
            SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1", SciencePlanInitialized: entranceFlagWasSaved));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        Assert.Single(harness.Handler.Bodies);
        Assert.True((await harness.Repository.GetCheckpointAsync(run))!.SciencePlanInitialized);
        Assert.Single(await harness.Repository.GetServerToolCompletedEventsAsync(run));
        Assert.Equal("auto", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Empty(await harness.Repository.GetVisibleTextEventsAsync(run, 0));
    }

    [Fact]
    public async Task OrdinaryCodingDoesNotAcquireASciencePlanRequirement()
    {
        using var harness = new Harness("compact-v1", science: false);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));
        Assert.False((await harness.Repository.GetCheckpointAsync(run))!.SciencePlanInitialized);
        Assert.Equal("auto", Assert.Single(harness.Handler.Bodies).GetProperty("tool_choice").GetString());
    }

    private sealed class Harness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        internal Handler Handler { get; } = new();
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        internal RunRequest Request { get; }

        internal Harness(string? profile, bool science = true)
        {
            Handler.AutomaticallyIntroduce = science;
            Handler.FirstText = science ? Introduction : null;
            _context.Options.ModelRuntimeUri = new("http://native.test:8081");
            _http = new(Handler);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance));
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Request = new(MissumAiProtocol.Version, RunMode.Coding, [new("user", [new("text", Text: "Prüfe die Herleitung in der Publikation.")])],
                ClientCapabilities: ["coding"], AllowedServerTools: [], SessionId: "science-initial-plan",
                PreferredGeneralModelId: Handler.Model, PreferredCodingModelId: Handler.Model,
                CodingOptions: new(WorkspacePath: _context.Root, ContinueSessionContext: true),
                Limits: new(TimeoutSeconds: 0), WorkflowProfile: science ? ScienceSkill.Profile : null,
                ContextProfileVersion: profile, ReasoningEffort: "high");
        }

        internal async Task<string> CreateAsync() => (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
        public void Dispose() { _services.Dispose(); _http.Dispose(); _context.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal const string Model = "coding/qwen3.8-27b~science-initial-plan";
        internal Queue<LmToolCall[]> Responses { get; } = new();
        internal List<JsonElement> Bodies { get; } = [];
        internal string? FirstText { get; set; }
        internal Dictionary<int, string> TextByTurn { get; } = [];
        internal bool AutomaticallyIntroduce { get; set; }
        internal bool SupportsNone { get; set; } = true;
        private bool _loaded;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Response(new { data = new[]
                { new { id = Model, tags = new[] { "missum-context-train:32768", "missum-reasoning-mode:llama-native",
                    SupportsNone ? "missum-reasoning-levels:none|low|high" : "missum-reasoning-levels:low|high",
                    "missum-reasoning-default:high" }, status = new { value = _loaded ? "loaded" : "unloaded" } } } });
            if (path == "/props") return Response(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/models/load") { _loaded = true; return Response(new { success = true }); }
            if (path.StartsWith("/sessions/", StringComparison.Ordinal)) return Response(new { status = "resident" });
            if (path == "/v1/chat/completions/input_tokens") return Response(new { input_tokens = 128 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native endpoint: " + path);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            LmToolCall[] calls = Bodies.Count == 1 && AutomaticallyIntroduce ? [] : Responses.Dequeue();
            var text = TextByTurn.TryGetValue(Bodies.Count, out var customText) ? customText : Bodies.Count == 1 ? FirstText ?? "" : "";
            return Response(new { choices = new[] { new { message = new { role = "assistant", content = text,
                tool_calls = calls.Select(call => new { id = call.Id, type = "function", function = new
                    { name = ModelRuntimeClient.ToTransportToolName(call.Name), arguments = call.Arguments.GetRawText() } }) }, finish_reason = calls.Length == 0 ? "stop" : "tool_calls" } },
                usage = new { prompt_tokens = 128, completion_tokens = 8 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}

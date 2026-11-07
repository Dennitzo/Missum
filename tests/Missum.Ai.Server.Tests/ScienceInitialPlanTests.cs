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
    [InlineData(null, true)]
    [InlineData("compact-v1", true)]
    [InlineData(null, false)]
    [InlineData("compact-v1", false)]
    public async Task FirstValidPlanPrecedesClientActionsAndMixedOrAdministrativeCallsCannotOpenTheGate(string? contextProfile, bool science)
    {
        using var harness = new Harness(contextProfile, science);
        harness.Handler.Responses.Enqueue([harness.Plan, Read]);
        harness.Handler.Responses.Enqueue([new("admin", "coding.updatePlan", J("""{"phase":"planning","nextStep":"Publikation lesen"}"""))]);
        harness.Handler.Responses.Enqueue([harness.Plan]);
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
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Equal("s1", Assert.Single(checkpoint.WorkingState!.Plan).Id);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        var tools = harness.Handler.Bodies[0].GetProperty("tools").GetRawText();
        Assert.All(harness.Handler.Bodies, body => Assert.Equal(tools, body.GetProperty("tools").GetRawText()));
        Assert.All(harness.Handler.Bodies.Take(3), body => Assert.Equal("required", body.GetProperty("tool_choice").GetString()));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(4, false)]
    public async Task InheritedOpenPlanMustBeAcknowledgedWithoutErasingItsStagesOrCountingItAsANoOp(int previousNoOps, bool science)
    {
        using var harness = new Harness("compact-v1", science);
        harness.Handler.Responses.Enqueue([harness.Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Alter Auftrag") with
            { Science = science ? new(ScienceSkill.Profile, []) : null }, harness.Plan.Arguments) with { ConsecutivePlanNoOps = previousNoOps };
        var initial = RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1");
        await harness.Repository.SaveCheckpointAsync(run, new(initial, 0, 0, 0, 0, WorkingState: state,
            SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Equal(0, checkpoint.WorkingState!.ConsecutivePlanNoOps);
        Assert.Equal("s1", Assert.Single(checkpoint.WorkingState.Plan).Id);
        if (science) Assert.Equal("Gilt die Herleitung?", Assert.Single(checkpoint.WorkingState.Science!.Stages).Question);
        else Assert.Null(checkpoint.WorkingState.Science);
        var receipt = Assert.Single(await harness.Repository.GetServerToolCompletedEventsAsync(run));
        Assert.True(receipt.Data.GetProperty("success").GetBoolean());
        Assert.Single(receipt.Data.GetProperty("result").GetProperty("storedSteps").EnumerateArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstTurnCreatesThePlanWithTheRequestedReasoningAndNormalOutputBudget(bool science)
    {
        using var harness = new Harness("compact-v1", science);
        harness.Handler.Responses.Enqueue([harness.Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        Assert.Equal(2, harness.Handler.Bodies.Count);
        Assert.Equal("required", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("auto", harness.Handler.Bodies[1].GetProperty("tool_choice").GetString());
        Assert.All(harness.Handler.Bodies, body => Assert.Equal("high", body.GetProperty("reasoning_effort").GetString()));
        Assert.True(harness.Handler.Bodies[0].GetProperty("max_tokens").GetInt32() > 256);
        Assert.Empty(await harness.Repository.GetVisibleTextEventsAsync(run, 0));
        Assert.True((await harness.Repository.GetCheckpointAsync(run))!.SciencePlanInitialized);
        Assert.All(harness.Handler.Bodies, body => Assert.Contains(CodingPlanMilestoneGuard.Marker, body.GetProperty("messages").GetRawText().Replace("\\n", "\n", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalTextBeforePlanIsNotPublishedAndCannotCompleteTheRun(bool science)
    {
        using var harness = new Harness("compact-v1", science);
        const string prematureAnswer = "Auftrag vollständig erledigt.";
        harness.Handler.FirstText = prematureAnswer;
        harness.Handler.Responses.Enqueue([]);
        harness.Handler.Responses.Enqueue([harness.Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));

        var events = await harness.Repository.GetEventsAfterAsync(run, 0);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.TextDelta
            && item.Data.GetRawText().Contains(prematureAnswer, StringComparison.Ordinal));
        Assert.True((await harness.Repository.GetCheckpointAsync(run))!.SciencePlanInitialized);
        Assert.Equal(3, harness.Handler.Bodies.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task RecoveredSameRunWithAValidSavedPlanDoesNotPlanAgain(bool entranceFlagWasSaved, bool science)
    {
        using var harness = new Harness("compact-v1", science);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Prüfe die Herleitung") with
            { Science = science ? new(ScienceSkill.Profile, []) : null }, harness.Plan.Arguments);
        await harness.Repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted, new
        {
            tool = "coding.updatePlan", success = true,
            result = CodingWorkingStateTools.CreatePlanReceipt(state, harness.Plan.Arguments),
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
    }

    [Fact]
    public async Task OrdinaryCodingMustCreateAPlanWithoutAcquiringScienceRules()
    {
        using var harness = new Harness("compact-v1", science: false);
        harness.Handler.Responses.Enqueue([harness.Plan]);
        harness.Handler.Responses.Enqueue([Read]);
        var run = await harness.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));
        var checkpoint = (await harness.Repository.GetCheckpointAsync(run))!;
        Assert.True(checkpoint.SciencePlanInitialized);
        Assert.Null(checkpoint.WorkingState!.Science);
        Assert.Equal("required", harness.Handler.Bodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("auto", harness.Handler.Bodies[1].GetProperty("tool_choice").GetString());
        Assert.DoesNotContain(harness.Handler.Bodies, body => body.GetProperty("messages").GetRawText().Contains(ScienceSkill.Marker, StringComparison.Ordinal));
    }

    [Fact]
    public void InternalSummariesAndChildAgentsDoNotAcquireTheMainRunPlanGate()
    {
        using var harness = new Harness("compact-v1", science: false);
        Assert.True(ScienceInitialPlanGuard.Applies(harness.Request));
        Assert.False(ScienceInitialPlanGuard.Applies(harness.Request with { Mode = RunMode.General }));
        Assert.False(ScienceInitialPlanGuard.Applies(harness.Request with { ConversationProfile = ConversationProfile.ContextPreparation }));
        // Child admission remains governed by the existing subagent workflow.
        Assert.False(ScienceInitialPlanGuard.Applies(harness.Request with { Subagent = new("parent", "child", "Prüfe den Teilauftrag", null) }));
    }

    [Fact]
    public async Task CodexCanCompleteSeveralStagesInOneUpdate()
    {
        using var harness = new Harness("compact-v1", science: false);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Prüfe beide Etappen"), J("""
            {"steps":[{"id":"s1","title":"Erste Prüfung","status":"in_progress"},{"id":"s2","title":"Zweite Prüfung"}]}
            """));
        state = CodingWorkingStateReducer.ObserveToolResult(state, new("actual-read", "coding.read", J("{}")), """{"success":true,"content":"Reales Prüfergebnis"}""");
        await harness.Repository.SaveCheckpointAsync(run, new(RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1"),
            1, 1, 0, 0, WorkingState: state, SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1", SciencePlanInitialized: true));
        harness.Handler.Responses.Enqueue([new("batch", "coding.updatePlan", J("""
            {"steps":[{"id":"s1","title":"Erste Prüfung","status":"completed"},
                      {"id":"s2","title":"Zweite Prüfung","status":"completed"}],"phase":"final"}
            """))]);
        harness.Handler.Responses.Enqueue([Read]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));
        var receipts = await harness.Repository.GetServerToolCompletedEventsAsync(run);
        Assert.True(Assert.Single(receipts).Data.GetProperty("success").GetBoolean());
        var saved = (await harness.Repository.GetCheckpointAsync(run))!.WorkingState!;
        Assert.All(saved.Plan, step => Assert.Equal("completed", step.Status));
        Assert.Equal("final", saved.Phase);
    }

    [Fact]
    public async Task ALegacyPlanEvidenceBlockIsClearedOnceAndTheStageCompletesWithoutIt()
    {
        using var harness = new Harness("compact-v1", science: false);
        var run = await harness.CreateAsync();
        var state = CodingWorkingStateReducer.ApplyPlanUpdate(CodingWorkingState.Create("Prüfung"),
            J("""{"steps":[{"id":"s","title":"Phase 1: Prüfen","status":"in_progress"}]}"""));
        // The block below comes from a session that was still gated by plan evidence.
        var retired = new LmToolCall("retry", "coding.updatePlan",
            J("""{"steps":[{"id":"s","title":"Phase 1: Prüfen","status":"completed","evidenceIds":["arrived-later"]}]}"""));
        for (var attempt = 0; attempt < 2; attempt++)
            state = CodingWorkingStateReducer.ObserveToolResult(state, retired with { Id = "failed-" + attempt },
                """{"success":false,"errorCode":"agent.invalid_state_action","error":"Unbekannte evidenceIds für Schritt s","rejectedIds":["arrived-later"],"candidates":[{"planEvidenceId":"actual-read"}]}""");
        Assert.Equal(2, Assert.Single(state.Failures).Count);
        await harness.Repository.SaveCheckpointAsync(run, new(RunProcessor.CreateInitialMessages(harness.Request, "coding", [], "compact-v1"),
            1, 1, 0, 0, WorkingState: state, SelectedModelId: Handler.Model, ContextProfileVersion: "compact-v1", SciencePlanInitialized: true));
        Assert.Empty((await harness.Repository.GetCheckpointAsync(run))!.WorkingState!.Failures);
        harness.Handler.Responses.Enqueue([new("retry", "coding.updatePlan",
            J("""{"steps":[{"id":"s","title":"Phase 1: Prüfen","status":"completed"}]}"""))]);
        harness.Handler.Responses.Enqueue([Read]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(run, deadline.Token));
        var receipt = Assert.Single(await harness.Repository.GetServerToolCompletedEventsAsync(run));
        Assert.True(receipt.Data.GetProperty("success").GetBoolean());
        Assert.Equal("completed", Assert.Single((await harness.Repository.GetCheckpointAsync(run))!.WorkingState!.Plan).Status);
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
        internal LmToolCall Plan { get; }

        internal Harness(string? profile, bool science = true)
        {
            Plan = science ? ScienceInitialPlanTests.Plan : new("plan", "coding.updatePlan", J("""{"steps":[{"id":"s1","title":"Herleitung prüfen","status":"in_progress"}]}"""));
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
        private static readonly string[] Tags = ["missum-context-train:32768", "missum-reasoning-mode:llama-native",
            "missum-reasoning-levels:none|low|high", "missum-reasoning-default:high"];
        internal Queue<LmToolCall[]> Responses { get; } = new();
        internal List<JsonElement> Bodies { get; } = [];
        internal string? FirstText { get; set; }
        private bool _loaded;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Response(new { data = new[]
                { new { id = Model, tags = Tags, status = new { value = _loaded ? "loaded" : "unloaded" } } } });
            if (path == "/props") return Response(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/models/load") { _loaded = true; return Response(new { success = true }); }
            if (path.StartsWith("/sessions/", StringComparison.Ordinal)) return Response(new { status = "resident" });
            if (path == "/v1/chat/completions/input_tokens") return Response(new { input_tokens = 128 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native endpoint: " + path);
            Bodies.Add(JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token)));
            var calls = Responses.Dequeue();
            return Response(new { choices = new[] { new { message = new { role = "assistant", content = Bodies.Count == 1 ? FirstText ?? "" : "",
                tool_calls = calls.Select(call => new { id = call.Id, type = "function", function = new
                    { name = ModelRuntimeClient.ToTransportToolName(call.Name), arguments = call.Arguments.GetRawText() } }) }, finish_reason = calls.Length == 0 ? "stop" : "tool_calls" } },
                usage = new { prompt_tokens = 128, completion_tokens = 8 } });
        }
        private static HttpResponseMessage Response(object body) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}

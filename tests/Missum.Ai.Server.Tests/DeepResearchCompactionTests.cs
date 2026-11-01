using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class DeepResearchCompactionTests
{
    private const string Model = "coding/ResearchCompaction-Q4~123456";
    private const int ContextLength = 32_768;
    private const string OriginalTask = "Prüfe die ursprüngliche Vorzeichenkonvention (-,+,+,+) anhand vorhandener Belege.";
    private const string ResearchTask = "Bewerte die Vorzeichenkonvention. Ohne gelesene Originalquelle bleibt der Befund offen.";
    private static readonly string[] ResearchTools = ["web.search", "web.fetch", "web.deepResearch"];

    [Fact]
    public async Task LargeInheritedHistoryIsCompactedAndTheSameResearchOperationFinishes()
    {
        using var harness = await Harness.CreateAsync(largeHistory: true);
        var parentBefore = JsonSerializer.Serialize(harness.ParentMessages);

        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(1, harness.Handler.SummaryCalls);
        Assert.Equal(1, harness.Handler.PlanCalls);
        Assert.Equal(1, harness.Handler.SynthesisCalls);
        Assert.Equal("Die Quellenlage bleibt offen.", result.Result.Result.GetProperty("summary").GetString());
        Assert.Equal(parentBefore, JsonSerializer.Serialize(harness.ParentMessages));
        var plan = Assert.Single(harness.Handler.ChatBodies, IsPlan);
        Assert.Contains(OriginalTask, Text(plan), StringComparison.Ordinal);
        Assert.Contains(ResearchTask, Text(plan), StringComparison.Ordinal);
        Assert.DoesNotContain(new string('h', 10_000), Text(plan), StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(Text(plan), "JSON-Objekt für research.plan"));
        Assert.Contains("\"questions\"", Text(plan), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
        AssertCompactions(await harness.Repository.GetEventsAfterAsync(harness.RunId, 0), 1);
    }

    [Fact]
    public async Task ExactNativeThresholdCompactsBeforeHardOverflowAndRetriesWithoutDuplicatingThePhase()
    {
        using var harness = await Harness.CreateAsync(exactOverflowAtPlan: true);

        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(1, harness.Handler.PlanThresholdHits);
        Assert.Equal(1, harness.Handler.SummaryCalls);
        Assert.Equal(1, harness.Handler.PlanCalls);
        Assert.Equal(1, harness.Handler.SynthesisCalls);
        Assert.Equal(27_000, harness.Handler.RejectedCounts.Single());
        Assert.True(27_000 < ContextLength); // This is the shared80% boundary, not a full slot.
        var attempts = harness.Handler.CountBodies.Where(IsPlan).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.All(attempts, body => Assert.Equal(1, Occurrences(Text(body), "JSON-Objekt für research.plan")));
        Assert.Contains(OriginalTask, Text(attempts[1]), StringComparison.Ordinal);
        Assert.Contains(ResearchTask, Text(attempts[1]), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
        AssertCompactions(await harness.Repository.GetEventsAfterAsync(harness.RunId, 0), 1);
    }

    [Fact]
    public async Task LaterNativePressureCanCompactAgainWithoutReplayingCompletedResearchPhases()
    {
        using var harness = await Harness.CreateAsync(exactOverflowAtPlan: true, exactOverflowAtSynthesis: true);

        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(2, harness.Handler.SummaryCalls);
        Assert.Equal(1, harness.Handler.PlanCalls);
        Assert.Equal(1, harness.Handler.SynthesisCalls);
        Assert.Equal(1, harness.Handler.PlanThresholdHits);
        Assert.Equal(1, harness.Handler.SynthesisThresholdHits);
        var synthesisAttempts = harness.Handler.CountBodies.Where(IsSynthesis).ToArray();
        Assert.Equal(2, synthesisAttempts.Length);
        Assert.All(synthesisAttempts, body => Assert.Equal(1, Occurrences(Text(body), "JSON-Objekt für research.synthesize")));
        Assert.Contains("\"findings\"", Text(synthesisAttempts[1]), StringComparison.Ordinal);
        Assert.Contains("\"uncertainties\"", Text(synthesisAttempts[1]), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
        AssertCompactions(await harness.Repository.GetEventsAfterAsync(harness.RunId, 0), 2);
    }

    [Fact]
    public async Task StopDuringResearchCompactionCancelsTheActualSummaryWithoutStartingTheResearchPhase()
    {
        using var harness = await Harness.CreateAsync(largeHistory: true, holdSummary: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var execution = harness.RunAsync(stop.Token);
        await harness.Handler.SummaryEntered.Task.WaitAsync(stop.Token);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(harness.Handler.SummaryCancelled);
        Assert.Equal(0, harness.Handler.PlanCalls);
        Assert.Equal(0, harness.Handler.SynthesisCalls);
        var events = await harness.Repository.GetEventsAfterAsync(harness.RunId, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "operation"
            && item.Data.GetProperty("status").GetString() == "cancelled");
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "compaction"
            && item.Data.GetProperty("status").GetString() == "completed");
        AssertSharedRuntime(harness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidPhaseResponsesAreCorrectedInPlaceWithAllModelCallsAndTokensAccounted(bool synthesis)
    {
        using var harness = await Harness.CreateAsync(invalidPlanResponses: synthesis ? 0 : 1,
            invalidSynthesisResponses: synthesis ? 1 : 0);
        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(3_000, result.InputTokens);
        Assert.Equal(90, result.OutputTokens);
        Assert.Equal(synthesis ? 1 : 2, harness.Handler.PlanCalls);
        Assert.Equal(synthesis ? 2 : 1, harness.Handler.SynthesisCalls);
        var repair = Assert.Single(harness.Handler.ChatBodies, body => LastText(body).Contains("Korrigiere ausschließlich", StringComparison.Ordinal));
        var initial = harness.Handler.ChatBodies.First(synthesis ? IsSynthesis : IsPlan);
        Assert.Equal(Occurrences(Text(initial), ResearchTask), Occurrences(Text(repair), ResearchTask));
        Assert.Contains(synthesis ? "gültiges JSON" : "$.questions", LastText(repair), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
        var events = await harness.Repository.GetEventsAfterAsync(harness.RunId, 0);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "operation" && item.Data.GetProperty("status").GetString() == "failed");
    }

    [Fact]
    public async Task RepeatedInvalidAnswersStopAfterTwoRepairsWithoutPretendingResearchSucceeded()
    {
        using var harness = await Harness.CreateAsync(invalidPlanResponses: 10);
        var result = await harness.RunAsync();

        Assert.False(result.Result.Succeeded);
        Assert.Equal(3, harness.Handler.PlanCalls);
        Assert.Equal(0, harness.Handler.SynthesisCalls);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(3_000, result.InputTokens);
        Assert.Equal("web.deepResearch.incomplete", result.Result.Result.GetProperty("errorCode").GetString());
        Assert.Contains("$.questions", result.Result.Result.GetProperty("uncertainties")[0].GetString(), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
    }

    [Theory]
    [InlineData("{\"questions\":[{\"question\":\"\",\"query\":\"Bohm\"}]}")]
    [InlineData("{\"questions\":[{\"question\":\"Bohm\",\"query\":\"   \"}]}")]
    public async Task EmptyRequiredTextsUseTheSameBoundedPhaseRepair(string invalidPlan)
    {
        using var harness = await Harness.CreateAsync(invalidPlanResponses: 1, invalidPlan: invalidPlan);
        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(2, harness.Handler.PlanCalls);
        Assert.Equal(3, result.ModelCalls);
        Assert.Contains("$.questions[0]", LastText(harness.Handler.ChatBodies[1]), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
    }

    [Fact]
    public async Task AnUnexpectedNativeToolCallIsRejectedAndRepairedWithoutExecutingItOrLosingItsUsage()
    {
        using var harness = await Harness.CreateAsync(unexpectedPlanTool: true);
        var result = await harness.RunAsync();

        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(2, harness.Handler.PlanCalls);
        Assert.Equal(3, result.ModelCalls);
        Assert.Equal(3_000, result.InputTokens);
        Assert.Equal(90, result.OutputTokens);
        Assert.Equal(0, result.ToolCalls);
        var repair = harness.Handler.ChatBodies[1];
        Assert.Contains("research.phase_tools_disabled", Text(repair), StringComparison.Ordinal);
        Assert.Contains("Korrigiere ausschließlich", LastText(repair), StringComparison.Ordinal);
        AssertSharedRuntime(harness);
    }

    private static void AssertSharedRuntime(Harness harness)
    {
        var bodies = harness.Handler.ChatBodies;
        Assert.NotEmpty(bodies);
        var tools = bodies[0].GetProperty("tools");
        Assert.All(bodies, body =>
        {
            Assert.Equal(Model, body.GetProperty("model").GetString());
            Assert.Equal(0, body.GetProperty("id_slot").GetInt32());
            Assert.Equal("none", body.GetProperty("tool_choice").GetString());
            Assert.True(JsonElement.DeepEquals(tools, body.GetProperty("tools")));
            Assert.False(body.TryGetProperty("response_format", out _));
        });
        var key = RunProcessor.BuildRunSessionCacheKey(harness.Request, harness.RunId, "general");
        Assert.NotEmpty(harness.Handler.CacheBodies);
        Assert.All(harness.Handler.CacheBodies, body => Assert.Equal(key, body.GetProperty("sessionKey").GetString()));
        Assert.All(harness.Handler.CacheBodies.Where(body => body.GetProperty("sessionId").ValueKind != JsonValueKind.Null),
            body => Assert.Equal(harness.Request.SessionId, body.GetProperty("sessionId").GetString()));
    }

    private static void AssertCompactions(IReadOnlyList<RunEvent> events, int expected)
    {
        var progress = events.Where(item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "compaction").ToArray();
        var starts = progress.Where(item => item.Data.GetProperty("status").GetString() == "running").ToArray();
        var completions = progress.Where(item => item.Data.GetProperty("status").GetString() == "completed").ToArray();
        Assert.Equal(expected, starts.Length);
        Assert.Equal(expected, completions.Length);
        Assert.Equal(expected, starts.Select(item => item.Data.GetProperty("stepId").GetString()).Distinct().Count());
        foreach (var start in starts)
        {
            Assert.Equal("context.compact", start.Data.GetProperty("tool").GetString());
            var end = Assert.Single(completions, item => item.Data.GetProperty("stepId").GetString() == start.Data.GetProperty("stepId").GetString());
            Assert.True(end.Id > start.Id);
            Assert.Equal(start.Data.GetProperty("operationId").GetString(), end.Data.GetProperty("operationId").GetString());
        }
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ContextChanged);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ModelGeneration
            && item.Data.GetProperty("state").GetString() == "codingCompacting");
        Assert.Single(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "operation"
            && item.Data.GetProperty("status").GetString() == "completed");
    }

    private static bool IsPlan(JsonElement body) => LastText(body).Contains("JSON-Objekt für research.plan", StringComparison.Ordinal);
    private static bool IsSynthesis(JsonElement body) => LastText(body).Contains("JSON-Objekt für research.synthesize", StringComparison.Ordinal);
    private static bool IsSummary(JsonElement body) => body.GetProperty("messages").EnumerateArray().Any(message =>
        message.GetProperty("role").GetString() == "system"
        && (message.GetProperty("content").GetString()?.Contains("Arbeitszusammenfassung", StringComparison.Ordinal) ?? false));
    private static string LastText(JsonElement body) => body.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
    private static string Text(JsonElement body) => string.Join("\n", body.GetProperty("messages").EnumerateArray().Select(message => message.GetProperty("content").GetString()));
    private static int Occurrences(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    private sealed class Harness : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _runtime;
        private readonly ServiceProvider _provider;
        internal NativeHandler Handler { get; }
        internal RunRepository Repository => _provider.GetRequiredService<RunRepository>();
        internal RunRequest Request { get; } = new(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", OriginalTask)])], AllowedServerTools: ResearchTools,
            PreferredGeneralModelId: Model, SessionId: "research-compaction-session");
        internal string RunId { get; private set; } = "";
        internal LmChatMessage[] ParentMessages { get; private set; } = [];
        private IReadOnlyList<AgentToolSpec> _tools = [];
        private IReadOnlyList<LmToolDefinition> _definitions = [];

        private Harness(NativeHandler handler)
        {
            Handler = handler;
            _http = new(handler);
            _runtime = new(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(_runtime);
            _provider = services.BuildServiceProvider();
        }

        internal static async Task<Harness> CreateAsync(bool largeHistory = false, bool exactOverflowAtPlan = false,
            bool exactOverflowAtSynthesis = false, bool holdSummary = false, int invalidPlanResponses = 0, int invalidSynthesisResponses = 0,
            string invalidPlan = "{}", bool unexpectedPlanTool = false)
        {
            var harness = new Harness(new() { ExactOverflowAtPlan = exactOverflowAtPlan,
                ExactOverflowAtSynthesis = exactOverflowAtSynthesis, HoldSummary = holdSummary,
                InvalidPlanResponses = invalidPlanResponses, InvalidSynthesisResponses = invalidSynthesisResponses,
                InvalidPlan = invalidPlan, UnexpectedPlanTool = unexpectedPlanTool });
            harness.RunId = (await harness.Repository.CreateAsync(harness.Request, null)).Snapshot.RunId;
            harness._tools = new AgentToolCatalog().GetAvailableTools(harness.Request);
            harness._definitions = RunProcessor.CreateModelToolDefinitions(harness._tools, null, directTools: true);
            harness.ParentMessages = [new("system", "Hauptsystem: bestätigte Ergebnisse und Quellen vom aktuellen Auftrag unterscheiden."),
                new("user", OriginalTask), new("assistant", largeHistory ? new string('h', 110_000) : "Bisher bestätigte Konvention: (-,+,+,+)."),
                new("assistant", ToolCalls: [new("parent-research", "web.deepResearch", JsonSerializer.SerializeToElement(new { task = ResearchTask, profile = "web" }))])];
            return harness;
        }

        internal Task<CodingDeepResearchExecution> RunAsync(CancellationToken cancellationToken = default) =>
            _provider.GetRequiredService<RunProcessor>().ExecuteCodingDeepResearchAsync(RunId, "operation-compaction",
                JsonSerializer.SerializeToElement(new { task = ResearchTask, profile = "web" }), Model, "general", ContextLength,
                24, 27, _tools, null, parentMessages: ParentMessages, parentTools: _definitions, cancellationToken: cancellationToken);

        public void Dispose()
        {
            _provider.Dispose();
            _runtime.Dispose();
            _http.Dispose();
            _context.Dispose();
        }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        private static readonly string[] ModelTags = ["missum-context-train:32768"];
        internal bool ExactOverflowAtPlan { get; init; }
        internal bool ExactOverflowAtSynthesis { get; init; }
        internal bool HoldSummary { get; init; }
        internal int InvalidPlanResponses { get; init; }
        internal int InvalidSynthesisResponses { get; init; }
        internal string InvalidPlan { get; init; } = "{}";
        internal bool UnexpectedPlanTool { get; init; }
        internal int SummaryCalls { get; private set; }
        internal int PlanCalls { get; private set; }
        internal int SynthesisCalls { get; private set; }
        internal int PlanThresholdHits { get; private set; }
        internal int SynthesisThresholdHits { get; private set; }
        internal bool SummaryCancelled { get; private set; }
        internal List<int> RejectedCounts { get; } = [];
        internal List<JsonElement> ChatBodies { get; } = [];
        internal List<JsonElement> CountBodies { get; } = [];
        internal List<JsonElement> CacheBodies { get; } = [];
        internal TaskCompletionSource SummaryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/agents/", StringComparison.Ordinal)) throw new InvalidOperationException("No subagent admission is allowed for research compaction.");
            if (path == "/v1/models") return Json(new { data = new[] { new { id = Model,
                tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = ContextLength } });
            if (path is "/sessions/prepare" or "/sessions/save")
            {
                using var cache = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                CacheBodies.Add(cache.RootElement.Clone());
                return Json(new { status = path == "/sessions/save" ? "saved" : "resident" });
            }
            if (path is not ("/v1/chat/completions/input_tokens" or "/v1/chat/completions"))
                throw new InvalidOperationException("Unexpected native endpoint: " + path);
            using var parsed = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = parsed.RootElement.Clone();
            if (path == "/v1/chat/completions/input_tokens")
            {
                CountBodies.Add(body);
                if (!IsSummary(body) && IsPlan(body) && ExactOverflowAtPlan && PlanThresholdHits == 0)
                {
                    PlanThresholdHits++;
                    RejectedCounts.Add(27_000);
                    return Json(new { input_tokens = 27_000 });
                }
                if (!IsSummary(body) && IsSynthesis(body) && ExactOverflowAtSynthesis && SynthesisThresholdHits == 0)
                {
                    SynthesisThresholdHits++;
                    RejectedCounts.Add(27_000);
                    return Json(new { input_tokens = 27_000 });
                }
                return Json(new { input_tokens = 1_000 });
            }
            ChatBodies.Add(body);
            if (body.TryGetProperty("response_format", out _)) throw new InvalidOperationException("Research must retain normal native assistant framing.");
            string content;
            if (IsSummary(body))
            {
                SummaryCalls++;
                SummaryEntered.TrySetResult();
                if (HoldSummary)
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) { SummaryCancelled = true; throw; }
                content = "Verdichteter bestätigter Arbeitsstand: Konvention (-,+,+,+). Noch keine Originalquelle gelesen; offene Quellenprüfung fortsetzen.";
            }
            else if (IsPlan(body))
            {
                PlanCalls++;
                if (UnexpectedPlanTool && PlanCalls == 1)
                    return Json(new { choices = new[] { new { message = new { role = "assistant", tool_calls = new[]
                    {
                        new { id = "unexpected-native-fetch", type = "function", function = new { name = "web_fetch", arguments = "{\"url\":\"https://example.org/\",\"query\":\"Bohm\"}" } },
                    } }, finish_reason = "tool_calls" } }, usage = new { prompt_tokens = 1_000, completion_tokens = 30 } });
                content = PlanCalls <= InvalidPlanResponses ? InvalidPlan : "{\"questions\":[],\"sourceLimit\":0}";
            }
            else if (IsSynthesis(body))
            {
                SynthesisCalls++;
                content = SynthesisCalls <= InvalidSynthesisResponses ? "{\"summary\":\"\\q\"}"
                    : "{\"summary\":\"Die Quellenlage bleibt offen.\",\"findings\":[],\"uncertainties\":[\"Keine Originalquelle gelesen.\"]}";
            }
            else throw new InvalidOperationException("Unexpected research or compaction phase.");
            return Json(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1_000, completion_tokens = 30 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

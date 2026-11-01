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

public sealed class SubagentContextCompactionTests
{
    private const string Model = "coding/Subagent-Compaction-Fixture~1937";
    private const string Instance = Model + "@subagent";
    private const int Context = 32_768;
    private static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();

    [Theory]
    [InlineData(RunMode.Coding, false)]
    [InlineData(RunMode.Coding, true)]
    [InlineData(RunMode.General, false)]
    [InlineData(RunMode.General, true)]
    public async Task ChildCompactsAndContinuesOnItsOwnInstanceAndCacheForEstimatedAndExactThresholds(RunMode mode, bool exactOverflow)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new ChildRuntime(exactOverflow);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        const string task = "Prüfe ausschließlich die Vorzeichenkonvention und berichte das Ergebnis.";
        var request = new RunRequest(MissumAiProtocol.Version, mode,
            [new("user", [new("text", "Führe eine unabhängige fachliche Prüfung durch.")])],
            SessionId: "main-context-owner", AllowedServerTools: [], ClientCapabilities: ["coding", "workspace", "subagents"],
            PreferredCodingModelId: Model, PreferredGeneralModelId: Model, WorkspacePath: context.Root,
            CodingOptions: mode == RunMode.Coding ? new(WorkspacePath: context.Root) : null,
            ReasoningEffort: "high", Limits: new(MaximumContextTokens: Context));
        var parent = (await repository.CreateAsync(request, null)).Snapshot;
        var pending = new LmToolCall("parent-owned-spawn", SubagentToolNames.Spawn,
            JsonSerializer.SerializeToElement(new { task }));
        LmChatMessage[] parentMessages = [new("system", "Unveränderter Hauptkontext."), new("user", "Ursprüngliches Forschungsziel."),
            new("assistant", "Abgeschlossene Vorarbeit: " + new string('a', exactOverflow ? 12_000 : 100_000)),
            new("assistant", ToolCalls: [pending])];
        var tools = RunProcessor.CreateModelToolDefinitions(new AgentToolCatalog().GetAvailableTools(request, true), null, directTools: true);
        var parentCheckpoint = new AgentRunCheckpoint(parentMessages, 2, 1, 100, 10, ActiveToolCalls: [pending],
            SelectedModelId: Model, ContextTools: tools, UseStableSubagentToolCatalog: true, PreserveSessionPromptPrefix: true);
        await repository.SaveCheckpointAsync(parent.RunId, parentCheckpoint);
        var parentBefore = JsonSerializer.Serialize(parentCheckpoint);
        var childRequest = request with { SessionId = "independent-child-context", Messages = [new("user", [new("text", task)])],
            Subagent = new(parent.RunId, "compacting-child", task, request.SessionId, Instance, GpuIndex: 1) };
        var fork = RunProcessor.ForkSubagentMessages(parentMessages, task, childRequest.Subagent.AgentId);
        var checkpoint = new AgentRunCheckpoint(fork, 0, 0, 0, 0,
            WorkingState: mode == RunMode.Coding ? CodingWorkingState.Create(task) : null,
            SelectedModelId: Model, ContextTools: tools, UseStableSubagentToolCatalog: true, PreserveSessionPromptPrefix: true);
        var child = await repository.CreateSubagentAsync(childRequest, checkpoint, "stored-child-attempt");
        var before = ContextPlanner.EstimateTokens(fork);
        Assert.Equal(!exactOverflow, before >= ContextCompactionPolicy.InputTokenBudget(Context));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Re-enter a durable child through the same manager path used after a
        // reconnect; its ordinary runner also forwards the compaction events.
        var started = await processor.ExecuteSubagentToolAsync(SubagentToolNames.Spawn,
            JsonSerializer.SerializeToElement(new { task }), parent.RunId, "stored-child-attempt", request,
            new(Model, mode == RunMode.Coding ? "coding" : "general", Context), parentMessages.ToList(), timeout.Token);
        Assert.True(started.Succeeded, started.ErrorMessage);
        var result = await processor.ExecuteSubagentToolAsync(SubagentToolNames.Wait,
            JsonSerializer.SerializeToElement(new { runId = child.RunId }), parent.RunId, "wait-child",
            request, new(Model, mode == RunMode.Coding ? "coding" : "general", Context), parentMessages.ToList(), timeout.Token);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(RunState.Completed, (await repository.GetAsync(child.RunId))!.State);
        Assert.Equal(1, handler.Summaries);
        Assert.Equal(1, handler.Answers);
        Assert.Equal(exactOverflow ? 3 : 2, handler.TokenCounts);
        Assert.All(handler.ChatBodies, body => Assert.Equal(Instance, body.GetProperty("model").GetString()));
        Assert.True(JsonElement.DeepEquals(handler.ChatBodies[0].GetProperty("tools"), handler.ChatBodies[1].GetProperty("tools")));
        Assert.Equal("none", handler.ChatBodies[0].GetProperty("tool_choice").GetString());
        Assert.Equal("auto", handler.ChatBodies[1].GetProperty("tool_choice").GetString());
        var role = mode == RunMode.Coding ? "coding" : "general";
        var key = RunProcessor.BuildRunSessionCacheKey(childRequest, child.RunId, role);
        Assert.NotEqual(key, RunProcessor.BuildRunSessionCacheKey(request, parent.RunId, role));
        Assert.NotEmpty(handler.CacheBodies);
        Assert.All(handler.CacheBodies, body =>
        {
            Assert.Equal(Instance, body.GetProperty("model").GetString());
            Assert.Equal(key, body.GetProperty("sessionKey").GetString());
        });
        Assert.All(handler.CacheBodies.Where(body => body.GetProperty("sessionId").ValueKind != JsonValueKind.Null), body =>
        {
            Assert.Equal(childRequest.SessionId, body.GetProperty("sessionId").GetString());
            Assert.Equal(request.SessionId, body.GetProperty("parentSessionId").GetString());
        });
        Assert.Equal(parentBefore, JsonSerializer.Serialize(await repository.GetCheckpointAsync(parent.RunId)));
        // Completed children release their active checkpoint. Inspect the
        // actual resumed native prompt and durable compaction receipt instead.
        var resumed = handler.ChatBodies[1].GetProperty("messages").EnumerateArray().Select(message =>
            new LmChatMessage(message.GetProperty("role").GetString()!, message.GetProperty("content").GetString())).ToArray();
        Assert.Contains(resumed, message => message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) == true);
        Assert.Contains(resumed, message => message.Role == "user" && message.Content?.Contains(task, StringComparison.Ordinal) == true);
        Assert.True(ContextPlanner.EstimateTokens(resumed) < before);
        var events = await repository.GetEventsAfterAsync(child.RunId, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.ModelGeneration && item.Data.GetProperty("state").GetString() == "codingCompacting");
        var compacted = Assert.Single(events, item => item.Type == RunEventTypes.ContextChanged
            && item.Data.GetProperty("compactionPerformed").ValueKind == JsonValueKind.True);
        Assert.Equal(Context, compacted.Data.GetProperty("contextLimit").GetInt32());
        Assert.DoesNotContain(events, item => item.Type is RunEventTypes.ServerToolStarted or RunEventTypes.ClientToolProposed or RunEventTypes.RunFailed);
        var parentEvents = await repository.GetEventsAfterAsync(parent.RunId, 0);
        Assert.Contains(parentEvents, item => item.Type == RunEventTypes.SubagentEvent
            && item.Data.Deserialize<SubagentForwardedEvent>(JsonOptions) is { } forwarded
            && forwarded.RunId == child.RunId && forwarded.Event.Type == RunEventTypes.ContextChanged
            && forwarded.Event.Data.GetProperty("compactionPerformed").ValueKind == JsonValueKind.True);
    }

    [Fact]
    public void CompactionPreservesAnOpenChildToolBatchWithoutInventingItsOutstandingResult()
    {
        var task = new LmChatMessage("user", "Prüfe nur die zugewiesene Teilaufgabe.");
        var first = new LmToolCall("child-read-one", "coding.read", JsonSerializer.SerializeToElement(new { path = "one.txt" }));
        var pending = new LmToolCall("child-read-two", "coding.read", JsonSerializer.SerializeToElement(new { path = "two.txt" }));
        var openBatch = new LmChatMessage("assistant", ToolCalls: [first, pending]);
        var knownResult = new LmChatMessage("tool", "Originaler bereits bestätigter Inhalt.", ToolCallId: first.Id);
        LmChatMessage[] messages = [new("system", "Subagentenrechte bleiben erhalten."), task,
            new("assistant", new string('a', 20_000)), openBatch, knownResult];
        var before = JsonSerializer.Serialize(messages);

        var plan = CodingContextCompactor.Plan(messages, 16_384, force: true)!;
        Assert.NotNull(plan);
        var compacted = CodingContextCompactor.Complete(plan, "Die ältere Vorarbeit bleibt als bestätigter Arbeitsstand erhalten.");

        Assert.Contains(task, compacted);
        Assert.Same(openBatch, compacted[^2]);
        Assert.Same(knownResult, compacted[^1]);
        Assert.DoesNotContain(compacted, message => message.Role == "tool" && message.ToolCallId == pending.Id);
        Assert.Equal(before, JsonSerializer.Serialize(messages));
    }

    private sealed class ChildRuntime(bool exactOverflow) : HttpMessageHandler
    {
        private static readonly string[] ParentTags = ["missum-context-train:32768", "missum-reasoning-mode:llama-native",
            "missum-reasoning-levels:none|high", "missum-reasoning-default:high"];
        private static readonly string[] ChildTags = [.. ParentTags, "missum-agent-instance:subagent", "missum-base-model:" + Model];
        public List<JsonElement> ChatBodies { get; } = [];
        public List<JsonElement> CacheBodies { get; } = [];
        public int TokenCounts { get; private set; }
        public int Summaries { get; private set; }
        public int Answers { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[]
            {
                new { id = Model, tags = ParentTags, status = new { value = "loaded" } },
                new { id = Instance, tags = ChildTags, status = new { value = "loaded" } },
            } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = Context } });
            if (path == "/agents/status") return Json(new { allowed = true, modelId = Model, instanceId = Instance, gpuIndex = 1,
                contextLength = Context, executionMode = "parallel", requiredBytes = 3_000_000_000L, freeBytes = 8_000_000_000L });
            if (path is "/sessions/prepare" or "/sessions/save")
            {
                using var cache = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                CacheBodies.Add(cache.RootElement.Clone());
                return Json(new { status = path == "/sessions/save" ? "saved" : "resident" });
            }
            if (path == "/v1/chat/completions/input_tokens")
            {
                TokenCounts++;
                return Json(new { input_tokens = exactOverflow && TokenCounts == 1 ? 27_000 : 1_024 });
            }
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected native request: " + path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement.Clone();
            ChatBodies.Add(body);
            var summary = body.GetProperty("messages").EnumerateArray().Any(message => message.TryGetProperty("content", out var text)
                && text.ValueKind == JsonValueKind.String && text.GetString()!.Contains("Erstelle eine kompakte Arbeitszusammenfassung", StringComparison.Ordinal));
            if (summary) Summaries++; else Answers++;
            var content = summary ? "Die ältere Arbeit wurde geprüft; die Vorzeichenkonvention bleibt in der zugewiesenen Teilaufgabe unabhängig zu bestätigen."
                : "Die zugewiesene Prüfung ist abgeschlossen; die Vorzeichenkonvention ist konsistent.";
            return Json(new { choices = new[] { new { message = new { role = "assistant", content }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1024, completion_tokens = 24 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

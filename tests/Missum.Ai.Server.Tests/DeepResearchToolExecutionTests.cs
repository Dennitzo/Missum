using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class DeepResearchToolExecutionTests
{
    private const string Model = "coding/ResearchFixture-Q4~123456";
    private static readonly string[] ResearchTools = ["web.search", "web.fetch", "web.deepResearch"];

    [Fact]
    public async Task DirectResearchUsesExistingModelWithoutAgentAdmissionAndReplaysDurableReport()
    {
        using var context = new TestServerContext();
        using var handler = new ResearchHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var provider = CreateProvider(context, runtime);
        var repository = provider.GetRequiredService<RunRepository>();
        var request = Request();
        var parent = (await repository.CreateAsync(request, null)).Snapshot;
        var processor = provider.GetRequiredService<RunProcessor>();
        var args = JsonSerializer.SerializeToElement(new { task = "Vergleiche öffentliche API-Dokumentation.", profile = "web" });
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var first = await processor.ExecuteCodingDeepResearchAsync(parent.RunId, "tool-operation", args,
            Model, "general", 32768, 24, 27, tools, null, cancellationToken: timeout.Token);
        var second = await processor.ExecuteCodingDeepResearchAsync(parent.RunId, "tool-operation", args,
            Model, "general", 32768, 24, 27, tools, null, cancellationToken: timeout.Token);

        Assert.True(first.Result.Succeeded, first.Result.Result.GetRawText());
        Assert.True(JsonElement.DeepEquals(first.Result.Result, second.Result.Result));
        Assert.Equal(ResearchHandler.FullReport, first.Result.Result.GetProperty("summary").GetString());
        Assert.Equal(2, handler.ModelCalls);
        Assert.Empty(await repository.GetSubagentRunsAsync(parent.RunId));
        var events = await repository.GetEventsAfterAsync(parent.RunId, 0);
        Assert.DoesNotContain(events, item => item.Type is RunEventTypes.SubagentStarted or RunEventTypes.SubagentCompleted or RunEventTypes.SubagentEvent);
        Assert.Contains(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "model");
        Assert.Single(events, item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "operation"
            && item.Data.GetProperty("status").GetString() is "completed" or "failed");
        Assert.All(handler.Models, model => Assert.Equal(Model, model));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ResearchReusesTheOwningSessionCacheAndExactMainPrefixAcrossAllPhases(bool coding, bool restored)
    {
        using var context = new TestServerContext();
        using var handler = new ResearchHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var provider = CreateProvider(context, runtime);
        var repository = provider.GetRequiredService<RunRepository>();
        var request = coding ? Request() with { Mode = RunMode.Coding, PreferredCodingModelId = Model,
            PreferredGeneralModelId = null, CodingOptions = new(WorkspacePath: "C:\\physics", ContinueSessionContext: true) } : Request();
        var parent = (await repository.CreateAsync(request, null)).Snapshot;
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        var definitions = RunProcessor.CreateModelToolDefinitions(tools, null, directTools: true);
        LmChatMessage[] prefix = [new("system", "Hauptkontext bleibt unverändert."),
            new("user", "Prüfe den Tensor mit der vorher abgeleiteten Konvention (-,+,+,+)."),
            new("assistant", "Die Konvention wurde bereits festgelegt.", ReasoningContent: "Nicht erneut herleiten.")];
        var role = coding ? "coding" : "general";
        var cacheKey = RunProcessor.BuildRunSessionCacheKey(request, parent.RunId, role);
        _ = await runtime.CompleteChatWithCacheOwnershipAsync(Model, prefix, definitions, request.SessionId, null,
            modelRole: role, sessionCacheKey: cacheKey);
        var pending = new LmToolCall("parent-research", "web.deepResearch",
            JsonSerializer.SerializeToElement(new { task = "Prüfe die Vorzeichenkonvention.", profile = "web" }));
        var parentMessages = prefix.Append(new LmChatMessage("assistant", ToolCalls: [pending])).ToArray();
        var before = JsonSerializer.Serialize(parentMessages);
        if (restored)
            await repository.SaveCheckpointAsync(parent.RunId, new(parentMessages, 1, 0, 100, 20,
                ActiveToolCalls: [pending], LastModelTools: definitions));
        var result = await provider.GetRequiredService<RunProcessor>().ExecuteCodingDeepResearchAsync(parent.RunId,
            "owned-operation", pending.Arguments, Model, role, 32768, 24, 27, tools, null,
            parentMessages: restored ? null : parentMessages, parentTools: restored ? null : definitions);
        Assert.True(result.Result.Succeeded, result.Result.Result.GetRawText());
        Assert.Equal(before, JsonSerializer.Serialize(parentMessages));
        _ = await runtime.CompleteChatWithCacheOwnershipAsync(Model,
            parentMessages.Append(new LmChatMessage("tool", result.Result.Result.GetRawText(), ToolCallId: pending.Id)).ToArray(),
            definitions, request.SessionId, null, modelRole: role, sessionCacheKey: cacheKey);

        Assert.Equal(4, handler.ChatBodies.Count); // main, research plan, research synthesis, main
        Assert.All(handler.CacheBodies, body => Assert.Equal(cacheKey, body.GetProperty("sessionKey").GetString()));
        Assert.Equal(4, handler.CacheBodies.Count(body => body.GetProperty("sessionId").GetString() == request.SessionId));
        var checkpoints = handler.CacheBodies.Where(body => body.GetProperty("sessionId").ValueKind == JsonValueKind.Null).ToArray();
        Assert.Equal(4, checkpoints.Length);
        Assert.True(checkpoints[0].GetProperty("force").GetBoolean());
        Assert.False(checkpoints[1].GetProperty("force").GetBoolean());
        Assert.False(checkpoints[2].GetProperty("force").GetBoolean());
        Assert.True(checkpoints[3].GetProperty("force").GetBoolean());
        var mainBody = handler.ChatBodies[0];
        foreach (var body in handler.ChatBodies.Skip(1))
        {
            Assert.True(JsonElement.DeepEquals(mainBody.GetProperty("tools"), body.GetProperty("tools")));
            Assert.Equal(0, body.GetProperty("id_slot").GetInt32());
            var original = mainBody.GetProperty("messages").EnumerateArray().ToArray();
            var actual = body.GetProperty("messages").EnumerateArray().ToArray();
            for (var i = 0; i < original.Length; i++) Assert.True(JsonElement.DeepEquals(original[i], actual[i]));
        }
        var planBody = handler.ChatBodies[1];
        var synthesisBody = handler.ChatBodies[2];
        Assert.Equal("none", planBody.GetProperty("tool_choice").GetString());
        Assert.Equal("none", synthesisBody.GetProperty("tool_choice").GetString());
        // The real DeepSeek PEG parser rejects bare JSON with response_format
        // and retained tools. Our handler reproduces its unterminated SSE result.
        Assert.False(planBody.TryGetProperty("response_format", out _));
        Assert.False(synthesisBody.TryGetProperty("response_format", out _));
        Assert.Equal(0, handler.NativeGrammarConflicts);
        var planMessages = planBody.GetProperty("messages").EnumerateArray().ToArray();
        var synthesisMessages = synthesisBody.GetProperty("messages").EnumerateArray().ToArray();
        for (var i = 0; i < planMessages.Length; i++) Assert.True(JsonElement.DeepEquals(planMessages[i], synthesisMessages[i]));
        Assert.Equal("assistant", synthesisMessages[planMessages.Length].GetProperty("role").GetString());
        Assert.Contains("sourceLimit", synthesisMessages[planMessages.Length].GetProperty("content").GetString());
        Assert.Empty(await repository.GetSubagentRunsAsync(parent.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAndSteeringCancelTheActualResearchModelCallWithoutAnyChild(bool steer)
    {
        using var context = new TestServerContext();
        using var handler = new ResearchHandler { HoldModel = true };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var provider = CreateProvider(context, runtime);
        var repository = provider.GetRequiredService<RunRepository>();
        var request = Request();
        var parent = (await repository.CreateAsync(request, null)).Snapshot;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var execution = provider.GetRequiredService<RunProcessor>().ExecuteCodingDeepResearchAsync(parent.RunId, "stopped-tool",
            JsonSerializer.SerializeToElement(new { task = "Vergleiche öffentliche API-Dokumentation.", profile = "web" }),
            Model, "general", 32768, 24, 27, new AgentToolCatalog().GetAvailableTools(request), null, cancellationToken: stop.Token);
        await handler.Entered.Task.WaitAsync(stop.Token);
        if (steer)
        {
            _ = await repository.AcceptSteeringAsync(parent.RunId, new(request.SessionId!, "new-direction", "Andere Frage."));
            Assert.Equal("run.steered", (await execution.WaitAsync(TimeSpan.FromSeconds(10))).Result.ErrorCode);
        }
        else
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        }
        Assert.True(handler.Cancelled);
        Assert.Empty(await repository.GetSubagentRunsAsync(parent.RunId));
        Assert.Contains(await repository.GetEventsAfterAsync(parent.RunId, 0), item => item.Type == RunEventTypes.DeepResearchProgress
            && item.Data.GetProperty("phase").GetString() == "operation"
            && item.Data.GetProperty("status").GetString() == (steer ? "interrupted" : "cancelled"));
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Vergleiche API-Dokumentation.")])], AllowedServerTools: ResearchTools,
        PreferredGeneralModelId: Model, SessionId: "research-tool-session");

    private static ServiceProvider CreateProvider(TestServerContext context, ModelRuntimeClient runtime)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        return services.BuildServiceProvider();
    }

    private sealed class ResearchHandler : HttpMessageHandler
    {
        private static readonly string[] ModelTags = ["missum-context-train:32768"];
        internal static string FullReport { get; } = "Vollständiger Bericht: " + new string('ü', 40_000) + " Ende.";
        public bool HoldModel { get; init; }
        public bool Cancelled { get; private set; }
        public int ModelCalls { get; private set; }
        public List<string?> Models { get; } = [];
        public List<JsonElement> ChatBodies { get; } = [];
        public List<JsonElement> CacheBodies { get; } = [];
        public int NativeGrammarConflicts { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/agents/", StringComparison.Ordinal)) throw new InvalidOperationException("Research must never use subagent admission.");
            if (path == "/v1/models") return Json(new { data = new[]
                { new { id = Model, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path is "/sessions/prepare" or "/sessions/save")
            {
                using var cache = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                CacheBodies.Add(cache.RootElement.Clone());
                return Json(new { status = path == "/sessions/save" ? "saved" : "resident" });
            }
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ChatBodies.Add(body.RootElement.Clone());
            Models.Add(body.RootElement.GetProperty("model").GetString());
            ModelCalls++;
            Entered.TrySetResult();
            if (HoldModel)
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            if (body.RootElement.TryGetProperty("response_format", out _))
            {
                NativeGrammarConflicts++;
                // Native DeepSeek emits content but never a finish event when
                // its response_format PEG parser cannot consume bare JSON.
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"questions\\\":[]}\"}}]}\n\n",
                    Encoding.UTF8, "text/event-stream") };
            }
            var phaseInstruction = body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString() ?? "";
            var isResearch = body.RootElement.TryGetProperty("tool_choice", out var choice) && choice.GetString() == "none";
            if (!isResearch)
                return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Hauptantwort." }, finish_reason = "stop" } } });
            var isPlan = phaseInstruction.Contains("JSON-Objekt für research.plan", StringComparison.Ordinal);
            object arguments = isPlan
                ? new { questions = Array.Empty<object>(), sourceLimit = 0 }
                : new { summary = FullReport, findings = Array.Empty<object>(), uncertainties = new[] { "Keine Originalquelle gelesen; Frage bleibt offen." } };
            var delta = new { content = JsonSerializer.Serialize(arguments) };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new
                { choices = new[] { new { index = 0, delta, finish_reason = "stop" } } })
                + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

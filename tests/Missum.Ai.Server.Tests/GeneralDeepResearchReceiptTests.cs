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

public sealed class GeneralDeepResearchReceiptTests
{
    private static readonly string[] ResearchTools = ["web.search", "web.fetch", "web.deepResearch"];

    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", false)]
    public async Task DurablePreflightResultIsHandedToNextModelTurnAsCompletedCallWithoutRepeatingResearch(string status, bool success)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test");
        var handler = new AnswerOnlyHandler();
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        const string task = "Vergleiche die vorhandenen Originalbelege.";
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", task)])], AllowedServerTools: ResearchTools,
            DeepResearch: true, PreferredGeneralModelId: AnswerOnlyHandler.ModelId);
        var runId = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var operationId = RunProcessor.CreateServerToolOperationId(runId, "general-research", 0, 0, "deep-research");
        var result = JsonSerializer.SerializeToElement(new
        {
            success, summary = "Bereits recherchierter Originalbericht.", conclusionStatus = success ? "supported" : "unresolved",
            budget = new { modelCalls = 2, toolCalls = 1 }, findings = Array.Empty<object>(), sources = Array.Empty<object>(),
        });
        await repository.AppendDeepResearchProgressAsync(new(operationId, runId, 0, "operation", status, "Deep Research",
            ErrorCode: success ? null : "web.deepResearch.incomplete", Result: result));

        // Simulate a restart after the durable operation result and before its
        // normal checkpoint. The handler rejects every inner research turn.
        await provider.GetRequiredService<RunProcessor>().ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await repository.GetAsync(runId))!.State);
        Assert.Equal(1, handler.ChatCalls);
        Assert.Equal(operationId, handler.CallId);
        Assert.Equal(task, handler.CallTask);
        Assert.Equal(result.GetRawText(), handler.Receipt);
        var events = await repository.GetEventsAfterAsync(runId, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.DeepResearchProgress);
    }

    [Fact]
    public void AppendingTheSameReceiptPreservesOneCallAndOneMatchingResult()
    {
        var messages = new List<LmChatMessage> { new("user", "Auftrag") };
        var call = new LmToolCall("operation", "web.deepResearch", JsonSerializer.SerializeToElement(new { task = "Auftrag" }));
        var result = JsonSerializer.SerializeToElement(new { success = true, summary = "Originalergebnis" });
        RunProcessor.AppendExplicitDeepResearchReceipt(messages, call, result);
        var original = messages.ToArray();
        RunProcessor.AppendExplicitDeepResearchReceipt(messages, call, result);
        Assert.Equal(original, messages);
        Assert.Single(messages.SelectMany(message => message.ToolCalls ?? []));
        Assert.Equal(call.Id, Assert.Single(messages, message => message.Role == "tool").ToolCallId);
    }

    private sealed class AnswerOnlyHandler : HttpMessageHandler
    {
        internal const string ModelId = "coding/ReceiptFixture-Q4~abc123";
        private static readonly string[] ModelTags = ["missum-context-train:32768"];
        internal int ChatCalls { get; private set; }
        internal string? CallId { get; private set; }
        internal string? CallTask { get; private set; }
        internal string? Receipt { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            ChatCalls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var messages = body.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            var receipt = Assert.Single(messages, message => message.GetProperty("role").GetString() == "tool");
            var id = receipt.GetProperty("tool_call_id").GetString();
            var assistant = Assert.Single(messages, message => message.TryGetProperty("tool_calls", out var calls) && calls.GetArrayLength() > 0);
            var call = Assert.Single(assistant.GetProperty("tool_calls").EnumerateArray());
            Assert.Equal(id, call.GetProperty("id").GetString());
            Assert.Equal(ModelRuntimeClient.ToTransportToolName("web.deepResearch"), call.GetProperty("function").GetProperty("name").GetString());
            CallId = id;
            CallTask = JsonSerializer.Deserialize<JsonElement>(call.GetProperty("function").GetProperty("arguments").GetString()!).GetProperty("task").GetString();
            Receipt = receipt.GetProperty("content").GetString();
            Assert.Contains("Bereits recherchierter Originalbericht.", receipt.GetProperty("content").GetString(), StringComparison.Ordinal);
            return Json(new { choices = new[] { new { message = new { content = "Das vorhandene Rechercheergebnis ist aufbereitet." }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 100, completion_tokens = 10 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

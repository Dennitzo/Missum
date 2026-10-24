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
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/agents/", StringComparison.Ordinal)) throw new InvalidOperationException("Research must never use subagent admission.");
            if (path == "/v1/models") return Json(new { data = new[]
                { new { id = Model, tags = ModelTags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Models.Add(body.RootElement.GetProperty("model").GetString());
            ModelCalls++;
            Entered.TrySetResult();
            if (HoldModel)
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            var name = body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString();
            object arguments = ModelCalls == 1
                ? new { questions = Array.Empty<object>(), sourceLimit = 0 }
                : new { summary = FullReport, findings = Array.Empty<object>(), uncertainties = new[] { "Keine Originalquelle gelesen; Frage bleibt offen." } };
            var delta = new { tool_calls = new[] { new { index = 0, id = "research-" + ModelCalls,
                type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } } } };
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + JsonSerializer.Serialize(new
                { choices = new[] { new { index = 0, delta, finish_reason = "tool_calls" } } })
                + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

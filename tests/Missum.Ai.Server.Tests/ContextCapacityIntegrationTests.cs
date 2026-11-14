using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ContextCapacityIntegrationTests
{
    private const string ModelId = "coding/ContextCapacity-Fixture~capacity";

    [Theory]
    [InlineData(true, 1_048_576, 1_048_576, 1_048_576, 943_718)]
    [InlineData(false, 1_048_576, 1_048_576, 1_048_576, 943_718)]
    [InlineData(true, 65_536, 1_048_576, 65_536, 58_982)]
    [InlineData(true, 1_048_576, 65_536, 65_536, 58_982)]
    [InlineData(true, 258_000, 1_048_576, 258_000, 232_200)]
    public async Task ContextTelemetrySeparatesEffectiveModelWindowFromWorkflowInputBudget(
        bool science, int loadedContext, int requestedContext, int expectedWindow, int expectedInputBudget)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = ModelId;
        using var native = new NativeHandler(loadedContext);
        using var http = new HttpClient(native);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Prüfe die aktuelle Forschungsfrage.")])],
            ClientCapabilities: science ? ["research.deliverables"] : [], AllowedServerTools: [],
            SessionId: "context-capacity", PreferredGeneralModelId: ModelId,
            Limits: new(MaximumContextTokens: requestedContext),
            ResearchOptions: science ? new(ProjectId: "context-capacity", ProtocolVersion: 2) : null);
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        // Resume immediately before a model turn, after the client's initial
        // canonical read. This exercises real preparation, planning and telemetry
        // without requiring a publication or a second model response.
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint(
            [new("user", "Prüfe die aktuelle Forschungsfrage.")], 0, 0, 0, 0));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = provider.GetRequiredService<RunProcessor>().ProcessAsync(run, cancellation.Token);
        try
        {
            await native.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var changed = Assert.Single(await repository.GetEventsAfterAsync(run, 0),
                item => item.Type == RunEventTypes.ContextChanged);
            Assert.Equal(expectedWindow, changed.Data.GetProperty("contextLimit").GetInt32());
            Assert.Equal(expectedInputBudget, changed.Data.GetProperty("inputTokenBudget").GetInt32());
            Assert.False(changed.Data.GetProperty("wasCompacted").GetBoolean());
            Assert.InRange(changed.Data.GetProperty("estimatedInputTokens").GetInt32(), 1, expectedInputBudget);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void LegacyContextEventsDeserializeWithoutAnInputBudget()
    {
        var restored = JsonSerializer.Deserialize<ContextChangedEvent>(
            """{"estimatedInputTokens":1000,"contextLimit":32768,"loadedFiles":0,"wasCompacted":false}""",
            MissumAiProtocol.CreateJsonOptions());
        Assert.NotNull(restored);
        Assert.Equal(32768, restored.ContextLimit);
        Assert.Null(restored.InputTokenBudget);
    }

    private sealed class NativeHandler(int loadedContext) : HttpMessageHandler
    {
        private static readonly string[] ModelTags = ["missum-context-train:1048576"];
        internal TaskCompletionSource InferenceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] {
                new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } }
            } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = loadedContext } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 256 });
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { status = "ready" });
            if (path == "/v1/chat/completions")
            {
                InferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            throw new InvalidOperationException("Unexpected native endpoint: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

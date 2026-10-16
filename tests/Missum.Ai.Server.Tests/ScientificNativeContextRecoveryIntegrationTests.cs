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

public sealed class ScientificNativeContextRecoveryIntegrationTests
{
    private const string ModelId = "coding/ScienceNativeContext-Fixture~recovery";
    private const string TaskText = "Prüfe den Doppelspalt quantitativ weiter und bewahre die bisherigen Befunde.";

    [Fact]
    public async Task NativeOverflowCompactsSavedScienceContextBeforeFirstGenerationWithoutReexecutingTools()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        context.Options.GeneralModelId = ModelId;
        using var native = new NativeHandler();
        using var http = new HttpClient(native);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", TaskText)])],
            ClientCapabilities: ["coding", "workspace"], AllowedServerTools: [],
            SessionId: "bohm-native-context", PreferredGeneralModelId: ModelId, PreferredCodingModelId: ModelId,
            Limits: new(MaximumContextTokens: 262_144),
            CodingOptions: new(WorkspacePath: context.Root));
        var run = (await repository.CreateAsync(request, null)).Snapshot.RunId;
        var messages = History();
        var sourceJson = JsonSerializer.Serialize(messages);
        var estimated = ContextPlanner.EstimateTokens(messages);
        Assert.InRange(estimated, 10_000, ContextPlanner.ComputeInputTokenBudget(262_144, null) - 1);
        await repository.SaveCheckpointAsync(run, new AgentRunCheckpoint(messages, 20, 20, 200_000, 10_000,
            PreserveSessionPromptPrefix: true, ResearchManagedByAgent: true));
        var transcript = await repository.AppendEventAsync(run, "fixture.originalTranscript", new { messages });
        for (var index = 0; index < 20; index++)
            await repository.AppendEventAsync(run, RunEventTypes.ServerToolCompleted,
                new { toolCallId = "history-" + index, name = "coding.read", success = true, text = "Originalbeleg " + index });
        var originalEvents = (await repository.GetEventsAfterAsync(run, 0))
            .ToDictionary(item => item.Id, item => item.Data.GetRawText());
        var originalRequest = JsonSerializer.Serialize(await repository.GetRequestAsync(run));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = provider.GetRequiredService<RunProcessor>().ProcessAsync(run, cancellation.Token);
        try
        {
            await native.InferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var events = await repository.GetEventsAfterAsync(run, 0);
            var compacted = Assert.Single(events, item => item.Type == RunEventTypes.ContextChanged
                && item.Data.GetProperty("wasCompacted").GetBoolean());
            Assert.Equal(262_144, compacted.Data.GetProperty("contextLimit").GetInt32());
            var digits = string.Concat((compacted.Data.GetProperty("detail").GetString() ?? "").Where(char.IsDigit));
            Assert.Contains("262305", digits, StringComparison.Ordinal);
            Assert.Contains("262144", digits, StringComparison.Ordinal);
            Assert.True(compacted.Data.GetProperty("estimatedInputTokens").GetInt32() < estimated);
            Assert.Equal(2, native.TokenCounts);
            Assert.Equal(1, native.Generations);
            Assert.True(native.PromptLengths[1] < native.PromptLengths[0]);
            var checkpoint = (await repository.GetCheckpointAsync(run))!;
            Assert.Equal(20, checkpoint.ToolCallCount);
            Assert.Equal(20, checkpoint.RoundCount);
            Assert.Equal(1, checkpoint.CompactionCount);
            Assert.Contains(checkpoint.Messages, message => message.Role == "user" && message.Content == TaskText);
            Assert.True(ContextPlanner.EstimateTokens(checkpoint.Messages) < estimated);
            Assert.Equal(sourceJson, JsonSerializer.Serialize(messages));
            Assert.Equal(originalRequest, JsonSerializer.Serialize(await repository.GetRequestAsync(run)));
            Assert.All(originalEvents, original => Assert.Equal(original.Value,
                Assert.Single(events, item => item.Id == original.Key).Data.GetRawText()));
            Assert.Contains(events, item => item.Id == transcript.Id);
            Assert.Equal(20, events.Count(item => item.Type == RunEventTypes.ServerToolCompleted));
            Assert.DoesNotContain(events, item => item.Type is RunEventTypes.ServerToolStarted
                or RunEventTypes.ClientToolProposed or RunEventTypes.RunFailed);
            AssertValidPairs(checkpoint.Messages);
        }
        finally
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public void RecoveryCannotDeleteOrTruncateAnOversizedProtectedUserTask()
    {
        LmChatMessage[] messages = [new("system", "Bewahre die Forschungsfrage."), new("user", new string('q', 30_000))];
        var original = JsonSerializer.Serialize(messages);
        var overflow = new NativeContextBudgetException(262_305, 262_144);

        var actual = Assert.Throws<NativeContextBudgetException>(() => ScientificNativeContextRecovery.Prepare(messages, overflow, null));

        Assert.Same(overflow, actual);
        Assert.Equal(original, JsonSerializer.Serialize(messages));
    }

    [Fact]
    public void TinyUnreduciblePromptKeepsItsExactOverflowDiagnosis()
    {
        LmChatMessage[] messages = [new("user", "Behalte diesen Auftrag vollständig.")];
        var overflow = new NativeContextBudgetException(262_305, 262_144);

        Assert.Same(overflow, Assert.Throws<NativeContextBudgetException>(() =>
            ScientificNativeContextRecovery.Prepare(messages, overflow, null)));
    }

    private static LmChatMessage[] History()
    {
        var messages = new List<LmChatMessage> { new("system", "Forschungsregeln."), new("user", TaskText) };
        for (var index = 0; index < 20; index++)
        {
            var call = new LmToolCall("history-" + index, "coding.read",
                JsonSerializer.SerializeToElement(new { path = "work/history-" + index + ".txt" }));
            messages.Add(new("assistant", ToolCalls: [call], ReasoningContent: new string('r', 15_000)));
            messages.Add(new("tool", JsonSerializer.Serialize(new
            {
                success = true, path = "work/history-" + index + ".txt", content = "Prüfbefund " + index + "\n" + new string('x', 5000),
            }), ToolCallId: call.Id));
        }
        messages.Add(new("assistant", "Die bisherigen Befunde sind gesichert. Als Nächstes folgt die quantitative Prüfung."));
        return messages.ToArray();
    }

    private static void AssertValidPairs(IReadOnlyList<LmChatMessage> messages)
    {
        var calls = messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id).ToArray();
        var receipts = messages.Where(message => message.Role == "tool").Select(message => message.ToolCallId).ToArray();
        Assert.Equal(calls.Order(), receipts.Order());
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        private static readonly string[] ModelTags = ["missum-context-train:1048576"];
        internal int TokenCounts { get; private set; }
        internal int Generations { get; private set; }
        internal List<int> PromptLengths { get; } = [];
        internal TaskCompletionSource InferenceStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[]
            {
                new { id = ModelId, tags = ModelTags, status = new { value = "loaded" } },
            } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 262_144 } });
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { status = "resident" });
            if (path == "/v1/chat/completions/input_tokens")
            {
                TokenCounts++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                PromptLengths.Add(body.Length);
                return Json(new { input_tokens = TokenCounts == 1 ? 262_305 : 100_000 });
            }
            if (path == "/v1/chat/completions")
            {
                Generations++;
                InferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            throw new InvalidOperationException("Unexpected native endpoint: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

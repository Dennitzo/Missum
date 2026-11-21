using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class NativePrefillClientTests
{
    private const string Model = "coding/Qwen3-Test~prefill";
    private static readonly LmToolDefinition Tool = new("workspace.read", "Read a file",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } }));

    [Theory]
    [InlineData("prepared")]
    [InlineData("skipped")]
    [InlineData("unavailable")]
    public async Task ManagedSessionPrefillsTheExactBudgetedChatRequest(string status)
    {
        using var handler = new PrefillHandler { PrefillStatus = status };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await client.CompleteChatAsync(Model, [new("user", "Read README.md")], [Tool], modelRole: "coding",
            reasoningEffort: "on", maximumOutputTokens: 17, sessionCacheKey: "session-a");

        Assert.True(handler.Paths.IndexOf("/sessions/prefill") < handler.Paths.IndexOf("/v1/chat/completions"));
        Assert.Equal(8082, handler.PrefillPort);
        var prefill = JsonNode.Parse(handler.PrefillBody!)!;
        var chat = JsonNode.Parse(handler.ChatBody!)!;
        Assert.Equal(Model, (string?)prefill["model"]);
        Assert.Equal("session-a", (string?)prefill["sessionKey"]);
        Assert.True((bool?)prefill["streamProgress"]);
        Assert.True(JsonNode.DeepEquals(prefill["requestBody"], chat));
        Assert.Equal(17, (int?)chat["max_tokens"]);
        Assert.NotNull(chat["tools"]);
        Assert.NotNull(chat["chat_template_kwargs"]);
    }

    [Fact]
    public async Task EarlyCheckpointProgressUsesTheFullPromptAndPrecedesNormalChat()
    {
        using var handler = new PrefillHandler { StreamPrefill = true, InputTokens = 100_000 };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var percentages = new List<double>();
        await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding",
            sessionCacheKey: "session-a", nativeProgress: (value, _) =>
            {
                if (value is { State: "promptProcessing", PromptTokens: 100_000, PromptProgress: { } fraction })
                {
                    Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
                    percentages.Add(fraction);
                }
                return ValueTask.CompletedTask;
            });

        Assert.Equal([0, .01, .05], percentages);
        Assert.Contains("/v1/chat/completions", handler.Paths);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(65_536)]
    public async Task ExactShortTextPromptSkipsTheSupervisorPrefill(int inputTokens)
    {
        using var handler = new PrefillHandler { InputTokens = inputTokens };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding", sessionCacheKey: "session-a");
        Assert.Contains("/v1/chat/completions/input_tokens", handler.Paths);
        Assert.DoesNotContain("/sessions/prefill", handler.Paths);
        Assert.Contains("/sessions/save", handler.Paths);
    }

    [Theory]
    [InlineData(false, "session-a")]
    [InlineData(true, null)]
    public async Task UnmanagedRuntimeOrMissingSessionDoesNotPrefill(bool managed, string? sessionKey)
    {
        using var handler = new PrefillHandler { Managed = managed };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding", sessionCacheKey: sessionKey);
        Assert.DoesNotContain("/sessions/prefill", handler.Paths);
        Assert.Contains("/v1/chat/completions", handler.Paths);
    }

    [Fact]
    public async Task OldSupervisorWithoutPrefillFallsBackToNormalChat()
    {
        using var handler = new PrefillHandler { PrefillNotFound = true };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding", sessionCacheKey: "session-a");
        Assert.Contains("/sessions/prefill", handler.Paths);
        Assert.Contains("/v1/chat/completions", handler.Paths);
    }

    [Fact]
    public async Task SupervisorCancellationNeverStartsNormalChat()
    {
        using var handler = new PrefillHandler { PrefillStatus = "cancelled" };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await Assert.ThrowsAsync<ModelProviderRequestException>(() => client.CompleteChatAsync(
            Model, [new("user", "Hallo")], [], modelRole: "coding", sessionCacheKey: "session-a")
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("/sessions/prefill", handler.Paths);
        Assert.Contains("/sessions/barrier", handler.Paths);
        Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
    }

    [Theory]
    [InlineData(5000, 1000)]
    [InlineData(500, 500)]
    public async Task MetricsCountPrefillWorkWithoutCallingItAnOldCacheHit(int chatCached, int expectedCached)
    {
        using var handler = new PrefillHandler { PreviousCached = 1000, PrefillEvaluated = 4000, ChatCached = chatCached };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var progress = new List<ModelRuntimeProgress>();
        var result = await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding",
            sessionCacheKey: "session-a", nativeProgress: (value, _) => { progress.Add(value); return ValueTask.CompletedTask; });
        Assert.Equal(expectedCached, result.Metrics!.CachedPromptTokens);
        Assert.Equal(4020, result.Metrics.PromptEvaluatedTokens);
        Assert.All(progress.Where(value => value.CachedPromptTokens.HasValue),
            value => Assert.True(value.CachedPromptTokens <= expectedCached));
    }

    [Theory]
    [InlineData(true, 1000, 4020)]
    [InlineData(false, 5000, 20)]
    public async Task UnavailablePrefillWaitsForBarrierAndCountsOnlyStartedWork(
        bool started, int expectedCached, int expectedEvaluated)
    {
        using var handler = new PrefillHandler { PrefillStatus = "unavailable", PrefillWasStarted = started,
            PreviousCached = 1000, PrefillEvaluated = 4000, ChatCached = 5000 };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var result = await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding",
            sessionCacheKey: "session-a").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(handler.Paths.IndexOf("/sessions/prefill") < handler.Paths.IndexOf("/sessions/barrier"));
        Assert.True(handler.Paths.IndexOf("/sessions/barrier") < handler.Paths.IndexOf("/v1/chat/completions"));
        Assert.Equal(expectedCached, result.Metrics!.CachedPromptTokens);
        Assert.Equal(expectedEvaluated, result.Metrics.PromptEvaluatedTokens);
    }

    [Fact]
    public async Task TransientBarrierDisconnectIsRetriedBeforeNormalChat()
    {
        using var handler = new PrefillHandler { PrefillStatus = "unavailable", BarrierTransportFailures = 1 };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await client.CompleteChatAsync(Model, [new("user", "Hallo")], [], modelRole: "coding",
            sessionCacheKey: "session-a").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, handler.BarrierAttempts);
        Assert.Single(handler.Paths, path => path == "/sessions/prefill");
        Assert.True(handler.Paths.LastIndexOf("/sessions/barrier") < handler.Paths.IndexOf("/v1/chat/completions"));
    }

    [Theory]
    [InlineData("idle", 3, 3)]
    [InlineData("unavailable", 0, 1)]
    public async Task UnconfirmedSlotNeverStartsNormalChat(string barrierStatus, int transportFailures, int expectedAttempts)
    {
        using var handler = new PrefillHandler { PrefillStatus = "unavailable", BarrierStatus = barrierStatus,
            BarrierTransportFailures = transportFailures };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        await Assert.ThrowsAsync<NativePrefillBarrierException>(() => client.CompleteChatAsync(
            Model, [new("user", "Hallo")], [], modelRole: "coding", sessionCacheKey: "session-a")
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(expectedAttempts, handler.BarrierAttempts);
        Assert.Single(handler.Paths, path => path == "/sessions/prefill");
        Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task CancelledPrefillKeepsTheTurnLeaseUntilTheSupervisorBarrierCompletes(int transportFailures)
    {
        using var handler = new PrefillHandler { HoldPrefill = true, BarrierTransportFailures = transportFailures };
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        using var stop = new CancellationTokenSource();
        var first = client.CompleteChatAsync(Model, [new("user", "First")], [], modelRole: "coding",
            sessionCacheKey: "session-a", cancellationToken: stop.Token);
        try
        {
            await handler.PrefillStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.Cancel();
            await handler.BarrierStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(first.IsCompleted);
            var second = client.CompleteChatAsync(Model, [new("user", "Second")], [], modelRole: "coding");
            await Task.Delay(100);
            Assert.DoesNotContain("/v1/chat/completions", handler.Paths);
            Assert.False(second.IsCompleted);
            handler.ReleaseBarrier.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
            await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(handler.Paths, path => path == "/v1/chat/completions");
            Assert.Equal(transportFailures + 1, handler.BarrierAttempts);
        }
        finally
        {
            stop.Cancel();
            handler.ReleaseBarrier.TrySetResult();
        }
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class PrefillHandler : HttpMessageHandler
    {
        public bool Managed { get; init; } = true;
        public bool PrefillNotFound { get; init; }
        public bool HoldPrefill { get; init; }
        public bool StreamPrefill { get; init; }
        public bool PrefillWasStarted { get; init; }
        public string PrefillStatus { get; init; } = "prepared";
        public string BarrierStatus { get; init; } = "idle";
        public int BarrierTransportFailures { get; init; }
        public int BarrierAttempts { get; private set; }
        public int PreviousCached { get; init; } = 1000;
        public int PrefillEvaluated { get; init; } = 4000;
        public int ChatCached { get; init; } = 5000;
        public int InputTokens { get; init; } = 65_537;
        public TaskCompletionSource PrefillStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BarrierStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBarrier { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Paths { get; } = [];
        public string? PrefillBody { get; private set; }
        public string? ChatBody { get; private set; }
        public int PrefillPort { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);
            if (path == "/v1/models")
            {
                var tags = new List<string> { "missum-context-train:131072", "missum-reasoning-mode:llama-toggle",
                    "missum-reasoning-levels:none|on", "missum-reasoning-default:on" };
                if (Managed) tags.Add("missum-gpu-policy:single-preferred-v1");
                return Json(new { data = new[] { new { id = Model, tags, status = new { value = "loaded" } } } });
            }
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 131072 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = InputTokens });
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { status = "saved" });
            if (path == "/sessions/prefill")
            {
                PrefillPort = request.RequestUri.Port;
                PrefillBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (PrefillNotFound) return new(HttpStatusCode.NotFound);
                if (HoldPrefill)
                {
                    PrefillStarted.TrySetResult();
                    await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
                }
                var result = new { status = PrefillStatus, started = PrefillWasStarted,
                    cachedTokens = PreviousCached, evaluatedTokens = PrefillEvaluated };
                if (!StreamPrefill) return Json(result);
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"prompt_progress\":{\"total\":100000,\"processed\":1000}}\n"
                        + "{\"prompt_progress\":{\"total\":100000,\"processed\":5000}}\n"
                        + JsonSerializer.Serialize(result) + "\n", Encoding.UTF8, "application/x-ndjson"),
                };
            }
            if (path == "/sessions/barrier")
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                if (++BarrierAttempts <= BarrierTransportFailures)
                    throw new HttpRequestException("The response ended prematurely.", new HttpIOException(HttpRequestError.ResponseEnded));
                BarrierStarted.TrySetResult();
                if (HoldPrefill) await ReleaseBarrier.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                return Json(new { status = BarrierStatus });
            }
            if (path == "/v1/chat/completions")
            {
                ChatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var frame = JsonSerializer.Serialize(new { choices = new[] { new { index = 0,
                    delta = new { content = "OK" }, finish_reason = "stop" } },
                    prompt_progress = new { total = 5020, cache = ChatCached, processed = 5020 },
                    usage = new { prompt_tokens = 5020, completion_tokens = 1,
                        prompt_tokens_details = new { cached_tokens = ChatCached } },
                    timings = new { prompt_n = 20, prompt_ms = 10 } });
                return new(HttpStatusCode.OK) { Content = new StringContent($"data: {frame}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected endpoint " + request.RequestUri);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class InterruptedSessionCacheTests
{
    private const string ModelId = "coding/qwen3.8-27b~interrupted-prefill";

    [Fact]
    public async Task StopBeforeTheFirstStreamingFrameStillPreservesTheMeasuredPromptTarget()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var handler = new InterruptedPrefillHandler(ModelId)
        {
            OnInference = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<HttpResponseMessage>(token);
            },
        };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(ModelId,
            [new("user", "Fortsetzen")], [], sessionCacheKey: "first-prefill-session", cancellationToken: cancellation.Token));

        Assert.Equal(1, handler.InferenceCalls);
        Assert.Equal(1, handler.Saves);
        var save = Assert.IsType<JsonElement>(handler.SaveBody);
        Assert.Equal(134957, save.GetProperty("promptTokens").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InternalTimeoutOrTerminalTransportFailureProtectsTheCacheBeforeTheRunEndFlush(bool timeout)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new InterruptedPrefillHandler(ModelId)
        {
            OnInference = _ => Task.FromException<HttpResponseMessage>(timeout
                ? new OperationCanceledException("Native inference stalled during its first batch")
                : new HttpRequestException("Native inference connection closed")),
        };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);

        if (timeout)
        {
            var error = await Assert.ThrowsAsync<ModelGenerationTerminatedException>(() => runtime.CompleteChatAsync(ModelId,
                [new("user", "Fortsetzen")], [], sessionCacheKey: "failed-prefill-session"));
            Assert.Equal("model_stall_timeout", error.ProviderCode);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ModelProviderRequestException>(() => runtime.CompleteChatAsync(ModelId,
                [new("user", "Fortsetzen")], [], sessionCacheKey: "failed-prefill-session"));
            Assert.Equal("generation", error.Phase);
        }

        Assert.Equal(timeout ? 1 : 3, handler.InferenceCalls);
        Assert.Equal(1, handler.Saves);
        var save = Assert.IsType<JsonElement>(handler.SaveBody);
        Assert.Equal(134957, save.GetProperty("promptTokens").GetInt32());
    }

    [Fact]
    public async Task CancellationBeforeInferenceDoesNotSaveAnInterruptedCache()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new InterruptedPrefillHandler(ModelId);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(ModelId,
            [new("user", "Fortsetzen")], [], sessionCacheKey: "before-prefill-session",
            nativeProgress: (value, token) =>
            {
                if (value.State == "tokenCounting" && value.PromptTokens == 134957)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return ValueTask.CompletedTask;
            }, cancellationToken: cancellation.Token));

        Assert.Equal(0, handler.InferenceCalls);
        Assert.Equal(0, handler.Saves);
    }

    [Fact]
    public async Task CancellationAfterACompletedResponseDoesNotReplaceItWithAnInterruptedSnapshot()
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new InterruptedPrefillHandler(ModelId)
        {
            OnInference = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Fertig\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":134957,\"completion_tokens\":2}}",
                    Encoding.UTF8, "application/json"),
            }),
        };
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(ModelId,
            [new("user", "Fortsetzen")], [], sessionCacheKey: "completed-before-stop-session",
            nativeProgress: (value, token) =>
            {
                if (value.State == "tokenProgress")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return ValueTask.CompletedTask;
            }, cancellationToken: cancellation.Token));

        Assert.Equal(1, handler.InferenceCalls);
        Assert.Equal(0, handler.Saves);
    }

    [Theory]
    [InlineData("coding/gpt-oss-120b~interrupted-prefill", false)]
    [InlineData("coding/qwen3.8-27b~interrupted-prefill", false)]
    [InlineData("coding/DeepSeek-V4-Flash~interrupted-prefill", true)]
    public async Task CancellingPrefillSendsTheFullTargetCountForEveryModelWithoutChangingTailRecovery(
        string modelId, bool publishesExactTail)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test:19090");
        using var handler = new InterruptedPrefillHandler(modelId);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var progress = new List<ModelRuntimeProgress>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.CompleteChatAsync(modelId,
            [new("user", "Setze die gespeicherte Sitzung fort.")], [], reasoningEffort: "high",
            nativeProgress: (value, token) =>
            {
                progress.Add(value);
                if (value.ProcessedPromptTokens == 5120)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return ValueTask.CompletedTask;
            }, sessionCacheKey: "interrupted-prefill-session", cancellationToken: cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.Saves);
        var save = Assert.IsType<JsonElement>(handler.SaveBody);
        Assert.Equal(134957, save.GetProperty("promptTokens").GetInt32());
        Assert.Equal("interrupted-prefill-session", save.GetProperty("sessionKey").GetString());
        Assert.Equal(publishesExactTail, progress.Any(value => value.State == "interruptedNativeTail"));
    }

    private sealed class InterruptedPrefillHandler(string modelId) : HttpMessageHandler
    {
        private static readonly string[] Tags = ["missum-context-train:262144", "missum-reasoning-mode:llama-native",
            "missum-reasoning-levels:none|high", "missum-reasoning-default:high"];
        internal int Saves { get; private set; }
        internal int InferenceCalls { get; private set; }
        internal JsonElement? SaveBody { get; private set; }
        internal Func<CancellationToken, Task<HttpResponseMessage>>? OnInference { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = modelId, tags = Tags, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 262144 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 134957 });
            if (path == "/sessions/prepare") return Json(new { status = "restored", restoredTokens = 134000 });
            if (path == "/sessions/save")
            {
                Saves++;
                Assert.False(cancellationToken.IsCancellationRequested);
                SaveBody = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json(new { status = "saved", generatedTail = "Exakter unterbrochener Gedanke." });
            }
            if (path == "/v1/chat/completions")
            {
                InferenceCalls++;
                if (OnInference is not null) return await OnInference(cancellationToken);
                var prefill = JsonSerializer.Serialize(new { prompt_progress = new { total = 134957, processed = 5120, cache = 0 } });
                return new(HttpStatusCode.OK)
                    { Content = new StringContent("data: " + prefill + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected endpoint: " + path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class NativeContextBudgetRegressionTests
{
    [Theory]
    [InlineData(1_000_000, 899_999, false)]
    [InlineData(1_000_000, 900_000, true)]
    [InlineData(1_000_000, 960_000, true)]
    [InlineData(258_000, 232_199, false)]
    [InlineData(258_000, 232_200, true)]
    public async Task ExactThresholdIsEnforcedBeforeInferenceEvenWhenTheCharacterEstimateIsSmall(int contextLength, int tokens, bool compact)
    {
        using var handler = new ExactBudgetHandler { InputTokens = tokens, LoadedContext = contextLength };
        using var client = new ModelRuntimeClient(new HttpClient(handler),
            Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
            NullLogger<ModelRuntimeClient>.Instance);
        var inference = client.CompleteChatWithCacheOwnershipAsync(ExactBudgetHandler.ModelId,
            [new("user", "Verlauf fortsetzen")], [], null, null, compactAtThreshold: true);
        if (compact)
        {
            var error = await Assert.ThrowsAsync<NativeContextBudgetException>(() => inference);
            Assert.Equal(Missum.Ai.Contracts.ContextCompactionPolicy.InputTokenBudget(contextLength), error.BudgetTokens);
            Assert.Equal(0, handler.GenerationAttempts);
            Assert.Single(handler.TokenCountingBodies);
        }
        else
        {
            Assert.Equal("Fortgesetzt.", (await inference).Content);
            Assert.Equal(1, handler.GenerationAttempts);
        }
    }

    [Theory]
    [InlineData(262_144, 262_305)] // Observed Bohmsche Mechanik request.
    [InlineData(262_144, 262_144)]
    [InlineData(262_144, 262_143)] // One final boundary token still leaves no output.
    [InlineData(1_048_576, 1_048_577)]
    public void ExactOverflowHasRecoverableContextDataInsteadOfGenericOperationFailure(int context, int input)
    {
        var error = Assert.Throws<NativeContextBudgetException>(() =>
            ModelRuntimeClient.ResolveMaximumOutputTokens(context, input, null));

        Assert.Equal(input, error.InputTokens);
        Assert.Equal(context, error.ContextLength);
        Assert.Equal(1, error.MinimumOutputTokens);
        Assert.Equal(context - 2, error.BudgetTokens);
        Assert.False(ModelRuntimeClient.IsTransientInferenceFailure(error));
        Assert.False(ModelRuntimeClient.IsRecoverableProviderTransportFailure(error));
    }

    [Theory]
    [InlineData(262_144, 262_142, null, 1)]
    [InlineData(262_144, 200_000, null, 62_143)]
    [InlineData(1_048_576, 262_305, null, 786_270)]
    [InlineData(1_048_576, 262_305, 16_384, 16_384)]
    [InlineData(1_048_576, 262_305, 1_000_000, 786_270)]
    public void FittingPromptsKeepTheirActualLoadedWindowAndRequestedOutput(int context, int input, int? maximum, int expected)
    {
        Assert.Equal(expected, ModelRuntimeClient.ResolveMaximumOutputTokens(context, input, maximum));
    }

    [Fact]
    public async Task ExactOverflowDoesNotGenerateRetryOrTouchTheResidentCacheAndReleasesTheTurnGate()
    {
        using var handler = new ExactBudgetHandler { InputTokens = 262_305 };
        using var http = new HttpClient(handler);
        using var client = new ModelRuntimeClient(http,
            Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
            NullLogger<ModelRuntimeClient>.Instance);
        LmChatMessage[] messages = [new("user", "Prüfe die bisherigen Berechnungen weiter.")];

        var error = await Assert.ThrowsAsync<NativeContextBudgetException>(() => client.CompleteChatAsync(
            ExactBudgetHandler.ModelId, messages, [], sessionCacheKey: "bohm-stable-cache"));

        Assert.Equal(262_305, error.InputTokens);
        Assert.Single(handler.TokenCountingBodies);
        Assert.Equal(0, handler.GenerationAttempts);
        Assert.Equal(["/sessions/prepare"], handler.CacheOperations);
        Assert.Equal("bohm-stable-cache", Assert.Single(handler.CacheKeys));

        // A deliberate planner repair can then use the same resident model and
        // cache identity. The budget failure itself must not strand the gate.
        handler.InputTokens = 128;
        var response = await client.CompleteChatAsync(ExactBudgetHandler.ModelId,
            [new("user", "Gesicherter und bewusst verdichteter Arbeitsstand.")], [], sessionCacheKey: "bohm-stable-cache");

        Assert.Equal("Fortgesetzt.", response.Content);
        Assert.Equal(2, handler.TokenCountingBodies.Count);
        Assert.Equal(1, handler.GenerationAttempts);
        Assert.Equal(["/sessions/prepare", "/sessions/prepare", "/sessions/save"], handler.CacheOperations);
        Assert.All(handler.CacheKeys, key => Assert.Equal("bohm-stable-cache", key));
        using var generated = JsonDocument.Parse(Assert.Single(handler.GenerationBodies));
        Assert.Equal(262_144 - 128 - 1, generated.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.True(generated.RootElement.GetProperty("cache_prompt").GetBoolean());
        Assert.Equal(0, generated.RootElement.GetProperty("id_slot").GetInt32());
    }

    private sealed class ExactBudgetHandler : HttpMessageHandler
    {
        internal const string ModelId = "coding/future-budget-model~test";
        internal int InputTokens { get; set; }
        internal int LoadedContext { get; init; } = 262_144;
        internal int GenerationAttempts { get; private set; }
        internal List<string> TokenCountingBodies { get; } = [];
        internal List<string> GenerationBodies { get; } = [];
        internal List<string> CacheOperations { get; } = [];
        internal List<string?> CacheKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models")
                return Json(JsonSerializer.Serialize(new
                {
                    data = new[] { new { id = ModelId, meta = new { n_ctx_train = 1_048_576 }, status = new { value = "loaded" } } },
                }));
            if (path == "/props") return Json(JsonSerializer.Serialize(new { default_generation_settings = new { n_ctx = LoadedContext } }));
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path.StartsWith("/sessions/", StringComparison.Ordinal))
            {
                CacheOperations.Add(path);
                using var cache = JsonDocument.Parse(body);
                CacheKeys.Add(cache.RootElement.GetProperty("sessionKey").GetString());
                return Json(path == "/sessions/save" ? "{\"status\":\"saved\"}" : "{\"status\":\"resident\"}");
            }
            if (path == "/v1/chat/completions/input_tokens")
            {
                TokenCountingBodies.Add(body);
                return Json(JsonSerializer.Serialize(new { input_tokens = InputTokens }));
            }
            if (path == "/v1/chat/completions")
            {
                GenerationAttempts++;
                GenerationBodies.Add(body);
                return Json("{\"choices\":[{\"message\":{\"content\":\"Fortgesetzt.\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":128,\"completion_tokens\":4}}");
            }
            // In particular, loading/unloading or clearing a model is forbidden
            // for an already resident model with an oversized next prompt.
            throw new InvalidOperationException("Unexpected native runtime request: " + path);
        }

        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }
}

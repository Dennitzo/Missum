using System.Net;
using System.Text;
using System.Text.Json;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Tests;

public sealed class ModelRuntimeResidentOnlyTests
{
    private const string ModelId = "coding/qwen3.8-27b~resident-review-test";
    private const string ChildId = ModelId + "@subagent";
    private const string CacheKey = "session-primary-review";
    private static readonly string[] ManagedTags = ["missum-gpu-policy:single-preferred-v1"];
    private static readonly string[] ChildTags = ["missum-agent-instance:subagent", "missum-base-model:" + ModelId];

    [Fact]
    public async Task SerialReviewRetainsTheMainModelCacheOwnershipAndFullToolPrefixWithoutLoading()
    {
        using var handler = new ResidentRuntimeHandler { AllowLoad = true };
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);
        var tools = Tools();
        await runtime.CompleteChatWithCacheOwnershipAsync(ModelId,
            [new("user", "Bearbeite die ursprüngliche Forschungsaufgabe.")], tools,
            "session-owner", null, modelRole: "coding", sessionCacheKey: CacheKey);
        Assert.Equal(1, handler.LoadCalls);
        handler.AllowLoad = false;

        var review = await runtime.CompleteChatWithCacheOwnershipAsync(ModelId,
            [new("user", "Bearbeite die ursprüngliche Forschungsaufgabe."), new("assistant", "Zwischenergebnis dokumentiert."),
             new("user", "Prüfe den Abschluss anhand des ursprünglichen Ziels und der offenen Punkte.")], tools,
            "session-owner", null, modelRole: "coding", sessionCacheKey: CacheKey,
            requiredContextLength: 258_000, responseSchema: JsonSerializer.SerializeToElement(new { type = "object" }),
            residentOnly: true, suppressToolCalls: true);

        Assert.Equal("Prüfung abgeschlossen.", review.Content);
        Assert.Equal(1, handler.LoadCalls); // No idempotent primary/companion load-control call during the review.
        Assert.Equal(2, handler.ChatBodies.Count);
        var main = handler.ChatBodies[0];
        var checkedBody = handler.ChatBodies[1];
        Assert.Equal(ModelId, main.GetProperty("model").GetString());
        Assert.Equal(ModelId, checkedBody.GetProperty("model").GetString());
        Assert.Equal(main.GetProperty("tools").GetRawText(), checkedBody.GetProperty("tools").GetRawText());
        Assert.Equal("auto", main.GetProperty("tool_choice").GetString());
        Assert.Equal("none", checkedBody.GetProperty("tool_choice").GetString());
        Assert.Equal("json_schema", checkedBody.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(32_768 - 128 - 1, checkedBody.GetProperty("max_tokens").GetInt32());
        Assert.True(checkedBody.GetProperty("cache_prompt").GetBoolean());
        Assert.Equal(0, checkedBody.GetProperty("id_slot").GetInt32());
        Assert.Equal(4, handler.CacheBodies.Count);
        Assert.All(handler.CacheBodies, cache =>
        {
            Assert.Equal(ModelId, cache.GetProperty("model").GetString());
            Assert.Equal(CacheKey, cache.GetProperty("sessionKey").GetString());
        });
        Assert.All(handler.CacheBodies.Where(cache => cache.GetProperty("sessionId").ValueKind == JsonValueKind.String),
            cache => Assert.Equal("session-owner", cache.GetProperty("sessionId").GetString()));
        Assert.DoesNotContain(handler.Paths, path => path.StartsWith("/agents/", StringComparison.Ordinal)
            || path == "/models/unload");
    }

    [Theory]
    [InlineData("unloaded")]
    [InlineData("sleeping")]
    [InlineData("loading")]
    [InlineData("failed")]
    public async Task NonActivePrimaryIsRejectedWithoutLoadingOrInference(string state)
    {
        using var handler = new ResidentRuntimeHandler { State = state };
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewAsync(runtime));

        Assert.StartsWith("specialized_agent.model_not_resident:", error.Message);
        Assert.Equal(0, handler.LoadCalls);
        Assert.Empty(handler.ChatBodies);
        Assert.Empty(handler.CacheBodies);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "coding/other-resident-model~test")]
    [InlineData(false, ChildId)]
    public async Task MissingOrMismatchedPrimaryNeverFallsBackToAnotherResidentInstance(bool missing, string? instance)
    {
        using var handler = new ResidentRuntimeHandler { MissingPrimary = missing, IncludeChild = true };
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteChatWithCacheOwnershipAsync(
            ModelId, [new("user", "Prüfe das Ziel.")], [], "session-owner", null,
            runtimeInstanceId: instance, residentOnly: true, suppressToolCalls: true));

        Assert.StartsWith("specialized_agent.model_not_resident:", error.Message);
        Assert.Equal(0, handler.LoadCalls);
        Assert.Empty(handler.ChatBodies);
        Assert.Empty(handler.CacheBodies);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "coding.read")]
    public async Task SuppressedToolsCannotAlsoBeRequiredAndFailBeforeRuntimeAccess(bool require, string? requiredName)
    {
        using var handler = new ResidentRuntimeHandler();
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.CompleteChatWithCacheOwnershipAsync(
            ModelId, [new("user", "Prüfe das Ziel.")], Tools(), "session-owner", null,
            requireToolCall: require, requiredToolName: requiredName, residentOnly: true, suppressToolCalls: true));

        Assert.Empty(handler.Paths);
    }

    [Fact]
    public async Task AProviderIgnoringSuppressionCannotReturnAnAcceptedToolCall()
    {
        using var handler = new ResidentRuntimeHandler { ReturnToolCall = true };
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CompleteChatWithCacheOwnershipAsync(
            ModelId, [new("user", "Prüfe das Ziel.")], Tools(), "session-owner", null,
            residentOnly: true, suppressToolCalls: true));

        Assert.StartsWith("specialized_agent.unexpected_tool_call:", error.Message);
        Assert.Equal("none", Assert.Single(handler.ChatBodies).GetProperty("tool_choice").GetString());
        Assert.Equal(0, handler.LoadCalls);
    }

    [Fact]
    public async Task AReviewUsesTheSerializedPrimaryTurnAndReleasesItAfterStop()
    {
        using var handler = new ResidentRuntimeHandler { HoldFirstChat = true };
        using var http = new HttpClient(handler);
        using var runtime = CreateClient(http);
        using var stop = new CancellationTokenSource();
        var first = ReviewAsync(runtime, stop.Token);
        await handler.ChatEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var catalogReads = handler.CatalogReads;
        var second = ReviewAsync(runtime);
        try
        {
            Assert.False(second.IsCompleted);
            Assert.Equal(catalogReads, handler.CatalogReads);
            Assert.Single(handler.ChatBodies);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { handler.ReleaseChat.TrySetResult(); }

        Assert.Equal("Prüfung abgeschlossen.", (await second.WaitAsync(TimeSpan.FromSeconds(5))).Content);
        Assert.Equal(2, handler.ChatBodies.Count);
        Assert.Equal(0, handler.LoadCalls);
        Assert.All(handler.CacheBodies, body => Assert.Equal(CacheKey, body.GetProperty("sessionKey").GetString()));
    }

    private static Task<LmChatResult> ReviewAsync(ModelRuntimeClient runtime, CancellationToken cancellationToken = default) =>
        runtime.CompleteChatWithCacheOwnershipAsync(ModelId, [new("user", "Prüfe das ursprüngliche Ziel und den Arbeitsplan.")],
            [], "session-owner", null, sessionCacheKey: CacheKey, residentOnly: true, suppressToolCalls: true,
            cancellationToken: cancellationToken);

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://native.test:8081") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private static LmToolDefinition[] Tools()
    {
        var parameters = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } });
        return [new("coding.read", "Datei lesen", parameters), new("coding.updatePlan", "Arbeitsplan aktualisieren", parameters)];
    }

    private sealed class ResidentRuntimeHandler : HttpMessageHandler
    {
        internal string State { get; init; } = "loaded";
        internal bool MissingPrimary { get; init; }
        internal bool IncludeChild { get; init; }
        internal bool AllowLoad { get; set; }
        internal bool ReturnToolCall { get; init; }
        internal bool HoldFirstChat { get; init; }
        internal int LoadCalls { get; private set; }
        internal int CatalogReads { get; private set; }
        internal List<string> Paths { get; } = [];
        internal List<JsonElement> ChatBodies { get; } = [];
        internal List<JsonElement> CacheBodies { get; } = [];
        internal TaskCompletionSource ChatEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseChat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/v1/models")
            {
                CatalogReads++;
                var models = new List<object>();
                if (!MissingPrimary) models.Add(new { id = ModelId, status = new { value = State }, tags = ManagedTags,
                    meta = new { n_ctx_train = 1_048_576 } });
                models.Add(new { id = "coding/other-resident-model~test", status = new { value = "loaded" }, tags = ManagedTags });
                if (IncludeChild) models.Add(new { id = ChildId, status = new { value = "loaded" }, tags = ChildTags });
                return Json(new { data = models });
            }
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32_768 } });
            if (path == "/models/load")
            {
                LoadCalls++;
                if (!AllowLoad) throw new InvalidOperationException("The resident-only path must not issue model load/preload control calls.");
                return Json(new { success = true });
            }
            if (path.StartsWith("/sessions/", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                CacheBodies.Add(body.RootElement.Clone());
                return Json(new { status = path == "/sessions/save" ? "saved" : "resident" });
            }
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 128 });
            if (path == "/v1/chat/completions")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ChatBodies.Add(body.RootElement.Clone());
                if (HoldFirstChat && ChatBodies.Count == 1)
                {
                    ChatEntered.TrySetResult();
                    await ReleaseChat.Task.WaitAsync(cancellationToken);
                }
                if (ReturnToolCall)
                    return Json(new { choices = new[] { new { message = new
                    {
                        role = "assistant", content = (string?)null,
                        tool_calls = new[] { new { id = "forbidden-review-call", type = "function", function = new
                        {
                            name = ModelRuntimeClient.ToTransportToolName("coding.read"), arguments = "{}",
                        } } },
                    }, finish_reason = "tool_calls" } }, usage = new { prompt_tokens = 128, completion_tokens = 4 } });
                return Json(new { choices = new[] { new { message = new { role = "assistant", content = "Prüfung abgeschlossen." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 128, completion_tokens = 4 } });
            }
            throw new InvalidOperationException("Unexpected native request: " + request.Method + " " + path);
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
    }
}

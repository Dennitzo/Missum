using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingModelRuntimeTests
{
    private const string CodingId = "coding/Qwen3-Coder-Q4~abc123";
    private static readonly string[] CodingTags = ["missum-context-train:32768"];

    [Fact]
    public async Task NativeQwenEditSchemaAndInterleavedArgumentFragmentsPreserveEveryField()
    {
        var handler = new CodingHandler(fragmentedEdits: true);
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var spec = CodingToolCatalog.CreateTools().Single(tool => tool.Name == "coding.edit");
        var result = await client.CompleteChatAsync(CodingId, [new("user", "Edit both files")],
            [new(spec.Name, spec.Description, spec.Schema)], modelRole: "coding");

        Assert.Equal(2, result.ToolCalls.Count);
        for (var index = 0; index < 2; index++)
        {
            var call = result.ToolCalls[index];
            Assert.Equal("coding.edit", call.Name);
            Assert.Equal("edit-" + index, call.Id);
            Assert.Equal($"file-{index}.txt", call.Arguments.GetProperty("path").GetString());
            Assert.Equal("Grüße\n\"old\"", call.Arguments.GetProperty("oldText").GetString());
            Assert.Equal("new\n", call.Arguments.GetProperty("newText").GetString());
            Assert.Equal(new string('a', 64), call.Arguments.GetProperty("expectedSha256").GetString());
        }
        using var body = JsonDocument.Parse(handler.ChatBody!);
        var schema = body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");
        Assert.False(schema.TryGetProperty("oneOf", out _));
        Assert.Equal(spec.Schema.GetProperty("properties").GetRawText(), schema.GetProperty("properties").GetRawText());
    }

    [Fact]
    public async Task CodingRunLoadsOnlyNativeModelAndStreamsToolAndPromptProgress()
    {
        var handler = new CodingHandler();
        using var http = new HttpClient(handler);
        using var client = CreateClient(http);
        var progress = new List<ModelRuntimeProgress>();
        var tool = new LmToolDefinition("workspace.read", "Read file",
            JsonSerializer.SerializeToElement(new { type = "object", properties = new { path = new { type = "string" } } }));

        var result = await client.CompleteChatAsync(CodingId,
            [new LmChatMessage("user", "Read README.md")], [tool], modelRole: "coding",
            nativeProgress: (value, _) => { progress.Add(value); return ValueTask.CompletedTask; });

        Assert.Equal("workspace.read", Assert.Single(result.ToolCalls).Name);
        Assert.Equal("README.md", result.ToolCalls[0].Arguments.GetProperty("path").GetString());
        Assert.Contains(progress, value => value.State == "promptProcessing" && value.ProcessedPromptTokens == 8 && value.PromptProgress == 0.5 && value.CachedPromptTokens == 6);
        Assert.Equal(6, result.Metrics!.CachedPromptTokens);
        Assert.Null(result.Metrics.PromptEvaluatedTokens);
        Assert.Contains(progress, value => value.State == "tokenProgress" && value.CachedPromptTokens == 6 && value.ProcessedPromptTokens == 16);
        Assert.Equal(1, handler.Loads);
        Assert.All(handler.Hosts, host => Assert.Equal("coding.test", host));
        using var request = JsonDocument.Parse(handler.ChatBody!);
        Assert.Equal(CodingId, request.RootElement.GetProperty("model").GetString());
        Assert.True(request.RootElement.GetProperty("cache_prompt").GetBoolean());
        Assert.True(request.RootElement.GetProperty("return_progress").GetBoolean());
        Assert.True(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(request.RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.Equal(32_751, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(request.RootElement.TryGetProperty("seed", out _));
    }

    [Fact]
    public async Task CodingCatalogDoesNotExposeNetworkDownloadEntriesOrGeneralModels()
    {
        using var http = new HttpClient(new CodingHandler());
        using var client = CreateClient(http);
        var catalog = await client.GetCodingModelsAsync();
        Assert.True(catalog.RuntimeReachable);
        var model = Assert.Single(catalog.Models);
        Assert.Equal("coding", model.Role);
        Assert.Equal("Qwen3-Coder-Q4", model.DisplayName);
        Assert.Equal(32_768, model.ContextTokens);
    }

    [Fact]
    public async Task MissingCodingRuntimeReturnsAnExplicitEmptyCatalog()
    {
        using var http = new HttpClient(new CodingHandler(unavailable: true));
        using var client = CreateClient(http);
        var catalog = await client.GetCodingModelsAsync();
        Assert.False(catalog.RuntimeReachable);
        Assert.Empty(catalog.Models);
        Assert.Contains("nicht erreichbar", catalog.Message);
    }

    [Fact]
    public async Task GeneralModelIdCannotBeSentToCodingRuntime()
    {
        using var http = new HttpClient(new CodingHandler());
        using var client = CreateClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteChatAsync("general-model", [], [], modelRole: "coding"));
    }

    private static ModelRuntimeClient CreateClient(HttpClient http) => new(http,
        Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new Uri("http://coding.test") }),
        NullLogger<ModelRuntimeClient>.Instance);

    private sealed class CodingHandler(bool unavailable = false, bool fragmentedEdits = false) : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        public int Loads { get; private set; }
        public string? ChatBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Hosts.Add(request.RequestUri!.Host);
            if (unavailable) { throw new HttpRequestException("Offline"); }
            if (request.RequestUri.AbsolutePath == "/props") return Json(new { default_generation_settings = new { n_ctx = 32_768 } });
            if (request.RequestUri.AbsolutePath is "/sessions/prepare" or "/sessions/save") return Json(new { success = true });
            if (request.RequestUri.AbsolutePath == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 16 });
            if (request.RequestUri.AbsolutePath == "/v1/models")
            {
                return Json(new { data = new[] {
                    new { id = CodingId, tags = CodingTags, status = new { value = Loads > 0 ? "loaded" : "unloaded" } },
                    new { id = "hf-network/model:Q4", tags = Array.Empty<string>(), status = new { value = "unloaded" } } } });
            }
            if (request.RequestUri.AbsolutePath == "/models/load")
            {
                Loads++;
                return Json(new { success = true });
            }
            if (request.RequestUri.AbsolutePath == "/v1/chat/completions")
            {
                ChatBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                if (fragmentedEdits)
                {
                    var data = new List<string>();
                    var arguments = Enumerable.Range(0, 2).Select(index => JsonSerializer.Serialize(new
                    {
                        path = $"file-{index}.txt", expectedSha256 = new string('a', 64), oldText = "Grüße\n\"old\"", newText = "new\n",
                    })).ToArray();
                    for (var offset = 0; offset < arguments[0].Length; offset += 7)
                        for (var index = 0; index < 2; index++)
                            data.Add(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = new[]
                            {
                                new { index, id = offset == 0 ? "edit-" + index : null, function = new
                                {
                                    name = offset == 0 ? ModelRuntimeClient.ToTransportToolName("coding.edit") : null,
                                    arguments = arguments[index].Substring(offset, Math.Min(7, arguments[index].Length - offset)),
                                } },
                            } } } } }));
                    data.Add(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { }, finish_reason = "tool_calls" } } }));
                    data.Add("[DONE]");
                    return new(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n\n", data.Select(frame => "data: " + frame)) + "\n\n", Encoding.UTF8, "text/event-stream") };
                }
                var toolName = ModelRuntimeClient.ToTransportToolName("workspace.read");
                var frames = new[] {
                    JsonSerializer.Serialize(new { prompt_progress = new { total = 16, cache = 6, processed = 8 }, choices = Array.Empty<object>() }),
                    JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { new { index = 0, id = "call_1", type = "function", function = new { name = toolName, arguments = "{\"path\":\"README.md\"}" } } } }, finish_reason = "tool_calls" } } }),
                    "[DONE]" };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Join("\n\n", frames.Select(frame => "data: " + frame)) + "\n\n", Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException("Unexpected endpoint " + request.RequestUri);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}

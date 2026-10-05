using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Tests;

public sealed class NativeThinkingToggleTests
{
    // Exact input bridge and generation branch from the installed Unsloth
    // DeepSeek-V4 template (Apache 2.0); media/history macros are irrelevant to
    // boolean capability discovery. Keep both spellings to protect its alias.
    private const string DeepSeekAliasedTemplate = """
        {%- if not thinking is defined -%}
          {%- if enable_thinking is defined -%}
            {%- set thinking = enable_thinking -%}
          {%- else -%}
            {%- set thinking = false -%}
          {%- endif -%}
        {%- endif -%}
        {%- set thinking_start_token = '<think>' -%}
        {%- set thinking_end_token = '</think>' -%}
        {%- if add_generation_prompt -%}
          {{- '<｜Assistant｜>' -}}
          {%- if thinking -%}
            {{- thinking_start_token -}}
          {%- else -%}
            {{- thinking_end_token -}}
          {%- endif -%}
        {%- endif -%}
        """;

    private const string ThinkingOnlyTemplate = """
        {%- if not thinking is defined -%}
          {%- set thinking = false -%}
        {%- endif -%}
        {%- if add_generation_prompt -%}
          {{- '<｜Assistant｜>' -}}
          {%- if thinking -%}
            {{- '<think>' -}}
          {%- else -%}
            {{- '</think>' -}}
          {%- endif -%}
        {%- endif -%}
        """;

    [Theory]
    [InlineData(false, "llama-toggle")]
    [InlineData(true, "llama-thinking-toggle")]
    public void TemplateDiscoveryPreservesTheActualBooleanInput(bool thinkingOnly, string family)
    {
        var profile = ModelRuntimeClient.ReadReasoningMetadata(JsonSerializer.SerializeToElement(new
        {
            chat_template = thinkingOnly ? ThinkingOnlyTemplate : DeepSeekAliasedTemplate,
        }));
        Assert.NotNull(profile);
        Assert.Equal(family, profile.Family);
        Assert.Equal(["none", "on"], profile.SupportedEfforts);
        Assert.Equal("on", profile.DefaultEffort);
    }

    [Theory]
    [InlineData("A code example: thinking = true; enable_thinking = true")]
    [InlineData("{# {% if thinking %} {% if enable_thinking %} #}")]
    [InlineData("{% raw %} {% if thinking %} {% if enable_thinking %} {% endraw %}")]
    [InlineData("{% raw %} {{ 'unterminated {% if thinking %} {% endraw %}")]
    [InlineData("{% if 'thinking enable_thinking' in message %}text{% endif %}")]
    [InlineData("{% if message['thinking'] or message['enable_thinking'] %}text{% endif %}")]
    [InlineData("{% if namespace.thinking or namespace . enable_thinking %}text{% endif %}")]
    [InlineData("{% if thinking.value or enable_thinking.value %}text{% endif %}")]
    [InlineData("{% if thinking . value or enable_thinking . value %}text{% endif %}")]
    [InlineData("{% if namespace . thinking or namespace . enable_thinking %}text{% endif %}")]
    [InlineData("{% if thinking_mode or enable_thinking_example %}text{% endif %}")]
    [InlineData("{% if thinking() or enable_thinking() %}text{% endif %}")]
    [InlineData("{{ '{% if thinking %}' }} {% set thinking = false %}")]
    public void DocumentationAndOtherDataDoNotAdvertiseAReasoningToggle(string template)
    {
        Assert.Null(ModelRuntimeClient.ReadReasoningMetadata(JsonSerializer.SerializeToElement(new { chat_template = template })));
    }

    [Fact]
    public void RawExamplesAndQuotedJinjaDelimitersDoNotHideTheLaterRealInput()
    {
        const string prefix = "{% raw %} {{ 'unterminated {% if thinking %} {% endraw %}"
            + "{% set description = '%} thinking' %}{# {% if thinking %} #}";
        var profile = ModelRuntimeClient.ReadReasoningMetadata(JsonSerializer.SerializeToElement(new
        {
            chat_template = prefix + DeepSeekAliasedTemplate,
        }));
        Assert.NotNull(profile);
        Assert.Equal("llama-toggle", profile.Family);
    }

    [Theory]
    [InlineData(false, "none", false)]
    [InlineData(false, "on", true)]
    [InlineData(true, "none", false)]
    [InlineData(true, "on", true)]
    public async Task DiscoveryUsesIdenticalOnOffControlForCountingInferenceAndSubagentPrefill(
        bool thinkingOnly, string requested, bool enabled)
    {
        using var handler = new TemplateRuntimeHandler(thinkingOnly ? ThinkingOnlyTemplate : DeepSeekAliasedTemplate);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http,
            Options.Create(new MissumAiServerOptions { ModelRuntimeUri = new("http://native.test") }),
            NullLogger<ModelRuntimeClient>.Instance);
        var status = await runtime.GetStatusAsync();
        var model = Assert.Single(status.Models, item => item.Id == TemplateRuntimeHandler.Model && item.Role == "general");
        Assert.Equal(["none", "on"], model.ReasoningEfforts);
        var progress = new List<ModelRuntimeProgress>();
        var result = await runtime.CompleteChatAsync(TemplateRuntimeHandler.Model,
            [new("user", "Erkläre kurz den Ansatz.")], [], modelRole: "general", reasoningEffort: requested,
            nativeProgress: (value, _) => { progress.Add(value); return ValueTask.CompletedTask; });
        Assert.Equal("Fertig", result.Content);
        Assert.Equal(enabled, result.HadReasoning);
        Assert.Equal(enabled ? "Ich prüfe den Ansatz." : "", string.Concat(progress.Select(item => item.ReasoningDelta)));
        await runtime.PrepareSubagentAsync(TemplateRuntimeHandler.Model, "parent-key", "child-key",
            [new("user", "Eigene Teilaufgabe.")], [], "general", requested);
        var field = thinkingOnly ? "thinking" : "enable_thinking";
        AssertControl(Assert.Single(handler.CountBodies), field, enabled);
        AssertControl(handler.ChatBody!.Value, field, enabled);
        AssertControl(handler.PrepareBody!.Value.GetProperty("prefill"), field, enabled);

        static void AssertControl(JsonElement body, string field, bool enabled)
        {
            var kwargs = body.GetProperty("chat_template_kwargs");
            Assert.Equal(enabled, kwargs.GetProperty(field).GetBoolean());
            Assert.Single(kwargs.EnumerateObject());
            Assert.Equal(!enabled, body.TryGetProperty("reasoning_effort", out var effort));
            if (!enabled) Assert.Equal("none", effort.GetString());
        }
    }

    private sealed class TemplateRuntimeHandler(string template) : HttpMessageHandler
    {
        internal const string Model = "coding/arbitrary-template-model~fixture";
        private static readonly string[] PrimaryTags = ["missum-context-train:32768"];
        private static readonly string[] ChildTags = ["missum-context-train:32768", "missum-agent-instance:subagent", "missum-base-model:" + Model];
        internal List<JsonElement> CountBodies { get; } = [];
        internal JsonElement? ChatBody;
        internal JsonElement? PrepareBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new
            {
                data = new[]
                {
                    new { id = Model, status = new { value = "loaded" }, tags = PrimaryTags },
                    new { id = Model + "@subagent", status = new { value = "loaded" }, tags = ChildTags },
                },
            });
            if (path == "/props") return Json(new { chat_template = template, default_generation_settings = new { n_ctx = 32768 } });
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (path == "/v1/chat/completions/input_tokens")
            {
                CountBodies.Add(body);
                return Json(new { input_tokens = 20 });
            }
            if (path == "/agents/prepare")
            {
                PrepareBody = body;
                return Json(new { allowed = true, modelId = Model, instanceId = Model + "@subagent", gpuIndex = 1, contextLength = 32768, cacheStatus = "forked" });
            }
            if (path == "/v1/chat/completions")
            {
                ChatBody = body;
                var kwargs = body.GetProperty("chat_template_kwargs");
                var enabled = kwargs.EnumerateObject().Single().Value.GetBoolean();
                var frames = enabled ? "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Ich prüfe den Ansatz.\"}}]}\n\n" : "";
                frames += "data: {\"choices\":[{\"delta\":{\"content\":\"Fertig\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };
            }
            throw new InvalidOperationException(path);
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}

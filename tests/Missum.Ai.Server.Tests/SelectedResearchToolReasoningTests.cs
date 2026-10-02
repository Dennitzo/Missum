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

public sealed class SelectedResearchToolReasoningTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedResearchCodeWriteUsesNonThinkingRoundAndRestoresUserReasoningAfterTool(bool supportsNone)
    {
        using var context = new TestServerContext();
        context.Options.ModelRuntimeUri = new("http://native.test");
        context.Options.GeneralModelId = NativeHandler.ModelId;
        using var handler = new NativeHandler(supportsNone);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var requestedEffort = supportsNone ? "on" : "high";
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Leite das Ergebnis her, erstelle die Forschungsdatei und erläutere danach das Ergebnis.")])],
            SessionId: "science-reasoning-fixture", ClientCapabilities: ["research.sandbox"], AllowedServerTools: [],
            PreferredGeneralModelId: NativeHandler.ModelId, ReasoningEffort: requestedEffort,
            WorkspacePath: context.Root, Limits: new(TimeoutSeconds: 0));
        var runId = (await repository.CreateAsync(request, null)).Snapshot.RunId;

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => processor.ProcessAsync(runId, CancellationToken.None));
        var checkpoint = (await repository.GetCheckpointAsync(runId))!;
        var proposal = (await repository.GetToolProposalAsync(checkpoint.PendingProposalId!, runId))!;
        Assert.Equal(ClientToolNames.ResearchCodeWrite, proposal.Name);
        Assert.Equal("work/calculation.py", proposal.Arguments.GetProperty("path").GetString());
        Assert.Equal("print(42)\n", proposal.Arguments.GetProperty("content").GetString());
        Assert.Null(checkpoint.SelectedToolName);
        await repository.SaveClientToolResultAsync(runId, new(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { path = "work/calculation.py", saved = true, sha256 = new string('a', 64) })));

        await processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await repository.GetAsync(runId))!.State);
        Assert.Equal(3, handler.ChatBodies.Count);
        var analysis = handler.ChatBodies[0];
        var selected = handler.ChatBodies[1];
        var answer = handler.ChatBodies[2];
        Assert.Equal(ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName), analysis.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("required", selected.GetProperty("tool_choice").GetString());
        Assert.Equal(ModelRuntimeClient.ToTransportToolName(ClientToolNames.ResearchCodeWrite), selected.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("auto", answer.GetProperty("tool_choice").GetString());
        if (supportsNone)
        {
            Assert.True(analysis.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.False(selected.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.True(answer.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        }
        else
        {
            // A discovered model without a thinking toggle must keep a supported
            // minimum effort instead of receiving an invalid "none" enum.
            Assert.Equal("high", analysis.GetProperty("reasoning_effort").GetString());
            Assert.Equal("low", selected.GetProperty("reasoning_effort").GetString());
            Assert.Equal("high", answer.GetProperty("reasoning_effort").GetString());
        }
        Assert.Equal(requestedEffort, (await repository.GetRequestAsync(runId))!.ReasoningEffort);
        Assert.Contains(answer.GetProperty("messages").EnumerateArray(), message =>
            message.GetProperty("role").GetString() == "tool"
            && message.GetProperty("content").GetString()!.Contains("work/calculation.py", StringComparison.Ordinal));
    }

    private sealed class NativeHandler(bool supportsNone) : HttpMessageHandler
    {
        internal const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~protocol-fixture";
        internal List<JsonElement> ChatBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new
            {
                data = new[] { new { id = ModelId, status = new { value = "loaded" }, tags = new[]
                {
                    "missum-context-train:32768", supportsNone ? "missum-reasoning-mode:llama-toggle" : "missum-reasoning-mode:llama-native",
                    supportsNone ? "missum-reasoning-levels:none|on" : "missum-reasoning-levels:low|high",
                    supportsNone ? "missum-reasoning-default:on" : "missum-reasoning-default:high",
                } } },
            });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 512 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ChatBodies.Add(document.RootElement.Clone());
            if (ChatBodies.Count == 1) return ToolResponse("select", ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName), new { name = ClientToolNames.ResearchCodeWrite });
            if (ChatBodies.Count == 2) return ToolResponse("write", ModelRuntimeClient.ToTransportToolName(ClientToolNames.ResearchCodeWrite),
                new { projectId = "reasoning-fixture", path = "work/calculation.py", content = "print(42)\n" });
            return Json(new { choices = new[] { new { message = new { content = "Die Forschungsdatei wurde gespeichert. Das Ergebnis ist 42." }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 512, completion_tokens = 16 } });
        }

        private static HttpResponseMessage ToolResponse(string id, string name, object arguments) => Json(new
        {
            choices = new[] { new { message = new { tool_calls = new[]
            {
                new { id, type = "function", function = new { name, arguments = JsonSerializer.Serialize(arguments) } },
            } }, finish_reason = "tool_calls" } }, usage = new { prompt_tokens = 512, completion_tokens = 16 },
        });

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}

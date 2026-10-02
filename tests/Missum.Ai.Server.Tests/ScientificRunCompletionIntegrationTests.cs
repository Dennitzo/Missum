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

public sealed class ScientificRunCompletionIntegrationTests
{
    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Auto)]
    public async Task FailedPdfReturnsToModelForRepairAndOnlySuccessfulArtifactsCompleteTheRun(RunMode mode)
    {
        using var fixture = new Fixture(repairManuscript: true, mode: mode);
        var runId = await fixture.CreateAsync(ScientificRunCompletionPolicyTests.SuccessfulWork());

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => fixture.Processor.ProcessAsync(runId, CancellationToken.None));
        var first = await fixture.ProposalAsync(runId);
        Assert.Equal(ClientToolNames.ResearchDeliverablesVerify, first.Name);
        Assert.NotEqual(RunState.Completed, (await fixture.Repository.GetAsync(runId))!.State);
        await fixture.SaveVerificationAsync(runId, first, success: false);

        // The failed receipt is offered to the model. Its revised manuscript
        // triggers a fresh check, instead of blindly repeating the same check.
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => fixture.Processor.ProcessAsync(runId, CancellationToken.None));
        Assert.Contains(fixture.Handler.ChatBodies[1].GetProperty("messages").EnumerateArray(), message =>
            message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            && content.GetString()!.Contains("PDF-Renderfehler", StringComparison.Ordinal));
        var second = await fixture.ProposalAsync(runId);
        Assert.NotEqual(first.ProposalId, second.ProposalId);
        Assert.DoesNotContain(await fixture.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
        await fixture.SaveVerificationAsync(runId, second, success: true);

        await fixture.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await fixture.Repository.GetAsync(runId))!.State);
        Assert.Equal(3, fixture.Handler.ChatBodies.Count);
        var completed = Assert.Single(await fixture.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
        var title = completed.Data.GetProperty("sessionTitle").GetString()!;
        Assert.DoesNotContain("MISSUM_", title, StringComparison.Ordinal);
        Assert.StartsWith("Untersuche Skalarfeld", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmptyProviderTurnCanStillRecoverTheScienceDeliverablesWithoutFailingTheRun()
    {
        using var fixture = new Fixture(repairManuscript: false, emptyFirstTurn: true);
        var runId = await fixture.CreateAsync(ScientificRunCompletionPolicyTests.SuccessfulWork());

        await Assert.ThrowsAsync<RunWaitingForClientException>(() => fixture.Processor.ProcessAsync(runId, CancellationToken.None));
        var proposal = await fixture.ProposalAsync(runId);
        Assert.Equal(ClientToolNames.ResearchDeliverablesVerify, proposal.Name);
        Assert.DoesNotContain(await fixture.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunFailed);
        await fixture.SaveVerificationAsync(runId, proposal, success: true);
        await fixture.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await fixture.Repository.GetAsync(runId))!.State);
    }

    [Fact]
    public async Task ManualStopCancelsAnUnchangedRecoveryBackoffAndPreservesTheWork()
    {
        using var fixture = new Fixture(repairManuscript: false);
        var messages = ScientificRunCompletionPolicyTests.SuccessfulWork();
        ScientificRunCompletionPolicyTests.AddVerification(messages, success: false, pdfReady: false);
        for (var index = 0; index < 2; index++)
            messages.Add(new("system", ScientificRunCompletionPolicy.RepairPrompt(
                ScientificRunCompletionPolicy.Assess(fixture.Request, messages, fixture.Tools))));
        var runId = await fixture.CreateAsync(messages);
        using var cancellation = new CancellationTokenSource();
        var processing = fixture.Processor.ProcessAsync(runId, cancellation.Token);
        try
        {
            await fixture.Handler.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AgentRunCheckpoint? checkpoint = null;
            for (var poll = 0; poll < 100; poll++)
            {
                checkpoint = await fixture.Repository.GetCheckpointAsync(runId);
                if (ScientificRunCompletionPolicy.Assess(fixture.Request, checkpoint!.Messages, fixture.Tools).RepeatedAttempts >= 3) break;
                await Task.Delay(10);
            }
            Assert.NotNull(checkpoint);
            var waiting = ScientificRunCompletionPolicy.Assess(fixture.Request, checkpoint.Messages, fixture.Tools);
            Assert.Equal(3, waiting.RepeatedAttempts);
            Assert.False(processing.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.DoesNotContain(await fixture.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
            Assert.NotNull(await fixture.Repository.GetCheckpointAsync(runId));
        }
        finally
        {
            cancellation.Cancel();
            try { await processing.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _runtime;
        private readonly ServiceProvider _provider;
        internal NativeHandler Handler { get; }
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }
        internal RunRequest Request { get; }
        internal IReadOnlyList<AgentToolSpec> Tools { get; }

        internal Fixture(bool repairManuscript, bool emptyFirstTurn = false, RunMode mode = RunMode.General)
        {
            _context.Options.ModelRuntimeUri = new("http://native.test");
            _context.Options.GeneralModelId = NativeHandler.ModelId;
            Handler = new(repairManuscript, emptyFirstTurn);
            _http = new(Handler);
            _runtime = new(_http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(_runtime);
            _provider = services.BuildServiceProvider();
            Repository = _provider.GetRequiredService<RunRepository>();
            Processor = _provider.GetRequiredService<RunProcessor>();
            Request = ScientificRunCompletionPolicyTests.Request() with
            {
                Mode = mode, AllowedServerTools = ["web.search", "web.fetch"],
                PreferredGeneralModelId = NativeHandler.ModelId, ReasoningEffort = "none",
                WorkspacePath = _context.Root, Limits = new(TimeoutSeconds: 0),
                Messages = [new("user", [new("text", "Untersuche das Skalarfeld und erstelle die Publikation mit Python-Abbildungen.")]),
                    new("user", [new("text", RunProcessor.ScienceSessionContextStart + "\nGespeicherter Kontext")])],
            };
            Tools = new AgentToolCatalog().GetAvailableTools(Request);
        }

        internal async Task<string> CreateAsync(IReadOnlyList<LmChatMessage> messages)
        {
            RunRequestValidator.Validate(Request);
            var runId = (await Repository.CreateAsync(Request, null)).Snapshot.RunId;
            await Repository.SaveCheckpointAsync(runId, new(messages, 1, 2, 128, 32, DeepResearchCompleted: true));
            await Repository.UpdateStateAsync(runId, RunState.Running);
            return runId;
        }

        internal async Task<ToolProposal> ProposalAsync(string runId)
        {
            var checkpoint = (await Repository.GetCheckpointAsync(runId))!;
            return (await Repository.GetToolProposalAsync(checkpoint.PendingProposalId!, runId))!;
        }

        internal async Task SaveVerificationAsync(string runId, ToolProposal proposal, bool success)
        {
            var messages = new List<LmChatMessage>();
            ScientificRunCompletionPolicyTests.AddVerification(messages, success, pdfReady: success);
            using var receipt = JsonDocument.Parse(messages[^1].Content!);
            await Repository.SaveClientToolResultAsync(runId, new(proposal.ProposalId, "completed", receipt.RootElement.GetProperty("result").Clone()));
        }

        public void Dispose()
        {
            _provider.Dispose();
            _runtime.Dispose();
            _http.Dispose();
            _context.Dispose();
        }
    }

    private sealed class NativeHandler(bool repairManuscript, bool emptyFirstTurn) : HttpMessageHandler
    {
        internal const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~completion-fixture";
        private static readonly string[] ModelTags = [
            "missum-context-train:1048576", "missum-reasoning-mode:llama-toggle", "missum-reasoning-levels:none|on", "missum-reasoning-default:on",
        ];
        internal List<JsonElement> ChatBodies { get; } = [];
        internal TaskCompletionSource GenerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new
            {
                data = new[] { new { id = ModelId, status = new { value = "loaded" }, tags = ModelTags } },
            });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 1_048_576 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 512 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ChatBodies.Add(document.RootElement.Clone());
            GenerationStarted.TrySetResult();
            var manuscript = ScientificRunCompletionPolicyTests.Manuscript;
            if (emptyFirstTurn && ChatBodies.Count == 1) manuscript = "";
            if (repairManuscript && ChatBodies.Count > 1)
                manuscript = manuscript.Replace("Referenzskalierung", "korrigierte Referenzskalierung", StringComparison.Ordinal);
            return Json(new { choices = new[] { new { message = new { content = manuscript }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 512, completion_tokens = 16 } });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }
}

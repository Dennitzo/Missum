using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificResearchGuardIntegrationTests
{
    private const string QuotaMessage = "Der eingefrorene Ausführungsstand überschreitet das 100-GiB-Sandboxkontingent; es wurden keine Eingaben kopiert.";

    [Fact]
    public async Task HostedProcessorStopsTheSuccessfulNullRemovalLoopAndPreservesItsCheckpoint()
    {
        using var fixture = new Fixture(Scenario.UnchangedUpdate);
        var session = fixture.First;
        var runId = await fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.Processor.StartAsync(deadline.Token);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var proposal = await Fixture.WaitForExecutionAsync(session, runId, deadline.Token, ClientToolNames.ResearchUpdate);
                var noChange = JsonSerializer.SerializeToElement(new { success = true, protocol = "section-delta-v1",
                    projectId = "science-project", revision = 31, publicationRevision = 19,
                    changedIds = Array.Empty<string>(), publicationChanged = false, replayed = false, stored = true,
                    message = "Forschungsstand unverändert." });
                Assert.True(await session.Repository.SaveClientToolResultAsync(runId,
                    new(proposal.ProposalId, "completed", noChange), deadline.Token));
                Assert.True(await session.Repository.TryQueueClientToolContinuationAsync(runId, proposal.ProposalId, deadline.Token));
                await session.Queue.EnqueueAsync(runId, deadline.Token);
            }
            await Fixture.WaitForFailureAsync(session, runId, deadline.Token);
        }
        finally { await session.Processor.StopAsync(CancellationToken.None); }
        var final = (await session.Repository.GetAsync(runId))!;
        Assert.Equal(RunState.Failed, final.State);
        Assert.Equal("science.unproductive_loop", final.ErrorCode);
        var events = await session.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Equal(2, events.Count(item => item.Type == RunEventTypes.ClientToolProposed
            && Text(item.Data, "name") == ClientToolNames.ResearchUpdate));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.True(Assert.Single(events, item => item.Type == RunEventTypes.RunFailed).Data.GetProperty("retryable").GetBoolean());
        var checkpoint = (await session.Repository.GetCheckpointAsync(runId))!;
        Assert.Null(checkpoint.PendingProposalId);
        Assert.Equal(2, checkpoint.ScientificStateProgress!.NoChangeUpdateAttempts);
        Assert.Equal(ScientificResearchProgressGuard.MaximumBlockedAttempts,
            Assert.Single(checkpoint.ScientificStateProgress.Failures!).BlockedAttempts);
        Assert.Equal(10, fixture.Handler.ChatBodies.Count);
    }

    [Fact]
    public async Task HostedScienceProcessorStopsUnchangedQuotaLoopAfterTwoActualProposalsAndEightSyntheticRefusals()
    {
        using var fixture = new Fixture(Scenario.Quota);
        var session = fixture.First;
        var runId = await fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.Processor.StartAsync(deadline.Token);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var proposal = await Fixture.WaitForExecutionAsync(session, runId, deadline.Token);
                await SaveQuotaAsync(session, runId, proposal, deadline.Token);
            }
            await Fixture.WaitForFailureAsync(session, runId, deadline.Token);
        }
        finally { await session.Processor.StopAsync(CancellationToken.None); }

        var snapshot = (await session.Repository.GetAsync(runId))!;
        Assert.Equal(RunState.Failed, snapshot.State);
        Assert.Equal(ScientificResearchProgressGuard.BlockedCode, snapshot.ErrorCode);
        var events = await session.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Equal(2, ExecutionProposals(events).Count());
        Assert.Equal(ScientificResearchProgressGuard.MaximumBlockedAttempts, events.Count(item =>
            item.Type == RunEventTypes.ModelGeneration && Text(item.Data, "failureKind") == ScientificResearchProgressGuard.RejectionCode));
        var failure = Assert.Single(events, item => item.Type == RunEventTypes.RunFailed);
        Assert.True(failure.Data.GetProperty("retryable").GetBoolean());
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        var checkpoint = (await session.Repository.GetCheckpointAsync(runId))!;
        var cause = Assert.Single(checkpoint.ScientificStateProgress!.Failures!);
        Assert.True(cause.PrerequisiteBlocked);
        Assert.Equal(ScientificResearchProgressGuard.MaximumBlockedAttempts, cause.BlockedAttempts);
        Assert.Equal(2, cause.FeedbackCount);
        Assert.Equal(QuotaMessage, cause.LastError);
        Assert.Null(checkpoint.PendingProposalId);
        Assert.Equal(ScientificResearchProgressGuard.MaximumBlockedAttempts, checkpoint.Messages.Count(message =>
            message.Role == "tool" && HasError(message.Content, ScientificResearchProgressGuard.RejectionCode)));
        // All the model's variants remain in the journal; only their non-execution
        // receipts are synthesized. No hidden Python process is part of this test.
        Assert.Equal(10, fixture.Handler.ChatBodies.Count);
        Assert.Equal(10, fixture.Handler.ExecutionArguments.Distinct(StringComparer.Ordinal).Count());

        var recovered = fixture.NewSession();
        var eligible = await recovered.Repository.RecoverAsync();
        Assert.DoesNotContain(runId, eligible);
        await recovered.Processor.ProcessAsync(runId, CancellationToken.None);
        Assert.Equal(2, ExecutionProposals(await recovered.Repository.GetEventsAfterAsync(runId, 0)).Count());
        Assert.Equal(10, fixture.Handler.ChatBodies.Count);
        Assert.NotNull(await recovered.Repository.GetCheckpointAsync(runId));
    }

    [Fact]
    public async Task RecoveryCollectsAnAlreadyDispatchedProposalBeforeConsultingSeededPrerequisiteGuard()
    {
        using var fixture = new Fixture(Scenario.ExecutionThenInvalid);
        var first = fixture.First;
        var runId = await fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        ToolProposal proposal;
        await first.Processor.StartAsync(deadline.Token);
        try { proposal = await Fixture.WaitForExecutionAsync(first, runId, deadline.Token); }
        finally { await first.Processor.StopAsync(CancellationToken.None); }

        var checkpoint = (await first.Repository.GetCheckpointAsync(runId))!;
        var progress = new ScientificStateRunProgress();
        var scratch = new List<LmChatMessage>();
        foreach (var index in Enumerable.Range(0, 2))
        {
            var call = new LmToolCall("historical-" + index.ToString(CultureInfo.InvariantCulture), proposal.Name, proposal.Arguments);
            progress = RunProcessor.ReduceScientificStateReceipt(fixture.Request, call,
                JsonSerializer.Serialize(new { status = "failed", errorCode = "client.tool_failed", message = QuotaMessage }),
                false, progress, scratch).Progress;
        }
        Assert.True(ScientificResearchProgressGuard.TryBlock(fixture.Request,
            new("still-in-flight", proposal.Name, proposal.Arguments), progress, out _));
        await first.Repository.SaveCheckpointAsync(runId, checkpoint with { ScientificStateProgress = progress });
        var measured = JsonSerializer.SerializeToElement(new
        {
            success = true, projectId = "science-project",
            runs = new[] { new { runId = "actual-in-flight-run", exitCode = 0, timedOut = false } },
        });
        Assert.True(await first.Repository.SaveClientToolResultAsync(runId,
            new(proposal.ProposalId, "completed", measured), deadline.Token));

        var restored = fixture.NewSession();
        await restored.Processor.StartAsync(deadline.Token);
        try { await Fixture.WaitForFailureAsync(restored, runId, deadline.Token); }
        finally { await restored.Processor.StopAsync(CancellationToken.None); }

        var events = await restored.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Single(ExecutionProposals(events));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ModelGeneration
            && Text(item.Data, "failureKind") == ScientificResearchProgressGuard.RejectionCode
            && Text(item.Data, "toolName") == ClientToolNames.ResearchCodeExecute);
        var final = (await restored.Repository.GetCheckpointAsync(runId))!;
        Assert.DoesNotContain(final.ScientificStateProgress!.Failures!, static failure => failure.PrerequisiteBlocked);
        var callId = checkpoint.PendingToolCallId!;
        var collected = Assert.Single(final.Messages, message => message.Role == "tool" && message.ToolCallId == callId);
        using var receipt = JsonDocument.Parse(collected.Content!);
        Assert.True(receipt.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Null(final.PendingProposalId);
        Assert.Equal(1 + CodingToolBatchRecovery.MaximumInvalidTurns, fixture.Handler.ChatBodies.Count);
    }

    [Fact]
    public async Task GeneralModeScienceStopsConsecutiveMalformedToolTurnsWithoutDispatchingOrClaimingCompletion()
    {
        using var fixture = new Fixture(Scenario.Invalid);
        var session = fixture.First;
        var runId = await fixture.CreateAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.Processor.StartAsync(deadline.Token);
        try { await Fixture.WaitForFailureAsync(session, runId, deadline.Token); }
        finally { await session.Processor.StopAsync(CancellationToken.None); }

        var snapshot = (await session.Repository.GetAsync(runId))!;
        Assert.Equal(RunState.Failed, snapshot.State);
        Assert.Equal("science.unproductive_loop", snapshot.ErrorCode);
        var events = await session.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Empty(ExecutionProposals(events));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ClientToolProposed
            && Text(item.Data, "name") == ClientToolNames.ResearchCodeWrite);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        Assert.True(Assert.Single(events, item => item.Type == RunEventTypes.RunFailed).Data.GetProperty("retryable").GetBoolean());
        Assert.Equal(CodingToolBatchRecovery.MaximumInvalidTurns, fixture.Handler.ChatBodies.Count);
        var checkpoint = (await session.Repository.GetCheckpointAsync(runId))!;
        Assert.Equal(CodingToolBatchRecovery.MaximumInvalidTurns, checkpoint.InvalidToolTurnCount);
        Assert.Null(checkpoint.PendingProposalId);
        Assert.Equal(CodingToolBatchRecovery.MaximumInvalidTurns, events.Count(item =>
            item.Type == RunEventTypes.ModelGeneration && Text(item.Data, "state") == "toolRejected"
                && Text(item.Data, "toolName") == ClientToolNames.ResearchCodeWrite));
    }

    private static async Task SaveQuotaAsync(Session session, string runId, ToolProposal proposal, CancellationToken token)
    {
        Assert.True(await session.Repository.SaveClientToolResultAsync(runId,
            new(proposal.ProposalId, "failed", JsonSerializer.SerializeToElement(new { failed = true }),
                "client.tool_failed", QuotaMessage), token));
        Assert.True(await session.Repository.TryQueueClientToolContinuationAsync(runId, proposal.ProposalId, token));
        await session.Queue.EnqueueAsync(runId, token);
    }

    private static IEnumerable<RunEvent> ExecutionProposals(IEnumerable<RunEvent> events) => events.Where(item =>
        item.Type == RunEventTypes.ClientToolProposed && Text(item.Data, "name") == ClientToolNames.ResearchCodeExecute);

    private static bool HasError(string? json, string code)
    {
        if (json is null) return false;
        using var document = JsonDocument.Parse(json);
        return Text(document.RootElement, "errorCode") == code;
    }

    private static string Text(JsonElement value, string name) => ScientificStateCompletionPolicy.Text(value, name);
    private enum Scenario { Quota, Invalid, ExecutionThenInvalid, UnchangedUpdate }
    private sealed record Session(RunProcessor Processor, RunRepository Repository, RunWorkChannel Queue, RunEventNotifier Notifier);

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly List<ServiceProvider> _providers = [];
        private readonly List<HttpClient> _clients = [];
        internal NativeHandler Handler { get; }
        internal Session First { get; }
        internal RunRequest Request { get; }

        internal Fixture(Scenario scenario)
        {
            _context.Options.ModelRuntimeUri = new("http://native.test");
            _context.Options.GeneralModelId = NativeHandler.ModelId;
            Handler = new(scenario);
            First = NewSession();
            Request = new(MissumAiProtocol.Version, RunMode.General,
                [new("user", [new("text", "Prüfe das Doppelspaltexperiment quantitativ.")])],
                PreferredGeneralModelId: NativeHandler.ModelId, ReasoningEffort: "none",
                ClientCapabilities: ["research.sandbox", "research.deliverables"], AllowedServerTools: ["web.search", "web.fetch"], DeepResearch: true,
                SessionId: "science-guard-session", WorkspacePath: _context.Root, Limits: new(TimeoutSeconds: 0),
                ResearchOptions: new(ProtocolVersion: 2, ProjectId: "science-project", Profile: DeepResearchProfile.ScientificEvidence,
                    AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));
        }

        internal Session NewSession()
        {
            var http = new HttpClient(Handler, disposeHandler: false);
            _clients.Add(http);
            var runtime = new ModelRuntimeClient(http, _context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(runtime);
            var provider = services.BuildServiceProvider();
            _providers.Add(provider);
            return new(provider.GetRequiredService<RunProcessor>(), provider.GetRequiredService<RunRepository>(),
                provider.GetRequiredService<RunWorkChannel>(), provider.GetRequiredService<RunEventNotifier>());
        }

        internal async Task<string> CreateAsync()
        {
            RunRequestValidator.Validate(Request);
            var runId = (await First.Repository.CreateAsync(Request, null)).Snapshot.RunId;
            await First.Repository.SaveCheckpointAsync(runId,
                new([new("system", "Nutze die angebotenen Werkzeuge."), new("user", "Prüfe die numerische Anforderung.")],
                    0, 0, 0, 0, ResearchManagedByAgent: true));
            return runId;
        }

        internal static async Task<ToolProposal> WaitForExecutionAsync(Session session, string runId, CancellationToken token,
            string expectedTool = ClientToolNames.ResearchCodeExecute)
        {
            using var subscription = session.Notifier.Subscribe(runId);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var pending = await PendingAsync(session, runId, token);
                if (pending?.Name == expectedTool) return pending;
                if (pending?.Name == ClientToolNames.ResearchRead)
                {
                    await SaveOverviewAsync(session, runId, pending, token);
                    continue;
                }
                Assert.NotEqual(RunState.Failed, (await session.Repository.GetAsync(runId, token))!.State);
                await subscription.Reader.ReadAsync(token);
            }
        }

        internal static async Task WaitForFailureAsync(Session session, string runId, CancellationToken token)
        {
            using var subscription = session.Notifier.Subscribe(runId);
            while (!(await session.Repository.GetEventsAfterAsync(runId, 0, token))
                .Any(item => item.Type == RunEventTypes.RunFailed))
            {
                var pending = await PendingAsync(session, runId, token);
                if (pending is not null)
                {
                    Assert.Equal(ClientToolNames.ResearchRead, pending.Name);
                    await SaveOverviewAsync(session, runId, pending, token);
                    continue;
                }
                await subscription.Reader.ReadAsync(token);
            }
        }

        private static async Task<ToolProposal?> PendingAsync(Session session, string runId, CancellationToken token)
        {
            var checkpoint = await session.Repository.GetCheckpointAsync(runId, token);
            if (checkpoint?.PendingProposalId is not { } id
                || await session.Repository.GetClientToolResultAsync(id, token) is not null) return null;
            // A checkpoint alone is not a client-visible dispatch. Respond only
            // after the atomic proposal journal event has actually been published.
            if (!(await session.Repository.GetEventsAfterAsync(runId, 0, token)).Any(item =>
                item.Type == RunEventTypes.ClientToolProposed && Text(item.Data, "proposalId") == id)) return null;
            return await session.Repository.GetToolProposalAsync(id, runId, token);
        }

        private static async Task SaveOverviewAsync(Session session, string runId, ToolProposal proposal, CancellationToken token)
        {
            var result = JsonSerializer.SerializeToElement(new
            {
                success = true, protocol = "section-delta-v1", projectId = "science-project", view = "overview",
                revision = 1, publicationRevision = 1, stateStamp = new string('a', 64), items = Array.Empty<object>(),
            });
            Assert.True(await session.Repository.SaveClientToolResultAsync(runId, new(proposal.ProposalId, "completed", result), token));
            Assert.True(await session.Repository.TryQueueClientToolContinuationAsync(runId, proposal.ProposalId, token));
            await session.Queue.EnqueueAsync(runId, token);
        }

        public void Dispose()
        {
            foreach (var provider in _providers.AsEnumerable().Reverse()) provider.Dispose();
            foreach (var client in _clients) client.Dispose();
            Handler.Dispose();
            _context.Dispose();
        }
    }

    private sealed class NativeHandler(Scenario scenario) : HttpMessageHandler
    {
        internal const string ModelId = "coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~science-guard";
        private static readonly string[] Tags = ["missum-context-train:1048576", "missum-reasoning-mode:llama-toggle", "missum-reasoning-levels:none|on"];
        internal List<JsonElement> ChatBodies { get; } = [];
        internal List<string> ExecutionArguments { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/sessions/prepare" or "/sessions/save") return new(HttpStatusCode.NotFound);
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId, status = new { value = "loaded" }, tags = Tags } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 1_048_576 } });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 512 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            ChatBodies.Add(document.RootElement.Clone());
            var round = ChatBodies.Count;
            if (scenario == Scenario.UnchangedUpdate)
            {
                var arguments = JsonSerializer.Serialize(new { projectId = "science-project", changes = new[] {
                    new { id = "sec_simplified_slit", kind = "section", expectedRevision = 8,
                        patch = new { review = (object?)null, quantitativeValidation = (object?)null } } } });
                return Tools([Call("update-" + round.ToString(CultureInfo.InvariantCulture), ClientToolNames.ResearchUpdate, arguments)]);
            }
            if (scenario == Scenario.Quota || scenario == Scenario.ExecutionThenInvalid && round == 1)
            {
                var number = round.ToString(CultureInfo.InvariantCulture);
                var arguments = JsonSerializer.Serialize(new
                {
                    projectId = "science-project", experimentId = "attempt-" + number, executable = "python",
                    arguments = new[] { "method-" + number + ".py", "--particles", number }, timeoutSeconds = 60,
                });
                ExecutionArguments.Add(arguments);
                return Tools([Call("execute-" + number, ClientToolNames.ResearchCodeExecute, arguments)]);
            }
            // General-mode native transport offers one call per turn. Valid JSON
            // with missing schema fields must reach the application guard, rather
            // than failing the provider's parallel-call/JSON transport contract.
            return Tools([Call("invalid-" + round.ToString(CultureInfo.InvariantCulture),
                ClientToolNames.ResearchCodeWrite, "{}")]);
        }

        private static object Call(string id, string name, string arguments) => new
        { id, type = "function", function = new { name = ModelRuntimeClient.ToTransportToolName(name), arguments } };

        private static HttpResponseMessage Tools(object[] calls) => Json(new
        {
            choices = new[] { new { message = new { content = (string?)null, tool_calls = calls }, finish_reason = "tool_calls" } },
            usage = new { prompt_tokens = 512, completion_tokens = 16 },
        });

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

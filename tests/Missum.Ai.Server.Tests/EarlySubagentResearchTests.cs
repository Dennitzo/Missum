using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Research;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class EarlySubagentResearchTests
{
    private const string ModelId = "coding/Qwen-Early-Fixture-Q4~123456";
    private static readonly string[] ResearchTools = ["web.search", "web.fetch", "web.deepResearch"];

    [Fact]
    public void ResumeKeepsTheExistingDelegationPolicyPrefixAcrossPolicyUpdates()
    {
        const string existing = "Existing instructions\nFrühe Arbeitsteilung für den aktuellen Forschungsauftrag:\nEarlier policy text.";
        var messages = new List<LmChatMessage> { new("system", existing), new("user", "Fortsetzen") };
        RunProcessor.EnsureEarlyResearchInstructions(messages);
        Assert.Equal(existing, messages[0].Content);
        var fresh = new List<LmChatMessage> { new("system", "Base") };
        RunProcessor.EnsureEarlyResearchInstructions(fresh);
        Assert.Contains("Halte diese erste Zuweisung klein", fresh[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalLegacyProfileCanReadTheFullOriginalTaskBeforeAssigningAChild()
    {
        using var fixture = new Fixture(readTaskBeforeSpawn: true);
        var request = Request() with { ClientCapabilities = ["workspace", "coding", "subagents", "research.deliverables"],
            ResearchOptions = new(ProjectId: "research-canonical-context", ProtocolVersion: 2) };
        var parent = await fixture.Repository.CreateAsync(request, null);
        var tools = new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true);
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            RunProcessor.CreateInitialMessages(request, "general", tools.Select(static tool => tool.Name).ToArray()),
            1, 1, 100, 20, SelectedModelId: ModelId, UseStableSubagentToolCatalog: true,
            ResearchManagedByAgent: true, EarlySubagentDelegationPending: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<RunWaitingForClientException>(
            () => fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token));
        var proposed = Assert.Single(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0),
            item => item.Type == RunEventTypes.ClientToolProposed);
        var taskRead = proposed.Data.Deserialize<ToolProposal>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal(ClientToolNames.ResearchRead, taskRead.Name);
        Assert.Equal("task", taskRead.Arguments.GetProperty("view").GetString());
        var checkpoint = (await fixture.Repository.GetCheckpointAsync(parent.Snapshot.RunId))!;
        Assert.True(checkpoint.EarlySubagentDelegationPending);
        Assert.Null(checkpoint.ContextProfileVersion);
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
    }

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public async Task MainAssignsBeforeSourcesAndContinuesItsOwnResearchWhileChildWorks(RunMode mode)
    {
        using var fixture = new Fixture(initiallyUnloaded: true);
        var request = Request(mode) with
        {
            ResearchOptions = new(DeepResearchProfile.SystematicReview, "research-real-question",
                ProtocolVersion: 2, ResumeCheckpointId: "existing-question-checkpoint",
                AutonomyLevel: ResearchAutonomyLevel.SandboxResearch,
                VerificationLevel: ResearchVerificationLevel.FormalWherePossible,
                MaximumWorks: 12, MaximumFullTexts: 4, PreferredLanguages: ["de", "en"]),
        };
        var parent = await fixture.Repository.CreateAsync(request, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await CompleteWithClientResultsAsync(fixture, parent.Snapshot.RunId, timeout.Token);

        var events = await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0);
        var spawn = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.GetProperty("tool").GetString() == SubagentToolNames.Spawn);
        var firstSource = Assert.Single(events, item => item.Type == RunEventTypes.ServerToolStarted
            && item.Data.GetProperty("tool").GetString() == "web.search");
        Assert.True(spawn.Data.GetProperty("success").GetBoolean());
        Assert.True(spawn.Id < firstSource.Id);
        var enrollment = Assert.Single(events, item => item.Type == RunEventTypes.ResearchProblemInterpreted);
        Assert.True(enrollment.Id < spawn.Id);
        Assert.Equal("research-real-question", enrollment.Data.GetProperty("projectId").GetString());
        Assert.Equal("agentManagedResearchStarted", enrollment.Data.GetProperty("state").GetString());
        Assert.Equal(parent.Snapshot.RunId, enrollment.Data.GetProperty("runId").GetString());
        Assert.Equal(request.SessionId, enrollment.Data.GetProperty("sessionId").GetString());
        Assert.Equal(ModelId, enrollment.Data.GetProperty("modelId").GetString());
        Assert.Equal(string.Join("\n", request.Messages[^1].Content.Select(static part => part.Text)),
            enrollment.Data.GetProperty("originalTask").GetString());
        Assert.Equal("systematicReview", enrollment.Data.GetProperty("profile").GetString());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request.ResearchOptions,
            MissumAiProtocol.CreateJsonOptions()), enrollment.Data.GetProperty("researchOptions")));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ResearchPlanUpdated);
        Assert.True(fixture.Handler.SearchedWhileChildWaiting);
        Assert.Equal("required", fixture.Handler.FirstParentToolChoice);
        Assert.Equal(1, fixture.Handler.SpawnDecisions);
        Assert.Equal(1, fixture.Handler.SearchRequests);
        Assert.True(fixture.Handler.ParentUsedChildResult);
        Assert.Contains("Frühe Arbeitsteilung", fixture.Handler.FirstParentPolicy);
        Assert.Equal(fixture.Handler.FirstParentPolicy, fixture.Handler.FirstChildPolicy);
        Assert.All(fixture.Handler.ParentSchemas.Concat(fixture.Handler.ChildSchemas), schema =>
            Assert.Equal(fixture.Handler.ParentSchemas[0], schema));
        Assert.DoesNotContain(fixture.Handler.ParentBodies, body => body.Contains("\"enable_thinking\":false", StringComparison.Ordinal));
        Assert.All(fixture.Handler.ParentBodies, body => Assert.Contains("\"enable_thinking\":true", body));
        var child = Assert.Single(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.False(child.Request.DeepResearch);
        Assert.Equal(request.ClientCapabilities, child.Request.ClientCapabilities);
        Assert.Equal(request.AllowedServerTools, child.Request.AllowedServerTools);
        Assert.Equal(request.WorkspacePath, child.Request.WorkspacePath);
        Assert.Equal(request.ReasoningEffort, child.Request.ReasoningEffort);
        Assert.Contains("Quellenanalyse\n", child.Request.Subagent!.AssignedTask);
        Assert.Equal(RunState.Completed, child.Snapshot.State);
        var completedParent = (await fixture.Repository.GetAsync(parent.Snapshot.RunId))!;
        Assert.Equal(RunState.Completed, completedParent.State);
        var introEvents = (await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0))
            .Where(item => item.Type == RunEventTypes.TextDelta);
        Assert.Contains(introEvents, item => item.Data.GetRawText().Contains("Ich teile die Forschung", StringComparison.Ordinal));
        Assert.Equal(RunProcessor.SanitizeTitle("", request.Messages[^1].Content[0].Text!), completedParent.SessionTitle);
        Assert.DoesNotContain(await fixture.Repository.GetEventsAfterAsync(child.Snapshot.RunId, 0),
            item => item.Type == RunEventTypes.ResearchProblemInterpreted);
    }

    [Fact]
    public async Task ModelCannotFetchSourcesBeforeItsValidIndependentAssignment()
    {
        using var fixture = new Fixture(rejectFirstAssignment: true);
        var parent = await fixture.Repository.CreateAsync(Request(), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await CompleteWithClientResultsAsync(fixture, parent.Snapshot.RunId, timeout.Token);

        Assert.Equal(1, fixture.Handler.SearchRequests);
        Assert.Equal(1, fixture.Handler.SpawnDecisions);
        Assert.All(fixture.Handler.ParentSchemas, schema => Assert.Equal(fixture.Handler.ParentSchemas[0], schema));
        Assert.Contains(fixture.Handler.ParentBodies, body => body.Contains("subagent.early_assignment_required", StringComparison.Ordinal));
        var calls = (await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0))
            .Where(item => item.Type == RunEventTypes.ServerToolStarted).ToArray();
        Assert.Equal(SubagentToolNames.Spawn, calls[0].Data.GetProperty("tool").GetString());
        Assert.Equal("web.search", calls[1].Data.GetProperty("tool").GetString());
    }

    [Fact]
    public async Task DurableAssignmentResumesWithoutNewModelAssignmentOrSerialResearchBootstrap()
    {
        using var fixture = new Fixture();
        var parent = await fixture.Repository.CreateAsync(Request(), null);
        var call = new LmToolCall("persisted-first-assignment", SubagentToolNames.Spawn,
            JsonSerializer.SerializeToElement(new { task = "Quellenanalyse\nUnabhängige Quellen prüfen; Hauptagent recherchiert den zweiten Bereich." }));
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            RunProcessor.CreateInitialMessages(Request(), "general", new AgentToolCatalog()
                .GetAvailableTools(Request(), subagentAvailable: true).Select(static tool => tool.Name).ToArray())
                .Concat([new LmChatMessage("assistant", ToolCalls: [call])]).ToArray(),
            1, 1, 100, 20, ActiveToolCalls: [call], ActiveCallRound: 1,
            SelectedModelId: ModelId, UseStableSubagentToolCatalog: true, ResearchManagedByAgent: true));
        await fixture.Repository.UpdateStateAsync(parent.Snapshot.RunId, RunState.Running);
        Assert.Contains(parent.Snapshot.RunId, await fixture.Repository.RecoverAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await CompleteWithClientResultsAsync(fixture, parent.Snapshot.RunId, timeout.Token);

        Assert.Equal(0, fixture.Handler.SpawnDecisions);
        Assert.Equal("auto", fixture.Handler.FirstParentToolChoice);
        Assert.Equal(1, fixture.Handler.PrepareCalls);
        Assert.Single(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Equal(1, fixture.Handler.SearchRequests);
        Assert.True(fixture.Handler.ParentUsedChildResult);
        Assert.Single(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0),
            item => item.Type == RunEventTypes.ResearchProblemInterpreted);
    }

    [Fact]
    public async Task ResearchEnrollmentSurvivesRecoveryWithoutReplacingTheOriginalReceiptOrEnrollingOtherRuns()
    {
        using var fixture = new Fixture();
        var request = Request();
        var parent = await fixture.Repository.CreateAsync(request, null);
        var metadata = JsonSerializer.SerializeToElement(new { projectId = "actual-project", originalTask = "Actual task" });
        var managed = new AgentRunCheckpoint([new("user", "Actual task")], 0, 0, 0, 0, ResearchManagedByAgent: true);
        await fixture.Repository.EnsureAgentManagedResearchEnrollmentAsync(parent.Snapshot.RunId, metadata);
        Assert.Empty(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0));
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, managed);
        await fixture.Repository.EnsureAgentManagedResearchEnrollmentAsync(parent.Snapshot.RunId, metadata);
        await fixture.Repository.UpdateStateAsync(parent.Snapshot.RunId, RunState.Running);
        Assert.Contains(parent.Snapshot.RunId, await fixture.Repository.RecoverAsync());
        await fixture.Repository.EnsureAgentManagedResearchEnrollmentAsync(parent.Snapshot.RunId,
            JsonSerializer.SerializeToElement(new { projectId = "must-not-replace", originalTask = "Synthetic replacement" }));
        var original = Assert.Single(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0),
            item => item.Type == RunEventTypes.ResearchProblemInterpreted);
        Assert.True(JsonElement.DeepEquals(metadata, original.Data));

        var child = await fixture.Repository.CreateAsync(request with
        { Subagent = new(parent.Snapshot.RunId, "child", "Actual assigned task", request.SessionId) }, null);
        var ordinary = await fixture.Repository.CreateAsync(request with { DeepResearch = false }, null);
        var legacy = await fixture.Repository.CreateAsync(request, null);
        foreach (var runId in new[] { child.Snapshot.RunId, ordinary.Snapshot.RunId, legacy.Snapshot.RunId })
        {
            await fixture.Repository.SaveCheckpointAsync(runId,
                runId == legacy.Snapshot.RunId ? managed with { ResearchManagedByAgent = false } : managed);
            await fixture.Repository.EnsureAgentManagedResearchEnrollmentAsync(runId, metadata);
            Assert.Empty(await fixture.Repository.GetEventsAfterAsync(runId, 0));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PersistedHardwareAdmissionSelectsEarlyManagerOrOriginalResearchPath(bool allowed)
    {
        using var fixture = new Fixture(pauseParent: true, denySubagent: !allowed);
        var scienceRequest = Request() with
        { ClientCapabilities = ["workspace", "coding", "subagents", "research.sandbox", "research.deliverables"] };
        var parent = await fixture.Repository.CreateAsync(scienceRequest, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await fixture.Handler.ParentStarted.Task.WaitAsync(timeout.Token);

        var checkpoint = (await fixture.Repository.GetCheckpointAsync(parent.Snapshot.RunId))!;
        Assert.Equal(allowed, checkpoint.ResearchManagedByAgent);
        Assert.Equal(allowed, checkpoint.EarlySubagentDelegationPending);
        Assert.Equal(allowed, checkpoint.UseStableSubagentToolCatalog);
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        if (allowed) Assert.Contains("Frühe Arbeitsteilung", fixture.Handler.FirstParentPolicy);
        else Assert.DoesNotContain("Frühe Arbeitsteilung", fixture.Handler.FirstParentPolicy ?? "");

        Assert.True(await fixture.Repository.CancelAsync(parent.Snapshot.RunId, timeout.Token));
        // Internal ProcessAsync has no hosted-worker cancellation registration.
        // Cancel its supplied token just as the hosted Stop path does.
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal(RunState.Cancelled, (await fixture.Repository.GetAsync(parent.Snapshot.RunId))!.State);
        Assert.Equal(0, fixture.Handler.PrepareCalls);
    }

    [Fact]
    public async Task RecoveryPreservesBoundedAssignmentRepairAndNeverExecutesRejectedSourceCalls()
    {
        using var fixture = new Fixture(rejectFirstAssignment: true);
        var request = Request();
        var parent = await fixture.Repository.CreateAsync(request, null);
        var tools = new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true);
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            RunProcessor.CreateInitialMessages(request, "general", tools.Select(static tool => tool.Name).ToArray()),
            2, 0, 200, 40, SelectedModelId: ModelId, UseStableSubagentToolCatalog: true,
            ResearchManagedByAgent: true, EarlySubagentDelegationPending: true, EarlySubagentDelegationRetryCount: 2));
        await fixture.Repository.UpdateStateAsync(parent.Snapshot.RunId, RunState.Running);
        Assert.Contains(parent.Snapshot.RunId, await fixture.Repository.RecoverAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await Assert.ThrowsAsync<AgentRunLimitException>(() => fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token));

        var checkpoint = (await fixture.Repository.GetCheckpointAsync(parent.Snapshot.RunId))!;
        Assert.Equal(3, checkpoint.EarlySubagentDelegationRetryCount);
        Assert.True(checkpoint.EarlySubagentDelegationPending);
        Assert.Equal(3, checkpoint.RoundCount);
        Assert.Contains(checkpoint.Messages, message => message.Role == "tool"
            && message.Content!.Contains("subagent.early_assignment_required", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Handler.SearchRequests);
        Assert.Equal(0, fixture.Handler.PrepareCalls);
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
    }

    [Fact]
    public async Task OrdinaryGreetingDoesNotRequireDelegation()
    {
        using var fixture = new Fixture(completeWithoutChild: true);
        var request = Request() with { DeepResearch = false, Messages = [new("user", [new("text", Text: "Hallo")])] };
        var parent = await fixture.Repository.CreateAsync(request, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);

        Assert.Equal("auto", fixture.Handler.FirstParentToolChoice);
        Assert.DoesNotContain("Frühe Arbeitsteilung", fixture.Handler.FirstParentPolicy ?? "");
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Equal(0, fixture.Handler.PrepareCalls);
    }

    [Fact]
    public async Task RestartedRequiredAssignmentReleasesOnlyItsRequirementWhenGpuAdmissionIsLost()
    {
        using var fixture = new Fixture(pauseParent: true, denySubagent: true, completeWithoutChild: true);
        var request = Request();
        var parent = await fixture.Repository.CreateAsync(request, null);
        var tools = new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true);
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            RunProcessor.CreateInitialMessages(request, "general", tools.Select(static tool => tool.Name).ToArray()),
            0, 0, 0, 0, SelectedModelId: ModelId, UseStableSubagentToolCatalog: true,
            ResearchManagedByAgent: true, EarlySubagentDelegationPending: true, EarlySubagentDelegationRetryCount: 1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await fixture.Handler.ParentStarted.Task.WaitAsync(timeout.Token);

        var checkpoint = (await fixture.Repository.GetCheckpointAsync(parent.Snapshot.RunId))!;
        Assert.True(checkpoint.ResearchManagedByAgent);
        Assert.True(checkpoint.UseStableSubagentToolCatalog);
        Assert.False(checkpoint.EarlySubagentDelegationPending);
        Assert.Equal(1, checkpoint.EarlySubagentDelegationRetryCount);
        Assert.Equal("auto", fixture.Handler.FirstParentToolChoice);
        Assert.Contains(ModelRuntimeClient.ToTransportToolName(SubagentToolNames.Spawn), fixture.Handler.ParentSchemas[0]);
        fixture.Handler.ContinueParent.TrySetResult();
        await processing;
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.DoesNotContain(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == RunEventTypes.ResearchPlanUpdated);
    }

    [Fact]
    public async Task ExistingCheckpointRetainsPendingProposalAndDoesNotAcquireNewAssignmentRequirement()
    {
        using var fixture = new Fixture(pauseParent: true, completeWithoutChild: true);
        var parent = await fixture.Repository.CreateAsync(Request(), null);
        var call = new LmToolCall("previous-client-read", ClientToolNames.CodingRead,
            JsonSerializer.SerializeToElement(new { path = "existing.txt" }));
        var proposal = new ToolProposal("previous-proposal", parent.Snapshot.RunId, call.Name,
            call.Arguments, ToolRiskClass.ReadOnly, "Already approved read", DateTimeOffset.MaxValue);
        await fixture.Repository.SaveToolProposalAsync(proposal);
        await fixture.Repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            [new("system", "Existing durable policy"), new("user", "Existing work"), new("assistant", ToolCalls: [call])],
            7, 3, 100, 20, ActiveToolCalls: [call], PendingProposalId: proposal.ProposalId,
            PendingToolCallId: call.Id, SelectedModelId: ModelId, DeepResearchCompleted: true,
            UseStableSubagentToolCatalog: true));
        Assert.NotNull(await fixture.Repository.EnsureClientToolProposedEventAsync(parent.Snapshot.RunId, proposal.ProposalId));
        await fixture.Repository.SaveClientToolResultAsync(parent.Snapshot.RunId, new(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { content = "EXISTING_RECEIPT" })));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = fixture.Processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await fixture.Handler.ParentStarted.Task.WaitAsync(timeout.Token);

        var checkpoint = (await fixture.Repository.GetCheckpointAsync(parent.Snapshot.RunId))!;
        Assert.False(checkpoint.ResearchManagedByAgent);
        Assert.False(checkpoint.EarlySubagentDelegationPending);
        Assert.Null(checkpoint.PendingProposalId);
        Assert.Contains(checkpoint.Messages, message => message.Role == "tool" && message.Content!.Contains("EXISTING_RECEIPT", StringComparison.Ordinal));
        Assert.DoesNotContain("Frühe Arbeitsteilung", fixture.Handler.FirstParentPolicy ?? "");
        Assert.Equal("auto", fixture.Handler.FirstParentToolChoice);
        fixture.Handler.ContinueParent.TrySetResult();
        await processing;
        Assert.Empty(await fixture.Repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Single(await fixture.Repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == RunEventTypes.ClientToolProposed);
    }

    private static RunRequest Request(RunMode mode = RunMode.General) => new(MissumAiProtocol.Version, mode,
        [new("user", [new("text", Text: "Untersuche zwei unabhängige Quellenbereiche ausführlich und verwende die Teilergebnisse direkt.")])],
        PreferredGeneralModelId: ModelId, PreferredCodingModelId: ModelId, ReasoningEffort: "xhigh",
        ClientCapabilities: ["workspace", "coding", "subagents"], AllowedServerTools: ResearchTools,
        SessionId: "early-research-session", WorkspacePath: "C:\early-research-fixture", DeepResearch: true);

    private static async Task CompleteWithClientResultsAsync(Fixture fixture, string parentRunId, CancellationToken token)
    {
        var processing = fixture.Processor.ProcessAsync(parentRunId, token);
        long cursor = 0;
        ToolProposal? childProposal = null;
        while (!processing.IsCompleted)
        {
            foreach (var item in await fixture.Repository.GetEventsAfterAsync(parentRunId, cursor, token))
            {
                cursor = item.Id;
                if (item.Type == RunEventTypes.SubagentEvent)
                {
                    var forwarded = item.Data.Deserialize<SubagentForwardedEvent>(MissumAiProtocol.CreateJsonOptions())!;
                    if (forwarded.Event.Type == RunEventTypes.ClientToolProposed)
                        childProposal = forwarded.Event.Data.Deserialize<ToolProposal>(MissumAiProtocol.CreateJsonOptions());
                }
            }
            if (childProposal is not null && fixture.Handler.SearchRequests > 0)
            {
                await fixture.Repository.SaveClientToolResultAsync(childProposal.RunId, new(childProposal.ProposalId, "completed",
                    JsonSerializer.SerializeToElement(new { path = "child.txt", content = "EARLY_CHILD_RESULT", sha256 = new string('a', 64) })), token);
                childProposal = null;
            }
            await Task.Delay(10, token);
        }
        await processing;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _provider;
        internal ResearchHandler Handler { get; }
        internal RunRepository Repository { get; }
        internal RunProcessor Processor { get; }

        internal Fixture(bool initiallyUnloaded = false, bool rejectFirstAssignment = false,
            bool pauseParent = false, bool denySubagent = false, bool completeWithoutChild = false, bool readTaskBeforeSpawn = false)
        {
            Handler = new(initiallyUnloaded, rejectFirstAssignment, pauseParent, denySubagent, completeWithoutChild, readTaskBeforeSpawn);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            services.AddSingleton(new ModelRuntimeClient(new HttpClient(Handler, disposeHandler: false), _context.WrappedOptions,
                NullLogger<ModelRuntimeClient>.Instance));
            services.AddSingleton(new WorkerApiClient(new HttpClient(Handler, disposeHandler: false), _context.WrappedOptions));
            services.AddSingleton(new WebResearchService(new ResearchHttpFactory(Handler), _context.WrappedOptions));
            services.AddSingleton(new ScientificMetadataService(new ResearchHttpFactory(Handler)));
            _provider = services.BuildServiceProvider();
            Repository = _provider.GetRequiredService<RunRepository>();
            Processor = _provider.GetRequiredService<RunProcessor>();
        }

        public void Dispose() { _provider.Dispose(); Handler.Dispose(); _context.Dispose(); }
    }

    private sealed class ResearchHttpFactory(ResearchHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ResearchHandler(bool initiallyUnloaded, bool rejectFirstAssignment, bool pauseParent,
        bool denySubagent, bool completeWithoutChild, bool readTaskBeforeSpawn) : HttpMessageHandler
    {
        private static readonly string[] ParentTags = ["missum-context-train:131072", "missum-reasoning-mode:qwen3.8",
            "missum-reasoning-levels:none|low|medium|xhigh", "missum-reasoning-default:xhigh"];
        private static readonly string[] ChildTags = ["missum-context-train:131072", "missum-agent-instance:subagent", "missum-base-model:" + ModelId,
            "missum-reasoning-mode:qwen3.8", "missum-reasoning-levels:none|low|medium|xhigh", "missum-reasoning-default:xhigh"];
        private bool _primaryLoaded = !initiallyUnloaded;
        private int _parentTurns;
        private int _childTurns;
        internal int SearchRequests { get; private set; }
        internal int SpawnDecisions { get; private set; }
        internal int PrepareCalls { get; private set; }
        internal bool SearchedWhileChildWaiting { get; private set; }
        internal bool ParentUsedChildResult { get; private set; }
        internal TaskCompletionSource ParentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ChildStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? FirstParentToolChoice { get; private set; }
        internal string? FirstParentPolicy { get; private set; }
        internal string? FirstChildPolicy { get; private set; }
        internal List<string> ParentSchemas { get; } = [];
        internal List<string> ChildSchemas { get; } = [];
        internal List<string> ParentBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is "/works" or "/dois" or "/entrez/eutils/esearch.fcgi") return Json(new { });
            if (path == "/api/query") return new(HttpStatusCode.OK)
            { Content = new StringContent("<feed xmlns=\"http://www.w3.org/2005/Atom\"/>", Encoding.UTF8, "application/xml") };
            if (path == "/v1/models") return Json(new { data = new[]
            {
                new { id = ModelId, tags = ParentTags, status = new { value = _primaryLoaded ? "loaded" : "unloaded" } },
                new { id = ModelId + "@subagent", tags = ChildTags, status = new { value = "loaded" } },
            } });
            if (path == "/models/load") { _primaryLoaded = true; return Json(new { success = true }); }
            if (path == "/release") return Json(new { success = true });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 131072 } });
            if (path is "/agents/status" or "/agents/prepare")
            {
                if (path == "/agents/prepare") PrepareCalls++;
                return Json(new { allowed = _primaryLoaded && !denySubagent, reason = denySubagent ? "subagent.insufficient_vram" : null,
                    modelId = ModelId, instanceId = ModelId + "@subagent", gpuIndex = 1, contextLength = 131072,
                    cacheStatus = "forked", cachedTokens = 100 });
            }
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { success = true });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path == "/search")
            {
                SearchRequests++;
                SearchedWhileChildWaiting = Volatile.Read(ref _childTurns) == 1;
                return Json(new { results = new[] { new { title = "Eigene Originalquelle", url = "https://example.com/main-source",
                    content = "Der Hauptagent recherchiert seinen eigenen Quellenbereich.", engine = "fixture" } } });
            }
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + path);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var body = document.RootElement;
            var messages = body.GetProperty("messages").EnumerateArray().ToArray();
            var schemas = body.GetProperty("tools").GetRawText();
            var policy = messages.First(static item => item.GetProperty("role").GetString() == "system").GetProperty("content").GetString();
            if (body.GetProperty("model").GetString() == ModelId + "@subagent")
            {
                ChildStarted.TrySetResult();
                ChildSchemas.Add(schemas);
                var turn = Interlocked.Increment(ref _childTurns);
                if (turn == 1) FirstChildPolicy = policy;
                return turn == 1 ? Tool(ClientToolNames.CodingRead, new { path = "child.txt" })
                    : Text("Die delegierte Quellenanalyse ist abgeschlossen: EARLY_CHILD_RESULT.");
            }
            var parentTurn = Interlocked.Increment(ref _parentTurns);
            ParentSchemas.Add(schemas);
            ParentBodies.Add(body.GetRawText());
            if (parentTurn == 1)
            {
                FirstParentPolicy = policy;
                FirstParentToolChoice = body.TryGetProperty("tool_choice", out var choice) ? choice.GetString() : null;
            }
            ParentStarted.TrySetResult();
            if (pauseParent) await ContinueParent.Task.WaitAsync(token);
            if (completeWithoutChild) return Text("Hallo. Der vorhandene Auftrag ist abgeschlossen.");
            if (readTaskBeforeSpawn && parentTurn == 1)
                return Tool(ClientToolNames.ResearchRead, new { projectId = "research-canonical-context", view = "task" });
            // Fail at the actual fixture contract problem instead of emitting
            // hundreds of identical spawn retries and reaching compaction.
            var preparationFailure = messages.Where(static item => item.GetProperty("role").GetString() == "tool")
                .Select(static item => item.GetProperty("content").GetString())
                .FirstOrDefault(static content => content?.Contains("subagent.preparation_failed", StringComparison.Ordinal) == true);
            if (preparationFailure is not null)
                throw new InvalidOperationException("The fixture's canonical child preparation failed: " + preparationFailure);
            if (rejectFirstAssignment && parentTurn == 1)
                return Tool("web.search", new { query = "must not execute before assignment" });
            var receipt = messages.Where(static item => item.GetProperty("role").GetString() == "tool")
                .Select(static item => item.GetProperty("content").GetString())
                .FirstOrDefault(static content => content?.Contains("\"status\":\"started\"", StringComparison.Ordinal) == true);
            if (receipt is null)
            {
                SpawnDecisions++;
                return Tool(SubagentToolNames.Spawn, new { task = "Quellenanalyse\nRecherchiere den unabhängigen ersten Quellenbereich und lies child.txt. Erwartet: belegtes EARLY_CHILD_RESULT. Nur child.txt schreiben. Ich recherchiere gleichzeitig den zweiten Quellenbereich." },
                    "Ich teile die Forschung auf und bearbeite gleichzeitig meine eigene Teilfrage.");
            }
            if (!messages.Any(static item => item.GetProperty("role").GetString() == "tool"
                && item.GetProperty("content").GetString()?.Contains("parent-original-sources", StringComparison.Ordinal) == true))
            {
                await ChildStarted.Task.WaitAsync(token);
                return Tool("web.search", new { query = "parent-original-sources" });
            }
            if (!messages.Any(static item => item.GetProperty("role").GetString() == "tool"
                && item.GetProperty("content").GetString()?.Contains("EARLY_CHILD_RESULT", StringComparison.Ordinal) == true))
            {
                using var started = JsonDocument.Parse(receipt);
                return Tool(SubagentToolNames.Wait, new { runId = started.RootElement.GetProperty("runId").GetString() });
            }
            ParentUsedChildResult = true;
            return Text("Eigene Quellenrecherche und EARLY_CHILD_RESULT sind direkt zusammengeführt. Die Arbeit ist abgeschlossen.");
        }

        private static HttpResponseMessage Tool(string name, object arguments, string? content = null) => Sse(new { content, tool_calls = new[]
        {
            new { index = 0, id = Guid.NewGuid().ToString("N"), type = "function", function = new
            { name = ModelRuntimeClient.ToTransportToolName(name), arguments = JsonSerializer.Serialize(arguments) } },
        } }, "tool_calls");
        private static HttpResponseMessage Text(string content) => Sse(new { content }, "stop");
        private static HttpResponseMessage Sse(object delta, string finish) => new(HttpStatusCode.OK)
        { Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } })
            + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class SubagentOrchestrationTests
{
    private const string ModelId = "coding/Qwen-Fixture-Q4~123456";

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void GpuAdmissionControlsDelegationAndChildRetainsIdenticalFullToolSchemas(RunMode mode)
    {
        var catalog = new AgentToolCatalog();
        var request = new RunRequest(MissumAiProtocol.Version, mode,
            [new("user", [new("text", Text: "Bearbeite zwei unabhängige Dateien.")])],
            ClientCapabilities: ["coding", "coding.process", "coding.evidence", "workspace", "workspace.open", "documents", "documentIo", "visual-tools", "subagents"],
            AllowedServerTools: ["web.search", "web.fetch", "media.analyze", "math.evaluate"], WorkspacePath: "C:\\work");
        var main = catalog.GetAvailableTools(request, subagentAvailable: true);
        var gated = catalog.GetAvailableTools(request, subagentAvailable: false);
        var child = catalog.GetAvailableTools(request with { Subagent = new("parent", "agent", "Aufgabe", "session") }, subagentAvailable: true);

        Assert.Contains(main, tool => tool.Name == SubagentToolNames.Spawn);
        Assert.Contains(main, tool => tool.Name == SubagentToolNames.Wait);
        Assert.DoesNotContain(gated, tool => SubagentToolNames.All.Contains(tool.Name));
        Assert.Equal(main.Select(tool => tool.Name), child.Select(tool => tool.Name));
        Assert.Equal(JsonSerializer.Serialize(RunProcessor.CreateModelToolDefinitions(main, selectedToolName: null, directTools: true)),
            JsonSerializer.Serialize(RunProcessor.CreateModelToolDefinitions(child, SubagentToolNames.Spawn, directTools: true)));
        Assert.Contains(child, tool => tool.Name == ClientToolNames.CodingRead);
        Assert.Contains(child, tool => tool.Name == ClientToolNames.CodingWrite);
        Assert.Contains(child, tool => tool.Name == "coding.command");
    }

    [Fact]
    public void ContextForkPreservesExactParentPrefixWithoutRepeatingPendingParentCalls()
    {
        var call = new LmToolCall("parent-read", "coding.read", JsonSerializer.SerializeToElement(new { path = "main.txt" }));
        IReadOnlyList<LmChatMessage> messages = [new("system", "Original system policy"), new("user", "Original task"),
            new("assistant", "Parent reasoning and tool action", ToolCalls: [call], ReasoningContent: "Original reasoning")];
        var fork = RunProcessor.ForkSubagentMessages(messages, "Write only child.txt");

        Assert.Equal(messages, fork.Take(messages.Count));
        Assert.Equal(3, messages.Count);
        Assert.Equal("parent-read", fork[^2].ToolCallId);
        Assert.Contains("parent_owned", fork[^2].Content);
        Assert.Contains("Write only child.txt", fork[^1].Content);
        Assert.Contains("systemweit Dateien lesen", fork[^1].Content);
    }

    [Fact]
    public void ContextForkClosesCurrentPendingCallWhenProviderReusesAnEarlierCompletedToolId()
    {
        var previous = new LmToolCall("call_1", ClientToolNames.CodingRead, JsonSerializer.SerializeToElement(new { path = "old.txt" }));
        var current = new LmToolCall("call_1", SubagentToolNames.Spawn, JsonSerializer.SerializeToElement(new { task = "Separate task" }));
        IReadOnlyList<LmChatMessage> messages = [new("system", "Original"), new("user", "Task"),
            new("assistant", ToolCalls: [previous]), new("tool", "Previously completed read", ToolCallId: previous.Id),
            new("assistant", ToolCalls: [current])];

        var fork = RunProcessor.ForkSubagentMessages(messages, "Separate task");

        Assert.Equal(messages, fork.Take(messages.Count));
        var closure = Assert.Single(fork.Skip(messages.Count), message => message.Role == "tool");
        Assert.Equal("call_1", closure.ToolCallId);
        Assert.Contains("parent_owned", closure.Content);
        Assert.Equal("MISSUM_SUBAGENT_AUFTRAG", fork[^1].Content!.Split('\n')[0]);
    }

    [Fact]
    public async Task ChildCreationAtomicallyPersistsPermissionsModelAndExactCheckpoint()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parent = await repository.CreateAsync(Request(), null);
        var request = Request() with { SessionId = "child-session", Subagent = new(parent.Snapshot.RunId, "agent", "Task", "parent-session", ModelId + "@subagent") };
        var checkpoint = new AgentRunCheckpoint([new("system", "Exact evaluated parent prefix"), new("user", "Child task")],
            0, 0, 0, 0, PreserveSessionPromptPrefix: true, SelectedModelId: ModelId);

        var child = await repository.CreateSubagentAsync(request, checkpoint, "parent-tool-operation");
        var duplicate = await repository.CreateSubagentAsync(request, checkpoint, "parent-tool-operation");
        var stored = await repository.GetRequestAsync(child.RunId);

        Assert.Equal(child.RunId, duplicate.RunId);
        Assert.Equal(request.ClientCapabilities, stored!.ClientCapabilities);
        Assert.Equal(request.AllowedServerTools, stored.AllowedServerTools);
        Assert.Equal(request.WorkspacePath, stored.WorkspacePath);
        Assert.Equal(request.Subagent, stored.Subagent);
        Assert.Equal(checkpoint.Messages, (await repository.GetCheckpointAsync(child.RunId))!.Messages);
        Assert.Single(await repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
    }

    [Fact]
    public async Task DurableForwardingReplaysOnceAndKeepsOriginalChildProposalIdentity()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parent = await repository.CreateAsync(Request(), null);
        var child = await repository.CreateAsync(Request() with { Subagent = new(parent.Snapshot.RunId, "agent", "Task", "session") }, null);
        var proposal = new ToolProposal("proposal-child", child.Snapshot.RunId, ClientToolNames.CodingWrite,
            JsonSerializer.SerializeToElement(new { path = "child.txt", content = "result" }), ToolRiskClass.LocalMutation,
            "Write child result", DateTimeOffset.MaxValue);
        var raw = await repository.AppendEventAsync(child.Snapshot.RunId, RunEventTypes.ClientToolProposed, proposal);
        var forwarded = new SubagentForwardedEvent("agent", parent.Snapshot.RunId, child.Snapshot.RunId, raw);

        Assert.True(await repository.ForwardSubagentEventAsync(forwarded));
        Assert.False(await repository.ForwardSubagentEventAsync(forwarded));
        Assert.Equal(raw.Id, await repository.GetSubagentForwardCursorAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        var wrapper = Assert.Single(await repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0));
        var replay = wrapper.Data.Deserialize<SubagentForwardedEvent>(MissumAiProtocol.CreateJsonOptions())!;
        Assert.Equal(raw.Id, replay.Event.Id);
        Assert.Equal(child.Snapshot.RunId, replay.Event.RunId);
        Assert.Equal(child.Snapshot.RunId, replay.Event.Data.GetProperty("runId").GetString());
        Assert.Equal("proposal-child", replay.Event.Data.GetProperty("proposalId").GetString());
    }

    [Fact]
    public async Task GatewayRecoveryResumesBothSidesOfInterruptedGeneralDelegation()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parent = await repository.CreateAsync(Request(), null);
        var child = await repository.CreateAsync(Request() with { Subagent = new(parent.Snapshot.RunId, "agent", "Task", "session") }, null);
        await repository.UpdateStateAsync(parent.Snapshot.RunId, RunState.Running);
        await repository.UpdateStateAsync(child.Snapshot.RunId, RunState.Running);

        var recovered = await repository.RecoverAsync();

        Assert.Contains(parent.Snapshot.RunId, recovered);
        Assert.Contains(child.Snapshot.RunId, recovered);
        Assert.Equal(RunState.Queued, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
        Assert.Equal(RunState.Queued, (await repository.GetAsync(child.Snapshot.RunId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultConsumptionRequiresCommittedParentReceiptAndSurvivesRepositoryRecovery(bool automaticallyCollected)
    {
        using var context = new TestServerContext();
        var notifier = new RunEventNotifier();
        var repository = new RunRepository(context.Database, notifier);
        var parent = await repository.CreateAsync(Request(), null);
        var childRequest = Request() with { Subagent = new(parent.Snapshot.RunId, "result-agent", "Task", "session") };
        var child = await repository.CreateAsync(childRequest, null);
        await repository.UpdateStateAsync(child.Snapshot.RunId, RunState.Completed);
        var startedReceipt = JsonSerializer.Serialize(new { status = "started", runId = child.Snapshot.RunId });
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId,
            new([new("system", "Parent policy"), new("tool", startedReceipt, ToolCallId: "spawn")], 1, 1, 1, 1));

        // Child completion and fetching its result are insufficient. A crash at
        // this point must still collect its work after recovery.
        Assert.False(await repository.TryConsumeSubagentResultAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        Assert.False(await repository.HasConsumedSubagentResultAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        var result = JsonSerializer.SerializeToElement(new { status = "completed", runId = child.Snapshot.RunId, result = "Actual completed child work" });
        var message = automaticallyCollected
            ? new LmChatMessage("user", SubagentToolNames.ResultContextMarker + "Ergebnisse:\n" + JsonSerializer.Serialize(new[] { result }))
            : new LmChatMessage("tool", result.GetRawText(), ToolCallId: "wait");
        List<LmChatMessage> acceptedMessages = [new("system", "Parent policy"), new("tool", startedReceipt, ToolCallId: "spawn"), message];
        var originalPrefix = acceptedMessages.ToArray();
        Assert.True(RunProcessor.AppendSubagentResultAcceptanceInstruction(acceptedMessages, result));
        Assert.Equal(originalPrefix, acceptedMessages.Take(originalPrefix.Length));
        Assert.Equal("Parent policy", acceptedMessages[0].Content);
        Assert.Contains("Dateiinventur", acceptedMessages[^1].Content);
        Assert.Contains("SymPy", acceptedMessages[^1].Content);
        Assert.Contains("research.deliverables.verify", acceptedMessages[^1].Content);
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId,
            new(acceptedMessages, 2, 2, 2, 2));

        Assert.True(await repository.TryConsumeSubagentResultAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        Assert.True(await repository.TryConsumeSubagentResultAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        var recovered = new RunRepository(context.Database, notifier);
        Assert.True(await recovered.HasConsumedSubagentResultAsync(parent.Snapshot.RunId, child.Snapshot.RunId));
        var restored = (await recovered.GetCheckpointAsync(parent.Snapshot.RunId))!.Messages.ToList();
        Assert.Equal(message, restored[^2]);
        Assert.EndsWith(SubagentAgentPolicy.CompletedWork, restored[^1].Content);
        Assert.False(RunProcessor.AppendSubagentResultAcceptanceInstruction(restored, result));
        Assert.False(RunProcessor.AppendSubagentResultAcceptanceInstruction(restored,
            JsonSerializer.SerializeToElement(new { status = "failed", runId = "failed-child", errorCode = "subagent.failed" })));
        Assert.Equal(acceptedMessages.Count, restored.Count);
        await recovered.SaveCheckpointAsync(parent.Snapshot.RunId, new(restored, 2, 2, 2, 2));
        Assert.Single(await recovered.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == SubagentToolNames.ResultConsumedEvent);
    }

    [Fact]
    public async Task ParentWaitReceivesChildClientToolResultWhileMainQueueIsOccupied()
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(emitInitialSpawn: true);
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
        var parent = await repository.CreateAsync(Request(), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var processing = processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        long cursor = 0;
        var executed = new HashSet<string>(StringComparer.Ordinal);
        while (!processing.IsCompleted)
        {
            foreach (var item in await repository.GetEventsAfterAsync(parent.Snapshot.RunId, cursor, timeout.Token))
            {
                cursor = item.Id;
                if (item.Type != RunEventTypes.SubagentEvent) continue;
                var forwarded = item.Data.Deserialize<SubagentForwardedEvent>(MissumAiProtocol.CreateJsonOptions())!;
                if (forwarded.Event.Type != RunEventTypes.ClientToolProposed) continue;
                var proposal = forwarded.Event.Data.Deserialize<ToolProposal>(MissumAiProtocol.CreateJsonOptions())!;
                if (!executed.Add(proposal.ProposalId)) continue;
                Assert.NotEqual(parent.Snapshot.RunId, proposal.RunId);
                Assert.Equal(ClientToolNames.CodingRead, proposal.Name);
                await repository.SaveClientToolResultAsync(proposal.RunId, new(proposal.ProposalId, "completed",
                    JsonSerializer.SerializeToElement(new { path = "child.txt", content = "CHILD_MARKER_731", sha256 = new string('a', 64) })), timeout.Token);
            }
            await Task.Delay(10, timeout.Token);
        }
        await processing;
        Assert.Single(executed);
        Assert.True(handler.ParentWorkedWhileChildActive);
        Assert.True(handler.ParentUsedChildResult);
        Assert.NotEmpty(handler.ParentSchemas);
        Assert.NotEmpty(handler.ChildSchemas);
        Assert.All(handler.ParentSchemas.Concat(handler.ChildSchemas), schema => Assert.Equal(handler.ParentSchemas[0], schema));
        Assert.Equal(handler.FirstParentSystemPolicy, handler.FirstChildSystemPolicy);
        Assert.DoesNotContain(ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName), handler.ParentToolNames);
        Assert.Equal(RunState.Completed, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
        var childRun = Assert.Single(await repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Equal(RunState.Completed, childRun.Snapshot.State);
        Assert.Equal(ModelId, childRun.Request.PreferredGeneralModelId);
        Assert.Equal(ModelId + "@subagent", childRun.Request.Subagent!.RuntimeInstanceId);
        Assert.Equal(Request().ClientCapabilities, childRun.Request.ClientCapabilities);
        Assert.Contains(await repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == RunEventTypes.SubagentCompleted);
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Delegate a separate file task.")])], ClientCapabilities: ["workspace", "coding", "subagents"],
        AllowedServerTools: [], PreferredGeneralModelId: ModelId, SessionId: "parent-session", WorkspacePath: "C:\\subagent-fixture");

    [Fact]
    public async Task GenericNativeVramDenialNeverPreparesOrStartsAChild()
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(denySubagent: true);
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
        var parent = await repository.CreateAsync(Request(), null);
        var spawn = new LmToolCall("spawn-denied", SubagentToolNames.Spawn, JsonSerializer.SerializeToElement(new { task = "Work on GPU1" }));
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId, new AgentRunCheckpoint(
            [new("system", "Test policy"), new("user", "Task"), new("assistant", ToolCalls: [spawn])],
            1, 1, 10, 10, ActiveToolCalls: [spawn], SelectedModelId: ModelId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);

        Assert.Empty(await repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Equal(0, handler.PrepareCalls);
        Assert.Equal(0, handler.ChildTurns);
        Assert.DoesNotContain(handler.ParentToolNames, name => name.Contains("subagent", StringComparison.Ordinal));
        Assert.Equal(RunState.Completed, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
    }

    [Fact]
    public async Task InitiallyUnloadedMainModelAdmitsSubagentToolsAndPolicyBeforeItsFirstNativePrompt()
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(initiallyUnloaded: true, pauseParent: true);
        using var http = new HttpClient(handler, disposeHandler: false);
        using var workerHttp = new HttpClient(handler, disposeHandler: false);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        services.AddSingleton(new WorkerApiClient(workerHttp, context.WrappedOptions));
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var parent = await repository.CreateAsync(Request(), null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var processing = provider.GetRequiredService<RunProcessor>().ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await handler.ParentStarted.Task.WaitAsync(timeout.Token);
        // Completion atomically deletes the transient checkpoint. Inspect the
        // durable schema decision while the first model turn is still active.
        Assert.True((await repository.GetCheckpointAsync(parent.Snapshot.RunId))!.UseStableSubagentToolCatalog);
        handler.ContinueParent.TrySetResult();
        await processing;

        Assert.True(handler.DeniedBeforeLoad);
        Assert.Equal(1, handler.MainLoads);
        Assert.Contains(ModelRuntimeClient.ToTransportToolName(SubagentToolNames.Spawn), handler.ParentToolNames);
        Assert.Contains(ModelRuntimeClient.ToTransportToolName(SubagentToolNames.Wait), handler.ParentToolNames);
        Assert.DoesNotContain(ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName), handler.ParentToolNames);
        Assert.Contains("Parallel arbeitender Subagent", handler.FirstParentSystemPolicy);
        Assert.Null(await repository.GetCheckpointAsync(parent.Snapshot.RunId));
        Assert.Equal(RunState.Completed, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
    }

    [Fact]
    public async Task PersistedParentCancellationCancelsGpu1AndCannotBeOverwrittenByLateCompletion()
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(holdChild: true);
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
        var parent = await repository.CreateAsync(Request(), null);
        var childRequest = Request() with { SessionId = "cancel-child-session", Subagent = new(parent.Snapshot.RunId,
            "cancel-agent", "Long child task", "parent-session", ModelId + "@subagent") };
        var child = await repository.CreateSubagentAsync(childRequest,
            new([new("system", "Test"), new("user", "Long child task")], 0, 0, 0, 0, SelectedModelId: ModelId), "cancel-child-operation");
        var wait = new LmToolCall("wait-child", SubagentToolNames.Wait, JsonSerializer.SerializeToElement(new { runId = child.RunId }));
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId, new AgentRunCheckpoint(
            [new("system", "Test"), new("user", "Wait"), new("assistant", ToolCalls: [wait])],
            1, 1, 10, 10, ActiveToolCalls: [wait], SelectedModelId: ModelId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await handler.ChildStarted.Task.WaitAsync(timeout.Token);

        Assert.True(await repository.CancelAsync(parent.Snapshot.RunId, timeout.Token));
        Assert.True(processor.Cancel(parent.Snapshot.RunId));
        await processing;

        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(child.RunId))!.State);
        Assert.Equal(0, handler.ParentTurns);
        Assert.False(await repository.FinalizeConversationAsync(parent.Snapshot.RunId, new("Late answer", ModelId, 0, 0)));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(child.RunId, 0), item => item.Type == RunEventTypes.RunCompleted);
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    public async Task TerminalParentStopsActiveGpu1InferenceAndDrainsItsCancellationBeforeReturning(RunState parentState)
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(holdChild: true);
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
        var parent = await repository.CreateAsync(Request(), null);
        var childRequest = Request() with { SessionId = "terminal-child-session", Subagent = new(parent.Snapshot.RunId,
            "terminal-agent", "Long child task", "parent-session", ModelId + "@subagent") };
        var child = await repository.CreateSubagentAsync(childRequest,
            new([new("system", "Test"), new("user", "Long child task")], 0, 0, 0, 0, SelectedModelId: ModelId), "terminal-child-operation");
        var wait = new LmToolCall("terminal-wait", SubagentToolNames.Wait, JsonSerializer.SerializeToElement(new { runId = child.RunId }));
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            [new("system", "Test"), new("user", "Wait"), new("assistant", ToolCalls: [wait])],
            1, 1, 10, 10, ActiveToolCalls: [wait], SelectedModelId: ModelId));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var processing = processor.ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await handler.ChildStarted.Task.WaitAsync(timeout.Token);
        await repository.UpdateStateAsync(parent.Snapshot.RunId, parentState, errorCode: "fixture.terminal_parent");

        await processor.CancelSubagentsForTerminalParentAsync(parent.Snapshot.RunId).WaitAsync(timeout.Token);
        await processing.WaitAsync(timeout.Token);

        Assert.True(handler.ChildInferenceCancelled);
        Assert.Equal(parentState, (await repository.GetAsync(parent.Snapshot.RunId))!.State);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(child.RunId))!.State);
        Assert.Equal(0, handler.ParentTurns);
        Assert.False(await repository.FinalizeConversationAsync(child.RunId, new("Late child answer", ModelId, 0, 0)));
        Assert.DoesNotContain(await repository.GetEventsAfterAsync(child.RunId, 0), item => item.Type == RunEventTypes.RunCompleted);
        Assert.Contains(await repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == RunEventTypes.SubagentCompleted
            && item.Data.GetProperty("runId").GetString() == child.RunId);
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    public async Task TerminalParentCancelsPersistedBranchesIdempotentlyAndPreservesCompletedAndForeignChildren(RunState parentState)
    {
        using var context = new TestServerContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var parent = (await repository.CreateAsync(Request(), null)).Snapshot;
        var foreignParent = (await repository.CreateAsync(Request() with { SessionId = "foreign-parent" }, null)).Snapshot;
        var childRequest = Request() with { SessionId = "persisted-child", Subagent = new(parent.RunId,
            "persisted-agent", "Assigned task", "parent-session", ModelId + "@subagent") };
        var queued = await repository.CreateSubagentAsync(childRequest, new([], 0, 0, 0, 0), "persisted-queued");
        var completed = await repository.CreateSubagentAsync(childRequest with { SessionId = "completed-child" },
            new([], 0, 0, 0, 0), "persisted-completed");
        await repository.AppendEventAsync(completed.RunId, RunEventTypes.TextDelta, new TextDeltaEvent("Useful completed child result"));
        await repository.UpdateStateAsync(completed.RunId, RunState.Completed);
        var foreign = await repository.CreateSubagentAsync(childRequest with { SessionId = "foreign-child",
            Subagent = childRequest.Subagent! with { ParentRunId = foreignParent.RunId } }, new([], 0, 0, 0, 0), "persisted-foreign");
        await repository.UpdateStateAsync(parent.RunId, parentState);

        await processor.CancelSubagentsForTerminalParentAsync(parent.RunId);
        await processor.CancelSubagentsForTerminalParentAsync(parent.RunId);
        // The central terminal hook is also invoked for a child's own failure.
        // It must return immediately rather than touching its parent/siblings.
        await processor.CancelSubagentsForTerminalParentAsync(queued.RunId);

        Assert.Equal(parentState, (await repository.GetAsync(parent.RunId))!.State);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(queued.RunId))!.State);
        Assert.Equal(RunState.Completed, (await repository.GetAsync(completed.RunId))!.State);
        Assert.Equal(RunState.Queued, (await repository.GetAsync(foreign.RunId))!.State);
        Assert.Single(await repository.GetEventsAfterAsync(queued.RunId, 0), item => item.Type == RunEventTypes.RunCancelled);
        Assert.Single(await repository.GetEventsAfterAsync(parent.RunId, 0), item => item.Type == RunEventTypes.SubagentCompleted
            && item.Data.GetProperty("runId").GetString() == queued.RunId);
        Assert.Equal("Useful completed child result", CodingTextReconciler.Project(await repository.GetVisibleTextEventsAsync(completed.RunId)));
    }

    [Theory]
    [InlineData(SubagentToolNames.Spawn)]
    [InlineData(SubagentToolNames.Wait)]
    public async Task ChildInheritsManagementSchemasButCannotExecuteRecursiveDelegation(string toolName)
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(childImmediatelyComplete: true);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var parent = await repository.CreateAsync(Request(), null);
        var request = Request() with { SessionId = "recursive-child", Subagent = new(parent.Snapshot.RunId,
            "recursive-agent", "Own task", "parent-session", ModelId + "@subagent") };
        var arguments = toolName == SubagentToolNames.Spawn
            ? JsonSerializer.SerializeToElement(new { task = "Forbidden nested task" })
            : JsonSerializer.SerializeToElement(new { runId = parent.Snapshot.RunId });
        var call = new LmToolCall("recursive-call", toolName, arguments);
        var child = await repository.CreateSubagentAsync(request, new(
            [new("system", "Exact inherited parent policy"), new("user", "Own task"), new("assistant", ToolCalls: [call])],
            1, 1, 0, 0, ActiveToolCalls: [call], SelectedModelId: ModelId, UseStableSubagentToolCatalog: true), "recursive-operation");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await provider.GetRequiredService<RunProcessor>().ProcessAsync(child.RunId, timeout.Token);

        var events = await repository.GetEventsAfterAsync(child.RunId, 0);
        Assert.Contains(events, item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.TryGetProperty("errorCode", out var code) && code.GetString() == "subagent.recursion_denied");
        Assert.Empty(await repository.GetSubagentRunsAsync(child.RunId));
        Assert.Equal(0, handler.PrepareCalls);
        Assert.Equal(ModelRuntimeClient.PrepareLanguageBoundMessages([new("system", "Exact inherited parent policy")])[0].Content,
            handler.FirstChildSystemPolicy);
        var expectedSchemas = JsonSerializer.Serialize(new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true)
            .Select(static tool => ModelRuntimeClient.ToTransportToolName(tool.Name)).ToArray());
        using var actualSchemas = JsonDocument.Parse(Assert.Single(handler.ChildSchemas));
        Assert.Equal(expectedSchemas, JsonSerializer.Serialize(actualSchemas.RootElement.EnumerateArray()
            .Select(static tool => tool.GetProperty("function").GetProperty("name").GetString()).ToArray()));
    }

    [Fact]
    public async Task PersistedStableSchemasSurviveResourceDenialButSpawnRemainsGated()
    {
        using var context = new TestServerContext();
        using var handler = new DualAgentHandler(denySubagent: true, pauseParent: true);
        using var http = new HttpClient(handler);
        using var runtime = new ModelRuntimeClient(http, context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        services.AddSingleton(runtime);
        using var provider = services.BuildServiceProvider();
        var repository = provider.GetRequiredService<RunRepository>();
        var parent = await repository.CreateAsync(Request(), null);
        var spawn = new LmToolCall("stable-spawn-denied", SubagentToolNames.Spawn,
            JsonSerializer.SerializeToElement(new { task = "Cannot fit GPU1" }));
        await repository.SaveCheckpointAsync(parent.Snapshot.RunId, new(
            [new("system", "Stable parent"), new("user", "Task"), new("assistant", ToolCalls: [spawn])],
            1, 1, 0, 0, ActiveToolCalls: [spawn], SelectedModelId: ModelId, UseStableSubagentToolCatalog: true));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var processing = provider.GetRequiredService<RunProcessor>().ProcessAsync(parent.Snapshot.RunId, timeout.Token);
        await handler.ParentStarted.Task.WaitAsync(timeout.Token);
        Assert.True((await repository.GetCheckpointAsync(parent.Snapshot.RunId))!.UseStableSubagentToolCatalog);
        handler.ContinueParent.TrySetResult();
        await processing;

        Assert.Contains(ModelRuntimeClient.ToTransportToolName(SubagentToolNames.Spawn), handler.ParentToolNames);
        Assert.Contains(ModelRuntimeClient.ToTransportToolName(SubagentToolNames.Wait), handler.ParentToolNames);
        Assert.Empty(await repository.GetSubagentRunsAsync(parent.Snapshot.RunId));
        Assert.Equal(0, handler.PrepareCalls);
        Assert.Contains(await repository.GetEventsAfterAsync(parent.Snapshot.RunId, 0), item => item.Type == RunEventTypes.ServerToolCompleted
            && item.Data.TryGetProperty("errorCode", out var code) && code.GetString() == "subagent.insufficient_vram");
        Assert.Null(await repository.GetCheckpointAsync(parent.Snapshot.RunId));
    }

    private sealed class DualAgentHandler(bool denySubagent = false, bool holdChild = false, bool initiallyUnloaded = false,
        bool childImmediatelyComplete = false, bool emitInitialSpawn = false, bool pauseParent = false) : HttpMessageHandler
    {
        private static readonly string[] ParentTags = ["missum-context-train:32768"];
        private static readonly string[] ChildTags = ["missum-context-train:32768", "missum-agent-instance:subagent", "missum-base-model:" + ModelId];
        private readonly TaskCompletionSource _childStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _parentTurns;
        private int _childTurns;
        private bool _primaryLoaded = !initiallyUnloaded;
        public bool ParentWorkedWhileChildActive { get; private set; }
        public bool ParentUsedChildResult { get; private set; }
        public TaskCompletionSource ChildStarted => _childStarted;
        public TaskCompletionSource ParentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ContinueParent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PrepareCalls { get; private set; }
        public int ChildTurns => Volatile.Read(ref _childTurns);
        public int ParentTurns => Volatile.Read(ref _parentTurns);
        public string[] ParentToolNames { get; private set; } = [];
        public List<string> ParentSchemas { get; } = [];
        public List<string> ChildSchemas { get; } = [];
        public string? FirstParentSystemPolicy { get; private set; }
        public string? FirstChildSystemPolicy { get; private set; }
        public bool DeniedBeforeLoad { get; private set; }
        public int MainLoads { get; private set; }
        public bool ChildInferenceCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[]
            {
                new { id = ModelId, tags = ParentTags, status = new { value = _primaryLoaded ? "loaded" : "unloaded" } },
                new { id = ModelId + "@subagent", tags = ChildTags, status = new { value = initiallyUnloaded ? "unloaded" : "loaded" } },
            } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = 32768 } });
            if (path == "/release") return Json(new { success = true });
            if (path == "/models/load")
            {
                // Loaded-primary companion reconciliation is idempotent; it
                // must not count as another physical primary load.
                if (!_primaryLoaded) MainLoads++;
                _primaryLoaded = true;
                return Json(new { success = true });
            }
            if (path is "/agents/status" or "/agents/prepare")
            {
                if (path == "/agents/prepare") PrepareCalls++;
                if (!_primaryLoaded) DeniedBeforeLoad = true;
                return Json(new { allowed = !denySubagent && _primaryLoaded, reason = !_primaryLoaded ? "subagent.primary_not_loaded" : denySubagent ? "subagent.insufficient_vram" : null, modelId = ModelId,
                    instanceId = ModelId + "@subagent", gpuIndex = 1, contextLength = 32768, cacheStatus = "forked", cachedTokens = 10 });
            }
            if (path is "/sessions/prepare" or "/sessions/save") return Json(new { success = true });
            if (path == "/v1/chat/completions/input_tokens") return Json(new { input_tokens = 100 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + request.RequestUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var isChild = root.GetProperty("model").GetString() == ModelId + "@subagent";
            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            var schemas = root.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "[]";
            if (isChild)
            {
                _childStarted.TrySetResult();
                var turn = Interlocked.Increment(ref _childTurns);
                ChildSchemas.Add(schemas);
                if (turn == 1)
                    FirstChildSystemPolicy = messages.First(static message => message.GetProperty("role").GetString() == "system").GetProperty("content").GetString();
                if (holdChild)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        ChildInferenceCancelled = true;
                        throw;
                    }
                }
                if (childImmediatelyComplete) return Text("Die eigene Aufgabe ist abgeschlossen.");
                return turn switch
                {
                    1 => Tool(ClientToolNames.CodingRead, new { path = "child.txt" }),
                    _ => Text("Das delegierte Dateiergebnis lautet CHILD_MARKER_731."),
                };
            }
            var parentTurn = Interlocked.Increment(ref _parentTurns);
            ParentSchemas.Add(schemas);
            ParentToolNames = tools.ValueKind == JsonValueKind.Array
                ? tools.EnumerateArray().Select(static item => item.GetProperty("function").GetProperty("name").GetString()!).ToArray() : [];
            if (parentTurn == 1)
                FirstParentSystemPolicy = messages.First(static message => message.GetProperty("role").GetString() == "system").GetProperty("content").GetString();
            ParentStarted.TrySetResult();
            if (pauseParent) await ContinueParent.Task.WaitAsync(cancellationToken);
            if (denySubagent || holdChild || initiallyUnloaded) return Text("Der Hauptagent hat seine verfügbare Arbeit abgeschlossen.");
            if (emitInitialSpawn && parentTurn == 1)
                return Tool(SubagentToolNames.Spawn, new { task = "Read child.txt and report its marker." });
            if (parentTurn == (emitInitialSpawn ? 2 : 1))
            {
                await _childStarted.Task.WaitAsync(cancellationToken);
                ParentWorkedWhileChildActive = true;
                var receipt = messages.Where(static item => item.GetProperty("role").GetString() == "tool")
                    .Select(static item => item.GetProperty("content").GetString()).First(static text => text?.Contains("\"status\":\"started\"", StringComparison.Ordinal) == true);
                using var receiptJson = JsonDocument.Parse(receipt!);
                return Tool(SubagentToolNames.Wait, new { runId = receiptJson.RootElement.GetProperty("runId").GetString() });
            }
            ParentUsedChildResult = messages.Any(static message => message.GetProperty("content").ValueKind == JsonValueKind.String
                && message.GetProperty("content").GetString()?.Contains("CHILD_MARKER_731", StringComparison.Ordinal) == true);
            return Text("Ich habe das delegierte Ergebnis CHILD_MARKER_731 direkt verwendet.");
        }

        private static HttpResponseMessage Tool(string name, object arguments) => Sse(new { tool_calls = new[]
        {
            new { index = 0, id = Guid.NewGuid().ToString("N"), type = "function", function = new
            { name = ModelRuntimeClient.ToTransportToolName(name), arguments = JsonSerializer.Serialize(arguments) } },
        } }, "tool_calls");
        private static HttpResponseMessage Text(string content) => Sse(new { content }, "stop");
        private static HttpResponseMessage Sse(object delta, string finish) => new(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } })
                + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

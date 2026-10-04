using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class SubagentContinuationStateTests
{
    [Theory]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Interrupted)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Completed)]
    public async Task RepeatedContinuationRetainsAnUnresumedChildWithItsRealOriginalParent(RunState childState)
    {
        using var context = new TestServerContext();
        using var provider = Services(context);
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var original = await repository.CreateAsync(ParentRequest(), null);
        var childRequest = ChildRequest(original.Snapshot.RunId);
        var child = await repository.CreateSubagentAsync(childRequest, Checkpoint(), "original-child");
        await repository.UpdateStateAsync(child.RunId, childState);
        await repository.UpdateStateAsync(original.Snapshot.RunId, RunState.Cancelled);
        var firstContinuation = await repository.CreateAsync(ParentRequest(), null);
        var firstMessages = ContinuationMessages();
        await processor.AppendSubagentContinuationStateAsync(firstContinuation.Snapshot.RunId,
            original.Snapshot.RunId, ParentRequest(), firstMessages, CancellationToken.None);
        Assert.NotNull(await repository.GetAuthorizedSubagentContinuationAsync(firstContinuation.Snapshot.RunId, child.RunId));
        await repository.UpdateStateAsync(firstContinuation.Snapshot.RunId, RunState.Cancelled);
        var nextContinuation = await repository.CreateAsync(ParentRequest(), null);
        var nextMessages = ContinuationMessages();

        await processor.AppendSubagentContinuationStateAsync(nextContinuation.Snapshot.RunId,
            firstContinuation.Snapshot.RunId, ParentRequest(), nextMessages, CancellationToken.None);

        var authorized = await repository.GetAuthorizedSubagentContinuationAsync(nextContinuation.Snapshot.RunId, child.RunId);
        Assert.NotNull(authorized);
        Assert.Equal(childState, authorized.GetValueOrDefault().Snapshot.State);
        Assert.Equal(original.Snapshot.RunId, authorized.GetValueOrDefault().Request.Subagent!.ParentRunId);
        var adoption = Assert.Single(await repository.GetEventsAfterAsync(nextContinuation.Snapshot.RunId, 0),
            item => item.Type == "subagent.continuationState");
        Assert.Equal(original.Snapshot.RunId, adoption.Data.GetProperty("sourceParentRunId").GetString());
        Assert.Equal(firstContinuation.Snapshot.RunId, adoption.Data.GetProperty("adoptedFromParentRunId").GetString());
        var listed = Assert.Single(adoption.Data.GetProperty("children").EnumerateArray());
        Assert.Equal(child.RunId, listed.GetProperty("runId").GetString());
        Assert.Equal(childRequest.Subagent!.AgentId, listed.GetProperty("agentId").GetString());
        Assert.Equal(childRequest.Subagent.AssignedTask, listed.GetProperty("task").GetString());
        Assert.Equal(childState.ToString().ToLowerInvariant(), listed.GetProperty("state").GetString());
        Assert.False(listed.GetProperty("isRunning").GetBoolean());
        Assert.Equal(childState != RunState.Completed, listed.GetProperty("canResume").GetBoolean());
        Assert.False(listed.GetProperty("resultConsumed").GetBoolean());
        Assert.Single(nextMessages, message => message.Content?.StartsWith("[MISSUM_SUBAGENT_CONTINUATION:", StringComparison.Ordinal) == true);
        Assert.Equal("Fortsetzen", nextMessages[^1].Content);
        Assert.Equal(childState, (await repository.GetAsync(child.RunId))!.State);
        Assert.Empty(await repository.GetSubagentRunsAsync(firstContinuation.Snapshot.RunId));
        Assert.Empty(await repository.GetSubagentRunsAsync(nextContinuation.Snapshot.RunId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedContinuationDoesNotListAnAlreadyConsumedCompletedChild(bool consumedByFirstContinuation)
    {
        using var context = new TestServerContext();
        using var provider = Services(context);
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var original = await repository.CreateAsync(ParentRequest(), null);
        var child = await repository.CreateSubagentAsync(ChildRequest(original.Snapshot.RunId), Checkpoint(), "completed-child");
        await repository.UpdateStateAsync(child.RunId, RunState.Completed);
        if (!consumedByFirstContinuation)
            await ConsumeCompletedChildAsync(repository, original.Snapshot.RunId, child.RunId);
        await repository.UpdateStateAsync(original.Snapshot.RunId, RunState.Cancelled);
        var firstContinuation = await repository.CreateAsync(ParentRequest(), null);
        await processor.AppendSubagentContinuationStateAsync(firstContinuation.Snapshot.RunId,
            original.Snapshot.RunId, ParentRequest(), ContinuationMessages(), CancellationToken.None);
        if (consumedByFirstContinuation)
            await ConsumeCompletedChildAsync(repository, firstContinuation.Snapshot.RunId, child.RunId);
        await repository.UpdateStateAsync(firstContinuation.Snapshot.RunId, RunState.Cancelled);
        var nextContinuation = await repository.CreateAsync(ParentRequest(), null);
        var messages = ContinuationMessages();

        await processor.AppendSubagentContinuationStateAsync(nextContinuation.Snapshot.RunId,
            firstContinuation.Snapshot.RunId, ParentRequest(), messages, CancellationToken.None);

        Assert.DoesNotContain(await repository.GetEventsAfterAsync(nextContinuation.Snapshot.RunId, 0),
            item => item.Type == "subagent.continuationState"
                && item.Data.GetProperty("children").EnumerateArray().Any(listed => listed.GetProperty("runId").GetString() == child.RunId));
        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(nextContinuation.Snapshot.RunId, child.RunId));
        Assert.DoesNotContain(messages, message => message.Content?.Contains(child.RunId, StringComparison.Ordinal) == true);
        Assert.Equal(RunState.Completed, (await repository.GetAsync(child.RunId))!.State);
    }

    [Theory]
    [InlineData(RunState.Cancelled, false)]
    [InlineData(RunState.Interrupted, false)]
    [InlineData(RunState.Completed, false)]
    [InlineData(RunState.Completed, true)]
    public async Task NewerDirectChildAttemptSupersedesAnAdoptedOlderAttemptOfTheSameAgent(RunState latestState, bool consumed)
    {
        using var context = new TestServerContext();
        using var provider = Services(context);
        var repository = provider.GetRequiredService<RunRepository>();
        var processor = provider.GetRequiredService<RunProcessor>();
        var original = await repository.CreateAsync(ParentRequest(), null);
        var childRequest = ChildRequest(original.Snapshot.RunId);
        var previous = await repository.CreateSubagentAsync(childRequest, Checkpoint(), "previous-attempt");
        await repository.UpdateStateAsync(previous.RunId, RunState.Cancelled);
        await repository.UpdateStateAsync(original.Snapshot.RunId, RunState.Cancelled);
        var firstContinuation = await repository.CreateAsync(ParentRequest(), null);
        await processor.AppendSubagentContinuationStateAsync(firstContinuation.Snapshot.RunId,
            original.Snapshot.RunId, ParentRequest(), ContinuationMessages(), CancellationToken.None);
        var latestRequest = childRequest with
        {
            Subagent = childRequest.Subagent! with { ParentRunId = firstContinuation.Snapshot.RunId },
        };
        var latest = await repository.CreateSubagentAsync(latestRequest, Checkpoint(), "new-direct-attempt");
        await repository.UpdateStateAsync(latest.RunId, latestState);
        if (consumed)
            await ConsumeCompletedChildAsync(repository, firstContinuation.Snapshot.RunId, latest.RunId);
        await repository.UpdateStateAsync(firstContinuation.Snapshot.RunId, RunState.Cancelled);
        var nextContinuation = await repository.CreateAsync(ParentRequest(), null);
        var messages = ContinuationMessages();

        await processor.AppendSubagentContinuationStateAsync(nextContinuation.Snapshot.RunId,
            firstContinuation.Snapshot.RunId, ParentRequest(), messages, CancellationToken.None);

        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(nextContinuation.Snapshot.RunId, previous.RunId));
        if (consumed)
        {
            Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(nextContinuation.Snapshot.RunId, latest.RunId));
            Assert.DoesNotContain(await repository.GetEventsAfterAsync(nextContinuation.Snapshot.RunId, 0),
                item => item.Type == "subagent.continuationState");
            Assert.DoesNotContain(messages, message => message.Content?.Contains(previous.RunId, StringComparison.Ordinal) == true);
            Assert.Equal(RunState.Cancelled, (await repository.GetAsync(previous.RunId))!.State);
            return;
        }
        Assert.NotNull(await repository.GetAuthorizedSubagentContinuationAsync(nextContinuation.Snapshot.RunId, latest.RunId));
        var adoption = Assert.Single(await repository.GetEventsAfterAsync(nextContinuation.Snapshot.RunId, 0),
            item => item.Type == "subagent.continuationState");
        Assert.Equal(firstContinuation.Snapshot.RunId, adoption.Data.GetProperty("sourceParentRunId").GetString());
        var listed = Assert.Single(adoption.Data.GetProperty("children").EnumerateArray());
        Assert.Equal(latest.RunId, listed.GetProperty("runId").GetString());
        Assert.Equal(childRequest.Subagent!.AgentId, listed.GetProperty("agentId").GetString());
        Assert.DoesNotContain(messages, message => message.Content?.Contains(previous.RunId, StringComparison.Ordinal) == true);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(previous.RunId))!.State);
    }

    [Theory]
    [InlineData(RunState.Queued, RunState.Cancelled)]
    [InlineData(RunState.Running, RunState.Interrupted)]
    [InlineData(RunState.WaitingForClient, RunState.Failed)]
    [InlineData(RunState.Running, RunState.Completed)]
    public async Task ActiveParentCanReadItsOwnTerminalChildState(RunState parentState, RunState childState)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parent = await repository.CreateAsync(ParentRequest(), null);
        await repository.UpdateStateAsync(parent.Snapshot.RunId, parentState);
        var childRequest = ChildRequest(parent.Snapshot.RunId);
        var child = await repository.CreateSubagentAsync(childRequest, Checkpoint(), "initial-child");
        await repository.UpdateStateAsync(child.RunId, childState);

        var authorized = await repository.GetAuthorizedSubagentContinuationAsync(parent.Snapshot.RunId, child.RunId);

        Assert.NotNull(authorized);
        var actual = authorized.GetValueOrDefault();
        Assert.Equal(child.RunId, actual.Snapshot.RunId);
        Assert.Equal(childState, actual.Snapshot.State);
        Assert.Equal(childRequest.Subagent, actual.Request.Subagent);
        Assert.Equal(childRequest.SessionId, actual.Request.SessionId);
    }

    [Fact]
    public async Task EarlierChildRequiresActualGatewayAdoptionEventForItsExactSourceParent()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var previous = await repository.CreateAsync(ParentRequest(), null);
        var child = await repository.CreateSubagentAsync(ChildRequest(previous.Snapshot.RunId), Checkpoint(), "old-child");
        await repository.UpdateStateAsync(child.RunId, RunState.Cancelled);
        await repository.UpdateStateAsync(previous.Snapshot.RunId, RunState.Cancelled);
        var current = await repository.CreateAsync(ParentRequest() with
        {
            Messages = [new("user", [new("text", Text: "Resume child " + child.RunId)])],
        }, null);

        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId));
        await repository.AppendEventAsync(current.Snapshot.RunId, "untrusted.modelText", new
        {
            sourceParentRunId = previous.Snapshot.RunId,
            children = new[] { new { runId = child.RunId } },
        });
        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId));
        await AdoptAsync(repository, current.Snapshot.RunId, "unrelated-parent", child.RunId);
        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId));
        await AdoptAsync(repository, current.Snapshot.RunId, previous.Snapshot.RunId, "unrelated-child");
        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId));

        await AdoptAsync(repository, current.Snapshot.RunId, previous.Snapshot.RunId, child.RunId);

        var authorized = await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId);
        Assert.NotNull(authorized);
        var actual = authorized.GetValueOrDefault();
        Assert.Equal(RunState.Cancelled, actual.Snapshot.State);
        Assert.Equal(previous.Snapshot.RunId, actual.Request.Subagent!.ParentRunId);
    }

    [Theory]
    [InlineData("foreign-session")]
    [InlineData("foreign-workspace")]
    [InlineData("foreign-mode")]
    [InlineData("nested-parent")]
    public async Task AdoptionEventCannotBypassSessionWorkspaceModeOrParentOwnership(string mismatch)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var previous = await repository.CreateAsync(ParentRequest(), null);
        var child = await repository.CreateSubagentAsync(ChildRequest(previous.Snapshot.RunId), Checkpoint(), "old-child");
        await repository.UpdateStateAsync(child.RunId, RunState.Cancelled);
        var request = mismatch switch
        {
            "foreign-session" => ParentRequest() with { SessionId = "other-parent-session" },
            "foreign-workspace" => ParentRequest() with { WorkspacePath = "C:\\other-workspace" },
            "foreign-mode" => ParentRequest() with { Mode = RunMode.Coding },
            "nested-parent" => ChildRequest(previous.Snapshot.RunId),
            _ => throw new ArgumentException("Unexpected fixture mismatch.", nameof(mismatch)),
        };
        var current = await repository.CreateAsync(request, null);
        await AdoptAsync(repository, current.Snapshot.RunId, previous.Snapshot.RunId, child.RunId);

        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CodingContinuationUsesItsActualCodingWorkspaceAndNormalizesEquivalentPaths(bool sameWorkspace)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parentRequest = ParentRequest() with
        {
            Mode = RunMode.Coding,
            WorkspacePath = "C:\\ignored-general-workspace",
            CodingOptions = new(WorkspacePath: "C:\\science-workspace"),
        };
        var previous = await repository.CreateAsync(parentRequest, null);
        var childRequest = ChildRequest(previous.Snapshot.RunId) with
        {
            Mode = RunMode.Coding,
            WorkspacePath = parentRequest.WorkspacePath,
            CodingOptions = parentRequest.CodingOptions,
        };
        var child = await repository.CreateSubagentAsync(childRequest, Checkpoint(), "coding-child");
        await repository.UpdateStateAsync(child.RunId, RunState.Cancelled);
        var current = await repository.CreateAsync(parentRequest with
        {
            CodingOptions = new(WorkspacePath: sameWorkspace ? "c:/SCIENCE-workspace/" : "C:\\other-coding-workspace"),
        }, null);
        await AdoptAsync(repository, current.Snapshot.RunId, previous.Snapshot.RunId, child.RunId);

        var authorized = await repository.GetAuthorizedSubagentContinuationAsync(current.Snapshot.RunId, child.RunId);

        Assert.Equal(sameWorkspace, authorized.HasValue);
    }

    [Theory]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Interrupted)]
    [InlineData(RunState.Failed)]
    public async Task TerminalParentCannotAuthorizeAChildContinuation(RunState parentState)
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var parent = await repository.CreateAsync(ParentRequest(), null);
        var child = await repository.CreateSubagentAsync(ChildRequest(parent.Snapshot.RunId), Checkpoint(), "child");
        await repository.UpdateStateAsync(child.RunId, RunState.Cancelled);
        await repository.UpdateStateAsync(parent.Snapshot.RunId, parentState);

        Assert.Null(await repository.GetAuthorizedSubagentContinuationAsync(parent.Snapshot.RunId, child.RunId));
    }

    [Fact]
    public async Task ResumeOperationIsIdempotentForNewParentAndPreservesCancelledSourceAndAgentIdentity()
    {
        using var context = new TestServerContext();
        var repository = new RunRepository(context.Database, new RunEventNotifier());
        var originalParent = await repository.CreateAsync(ParentRequest(), null);
        var sourceRequest = ChildRequest(originalParent.Snapshot.RunId);
        var source = await repository.CreateSubagentAsync(sourceRequest, Checkpoint(), "original-child");
        await repository.UpdateStateAsync(source.RunId, RunState.Cancelled);
        var currentParent = await repository.CreateAsync(ParentRequest(), null);
        await AdoptAsync(repository, currentParent.Snapshot.RunId, originalParent.Snapshot.RunId, source.RunId);
        var resumedRequest = sourceRequest with
        {
            Subagent = sourceRequest.Subagent! with { ParentRunId = currentParent.Snapshot.RunId },
        };
        var checkpoint = Checkpoint() with
        {
            Messages = [new("system", "Saved child policy"), new("user", "Original independent task"),
                new("assistant", "Preserved completed derivation"), new("user", "Resume remaining work")],
        };
        var operation = "resume:" + currentParent.Snapshot.RunId + ":" + source.RunId;

        var resumed = await repository.CreateSubagentAsync(resumedRequest, checkpoint, operation);
        var duplicate = await repository.CreateSubagentAsync(resumedRequest,
            checkpoint with { Messages = [new("user", "Must never overwrite the stored continuation")] }, operation);

        Assert.Equal(resumed.RunId, duplicate.RunId);
        Assert.NotEqual(source.RunId, resumed.RunId);
        Assert.Equal(RunState.Queued, resumed.State);
        var stored = await repository.GetRequestAsync(resumed.RunId);
        Assert.NotNull(stored);
        Assert.Equal(sourceRequest.Subagent!.AgentId, stored.Subagent!.AgentId);
        Assert.Equal(currentParent.Snapshot.RunId, stored.Subagent.ParentRunId);
        Assert.Equal(sourceRequest.Subagent.AssignedTask, stored.Subagent.AssignedTask);
        Assert.Equal(sourceRequest.SessionId, stored.SessionId);
        Assert.Equal(checkpoint.Messages, (await repository.GetCheckpointAsync(resumed.RunId))!.Messages);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(source.RunId))!.State);
        Assert.Equal(sourceRequest.Subagent, (await repository.GetRequestAsync(source.RunId))!.Subagent);
        Assert.Single(await repository.GetSubagentRunsAsync(currentParent.Snapshot.RunId));

        var nextParent = await repository.CreateAsync(ParentRequest(), null);
        var nextRequest = resumedRequest with
        {
            Subagent = resumedRequest.Subagent! with { ParentRunId = nextParent.Snapshot.RunId },
        };
        var nextAttempt = await repository.CreateSubagentAsync(nextRequest, checkpoint,
            "resume:" + nextParent.Snapshot.RunId + ":" + source.RunId);
        Assert.NotEqual(resumed.RunId, nextAttempt.RunId);
        Assert.Equal(sourceRequest.Subagent.AgentId, (await repository.GetRequestAsync(nextAttempt.RunId))!.Subagent!.AgentId);
        Assert.Equal(RunState.Cancelled, (await repository.GetAsync(source.RunId))!.State);
    }

    [Fact]
    public void InterruptedCallsPreserveExactPrefixAndCompletedReceiptsWithoutReplayingUnknownMutation()
    {
        var completed = Call("read-completed", ClientToolNames.CodingRead, "known.txt");
        var mutation = Call("write-pending", ClientToolNames.CodingWrite, "result.txt");
        IReadOnlyList<LmChatMessage> source = [new("system", "Exact child system prefix"), new("user", "Original task"),
            new("assistant", "Read first", ToolCalls: [completed], ReasoningContent: "Recorded reasoning"),
            new("tool", "{\"status\":\"completed\",\"content\":\"known result\"}", ToolCallId: completed.Id),
            new("assistant", "Write once", ToolCalls: [mutation])];

        var closed = RunProcessor.CloseInterruptedSubagentToolCalls(source, new Dictionary<string, string>());

        Assert.Equal(source, closed.Take(source.Count));
        Assert.Equal(source.Count + 1, closed.Count);
        Assert.Single(closed.SelectMany(message => message.ToolCalls ?? []), call => call.Name == ClientToolNames.CodingWrite);
        Assert.Equal(mutation.Id, closed[^1].ToolCallId);
        using var unknown = JsonDocument.Parse(closed[^1].Content!);
        Assert.Equal("interrupted", unknown.RootElement.GetProperty("status").GetString());
        Assert.True(unknown.RootElement.GetProperty("outcomeUnknown").GetBoolean());
        Assert.Contains("nicht automatisch wiederholen", unknown.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(5, source.Count);
    }

    [Fact]
    public void OutstandingCallUsesItsRecoveredExactDurableReceipt()
    {
        var call = Call("pending", ClientToolNames.CodingWrite, "proof.txt");
        IReadOnlyList<LmChatMessage> source = [new("system", "Original"), new("assistant", ToolCalls: [call])];
        const string receipt = "{\"status\":\"completed\",\"sha256\":\"actual-owned-receipt\",\"content\":\"saved\"}";

        var closed = RunProcessor.CloseInterruptedSubagentToolCalls(source, new Dictionary<string, string>
        {
            [call.Id] = receipt,
        });

        Assert.Equal(source, closed.Take(source.Count));
        Assert.Equal(receipt, closed[^1].Content);
        Assert.Equal(call.Id, closed[^1].ToolCallId);
        Assert.Equal("tool", closed[^1].Role);
        Assert.Equal(3, closed.Count);
    }

    [Fact]
    public void ReusedProviderIdDoesNotTreatAnEarlierCompletedReceiptAsTheCurrentCallResult()
    {
        var previous = Call("reused-id", ClientToolNames.CodingRead, "old.txt");
        var current = Call("reused-id", ClientToolNames.CodingWrite, "new.txt");
        const string previousReceipt = "Previously completed read";
        IReadOnlyList<LmChatMessage> source = [new("system", "Original"), new("assistant", ToolCalls: [previous]),
            new("tool", previousReceipt, ToolCallId: previous.Id), new("assistant", ToolCalls: [current])];

        var closed = RunProcessor.CloseInterruptedSubagentToolCalls(source, new Dictionary<string, string>());

        Assert.Equal(source, closed.Take(source.Count));
        Assert.Equal(previousReceipt, closed[2].Content);
        Assert.Equal(current.Id, closed[^1].ToolCallId);
        Assert.NotEqual(previousReceipt, closed[^1].Content);
        Assert.Contains("\"outcomeUnknown\":true", closed[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredLatestReceiptIsNotAppliedToAnOlderUnclosedCallWithTheSameProviderId()
    {
        var older = Call("reused-id", ClientToolNames.CodingWrite, "old.txt");
        var current = Call("reused-id", ClientToolNames.CodingWrite, "new.txt");
        const string receipt = "{\"status\":\"completed\",\"path\":\"new.txt\"}";
        IReadOnlyList<LmChatMessage> source = [new("system", "Original"), new("assistant", ToolCalls: [older]),
            new("assistant", ToolCalls: [current])];

        var closed = RunProcessor.CloseInterruptedSubagentToolCalls(source, new Dictionary<string, string>
        {
            [current.Id] = receipt,
        });

        Assert.Equal(source[0], closed[0]);
        Assert.Equal(source[1], closed[1]);
        Assert.Contains("\"outcomeUnknown\":true", closed[2].Content, StringComparison.Ordinal);
        Assert.Equal(source[2], closed[3]);
        Assert.Equal(receipt, closed[4].Content);
        Assert.Equal(current.Id, closed[4].ToolCallId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(128)]
    public void SpawnResumeRunIdAcceptsDeclaredLengthBoundaries(int length)
    {
        var catalog = new AgentToolCatalog();
        var spawn = catalog.Resolve(SubagentToolNames.Spawn, catalog.GetAvailableTools(ParentRequest(), subagentAvailable: true));

        catalog.Validate(spawn, JsonSerializer.SerializeToElement(new { task = "Original task", resumeRunId = new string('r', length) }));
        Assert.False(spawn.Schema.GetProperty("additionalProperties").GetBoolean());
        var property = spawn.Schema.GetProperty("properties").GetProperty("resumeRunId");
        Assert.Equal("string", property.GetProperty("type").GetString());
        Assert.Equal(1, property.GetProperty("minLength").GetInt32());
        Assert.Equal(128, property.GetProperty("maxLength").GetInt32());
        Assert.DoesNotContain(spawn.Schema.GetProperty("required").EnumerateArray(), item => item.GetString() == "resumeRunId");
    }

    [Fact]
    public void SpawnResumeRunIdIsOptionalAndUnknownOwnershipFieldsRemainForbidden()
    {
        var catalog = new AgentToolCatalog();
        var spawn = catalog.Resolve(SubagentToolNames.Spawn, catalog.GetAvailableTools(ParentRequest(), subagentAvailable: true));
        catalog.Validate(spawn, JsonSerializer.SerializeToElement(new { task = "Fresh task" }));

        Assert.Throws<ArgumentException>(() => catalog.Validate(spawn,
            JsonSerializer.SerializeToElement(new { task = "Original task", resumeRunId = "run-old", parentRunId = "forged-parent" })));
    }

    [Theory]
    [InlineData("{\"task\":\"Task\",\"resumeRunId\":\"\"}")]
    [InlineData("{\"task\":\"Task\",\"resumeRunId\":123}")]
    [InlineData("{\"task\":\"Task\",\"resumeRunId\":null}")]
    public void SpawnResumeRunIdRejectsInvalidTypeOrEmptyValue(string json)
    {
        var catalog = new AgentToolCatalog();
        var spawn = catalog.Resolve(SubagentToolNames.Spawn, catalog.GetAvailableTools(ParentRequest(), subagentAvailable: true));
        using var document = JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(() => catalog.Validate(spawn, document.RootElement));
    }

    [Fact]
    public void SpawnResumeRunIdRejectsLengthAboveSchemaMaximum()
    {
        var catalog = new AgentToolCatalog();
        var spawn = catalog.Resolve(SubagentToolNames.Spawn, catalog.GetAvailableTools(ParentRequest(), subagentAvailable: true));

        Assert.Throws<ArgumentException>(() => catalog.Validate(spawn,
            JsonSerializer.SerializeToElement(new { task = "Task", resumeRunId = new string('r', 129) })));
    }

    private static async Task AdoptAsync(RunRepository repository, string parent, string sourceParent, string child)
    {
        _ = await repository.AppendEventAsync(parent, "subagent.continuationState", new
        {
            sourceParentRunId = sourceParent,
            children = new[] { new { runId = child } },
        });
    }

    private static ServiceProvider Services(TestServerContext context)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMissumAiServerServices(context.Options, includeHostedServices: false);
        services.AddSingleton(context.Database);
        return services.BuildServiceProvider();
    }

    private static List<LmChatMessage> ContinuationMessages() => [new("system", "Original system"), new("user", "Fortsetzen")];

    private static async Task ConsumeCompletedChildAsync(RunRepository repository, string parent, string child)
    {
        var receipt = JsonSerializer.Serialize(new { status = "completed", runId = child, result = "Already delivered result" });
        await repository.SaveCheckpointAsync(parent, new([new("system", "Original system"), new("tool", receipt, ToolCallId: "wait-child")],
            1, 1, 100, 10));
        Assert.True(await repository.TryConsumeSubagentResultAsync(parent, child));
        Assert.True(await repository.HasConsumedSubagentResultAsync(parent, child));
    }

    private static RunRequest ParentRequest() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Investigate the scientific task")])],
        ClientCapabilities: ["workspace", "coding", "subagents"], AllowedServerTools: [],
        SessionId: "parent-session", WorkspacePath: "C:\\science-workspace");

    private static RunRequest ChildRequest(string parentRunId) => ParentRequest() with
    {
        SessionId = "stable-child-session",
        Subagent = new(parentRunId, "stable-planet-agent", "Independent derivation with preserved results", "parent-session"),
    };

    private static AgentRunCheckpoint Checkpoint() => new([new("system", "Saved child policy"), new("user", "Original task")],
        2, 3, 100, 50, PreserveSessionPromptPrefix: true);

    private static LmToolCall Call(string id, string name, string path) =>
        new(id, name, JsonSerializer.SerializeToElement(new { path }));
}

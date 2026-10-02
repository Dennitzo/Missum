using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Data;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Missum.Ai.Server.Core.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class CodingRunBudgetTests
{
    private const string ModelId = "coding/ProjectFixture-Q4~abc123";

    [Fact]
    public void DefaultsAreUnlimitedAndExplicitFiniteBudgetKeepsReservedSummary()
    {
        var options = new MissumAiServerOptions();
        var budget = CodingRunBudget.FromOptions(options);
        Assert.Equal(0, budget.ModelRounds);
        Assert.Equal(0, budget.ToolCalls);
        Assert.False(budget.ShouldWarn(long.MaxValue, long.MaxValue));
        Assert.False(budget.MustSummarize(long.MaxValue, long.MaxValue));
        Assert.Contains("unbegrenzte", budget.Instruction(10000, 10000));
        budget = new CodingRunBudget(96, 96);
        Assert.False(budget.ShouldWarn(87, 87));
        Assert.True(budget.ShouldWarn(88, 1));
        Assert.True(budget.ShouldWarn(1, 88));
        Assert.False(budget.MustSummarize(94, 95));
        Assert.True(budget.MustSummarize(95, 1));
        Assert.True(budget.MustSummarize(1, 96));
        options.CodingMaximumModelRounds = 500;
        options.CodingMaximumToolCalls = 500;
        Assert.Equal(500, CodingRunBudget.FromOptions(options).ToolCalls);
        Assert.Equal(500, CodingRunBudget.FromOptions(options).ModelRounds);
    }

    [Fact]
    public void UnlimitedBudgetRemovesRecoveredMarkersWithoutCreatingAnArtificialUserRequest()
    {
        var read = new LmToolCall("read", "coding.read", JsonSerializer.SerializeToElement(new { path = "source.py" }));
        var messages = new List<LmChatMessage>
        {
            new("system", "Stable coding policy: continue until verified completion."),
            new("system", CodingRunBudget.PromptMarker + "\nold compacted budget"),
            new("user", "Fix the slow project startup."),
            new("assistant", "I inspect the startup code.", ToolCalls: [read]),
            new("tool", "Current source", ToolCallId: "read"),
            new("system", CodingRunBudget.PromptMarker + "\nunlimited budget"),
        };
        new CodingRunBudget(0, 0).ApplyInstruction(messages, 100, 200);
        new CodingRunBudget(0, 0).ApplyInstruction(messages, 101, 203);
        var native = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages);

        Assert.Equal(4, native.Count);
        Assert.Equal("Fix the slow project startup.", Assert.Single(native, message => message.Role == "user").Content);
        Assert.Equal("tool", native[^1].Role);
        Assert.DoesNotContain(native, message => message.Content?.Contains(CodingRunBudget.PromptMarker, StringComparison.Ordinal) == true);
        Assert.Equal("read", native[^1].ToolCallId);
    }

    [Theory]
    [InlineData(96, 0)]
    [InlineData(0, 96)]
    [InlineData(96, 96)]
    public void AnExplicitFiniteLimitStillHasExactlyOneCurrentBudgetInstruction(int rounds, int calls)
    {
        var budget = new CodingRunBudget(rounds, calls);
        var messages = new List<LmChatMessage> { new("user", "Complete the task.") };
        budget.ApplyInstruction(messages, 10, 20);
        budget.ApplyInstruction(messages, 95, 96);
        var marker = Assert.Single(messages, message => message.Role == "system");
        Assert.Equal(budget.Instruction(95, 96), marker.Content);
        Assert.Contains("reservierte Abschluss", marker.Content);
    }

    [Fact]
    public async Task UnlimitedRunPassesOldLimitsAndPersistsRollingContextWithoutLosingCurrentRequest()
    {
        // Compaction is driven by real context pressure, not an arbitrary message count.
        using var harness = new Harness(readCalls: 150, readContentCharacters: 4_000);
        var runId = await harness.CreateRunAsync(timeoutSeconds: 0);

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(150, harness.ExecutedReads);
        Assert.True(harness.Handler.Compactions >= 2,
            $"Expected repeated model summaries; observed {harness.Handler.Compactions}, persisted {harness.HighestSavedCompaction}.");
        Assert.True(harness.HighestSavedCompaction >= 2,
            $"Expected repeated persisted summaries; observed {harness.HighestSavedCompaction}.");
        Assert.True(harness.Handler.LastMessages.Length < 128);
        Assert.DoesNotContain(harness.Handler.LastMessages,
            message => message.GetProperty("content").ValueKind == JsonValueKind.String
                && message.GetProperty("content").GetString()!.Contains(CodingRunBudget.PromptMarker, StringComparison.Ordinal));
        Assert.Contains(harness.Handler.LastMessages, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("content").GetString()!.Contains("Analysiere das Projekt und korrigiere den langsamen Programmstart.", StringComparison.Ordinal));
        Assert.Contains(harness.Handler.LastMessages, message => message.GetProperty("role").GetString() == "user"
            && message.GetProperty("content").GetString()!.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal)
            && message.GetProperty("content").GetString()!.Contains("Bisherige Dateien wurden geprüft.", StringComparison.Ordinal));
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Equal(150, events.Count(item => item.Type == RunEventTypes.ClientToolProposed));
        Assert.Contains(events, item => item.Type == RunEventTypes.ContextChanged && item.Data.GetProperty("wasCompacted").GetBoolean());
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunFailed);
    }

    [Fact]
    public async Task UnlimitedRunSupportsMultiDayResumeAnd64BitUsageCounters()
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(timeoutSeconds: 0);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        await harness.Repository.SaveCheckpointAsync(runId, new AgentRunCheckpoint(
            [new LmChatMessage("user", "Schließe mit dem bisherigen Ergebnis ab.")],
            3_000_000_000, 4_000_000_000, 5_000_000_000, 6_000_000_000));

        Assert.Null(await harness.DriveAsync(runId));

        var completed = Assert.Single(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
        Assert.Equal(5_000_000_016, completed.Data.GetProperty("inputTokens").GetInt64());
        Assert.Equal(6_000_000_004, completed.Data.GetProperty("outputTokens").GetInt64());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task OfflineCodingProposalHasNoExpiryAndResumesAfterDays(int? timeoutSeconds)
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync(timeoutSeconds);
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(runId, CancellationToken.None));
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        var proposal = (await harness.Repository.GetToolProposalAsync(checkpoint.PendingProposalId!, runId))!;
        Assert.Equal(DateTimeOffset.MaxValue, proposal.ExpiresAt);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow.AddDays(7)));
        await harness.Repository.SaveClientToolResultAsync(runId, new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { content = "saved" })));

        Assert.Null(await harness.DriveAsync(runId));
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(2, harness.Handler.ChatCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task SevenDayOldScienceRunStillHonorsCallerCancellation(int? legacyTimeoutSeconds)
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, RunMode.General, science: true);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Processor.ProcessAsync(runId, cancellation.Token));

        Assert.Equal(0, harness.Handler.Requests);
        Assert.Equal(0, harness.ExecutedReads);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
    }

    [Fact]
    public async Task ActiveScienceGenerationCanBeStoppedWithoutWaitingForAnyRunDeadline()
    {
        using var harness = new Harness(readCalls: 0);
        harness.Handler.WaitForCancellation = true;
        var runId = await harness.CreateRunAsync(3600, RunMode.General, science: true);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        using var cancellation = new CancellationTokenSource();
        var processing = harness.Processor.ProcessAsync(runId, cancellation.Token);
        await harness.Handler.GenerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotNull(await harness.Repository.GetCheckpointAsync(runId));
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task ScienceMainAgentPassesPreviousRoundAndToolLimitsAfterSevenDays(int? legacyTimeoutSeconds)
    {
        // This regression exercises run budgets, not exhaustion of a small model's context.
        using var harness = new Harness(readCalls: 70, contextTokens: 1_048_576);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, RunMode.General, science: true);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(70, harness.ExecutedReads);
        Assert.Equal(141, harness.Handler.ChatCalls);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Equal(70, events.Count(item => item.Type == RunEventTypes.ClientToolProposed));
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task OfflineScienceProposalRemainsWaitingAndResumesAfterDays(int? legacyTimeoutSeconds)
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, RunMode.General, science: true);
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(runId, CancellationToken.None));
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        var proposal = (await harness.Repository.GetToolProposalAsync(checkpoint.PendingProposalId!, runId))!;
        Assert.Equal(DateTimeOffset.MaxValue, proposal.ExpiresAt);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow.AddDays(7)));
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(runId))!.State);
        await harness.Repository.SaveClientToolResultAsync(runId, new ClientToolResult(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { content = "offline work saved" })));

        Assert.Null(await harness.DriveAsync(runId));
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(3, harness.Handler.ChatCalls);
        Assert.Single(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.ClientToolProposed);
    }

    [Fact]
    public async Task ThirtySuccessfulReadsSurviveSeparateClientContinuationsAndComplete()
    {
        using var harness = new Harness(readCalls: 30);
        var runId = await harness.CreateRunAsync();

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(30, harness.ExecutedReads);
        Assert.Equal(31, harness.Handler.ChatCalls);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(30, harness.Handler.LastMessages.Count(message => message.GetProperty("role").GetString() == "tool"));
        Assert.Contains(harness.Handler.LastMessages, message =>
            message.GetProperty("role").GetString() == "assistant"
            && message.TryGetProperty("content", out var content) && content.GetString() == "Ich prüfe die nächste Datei.");
        Assert.Null(await harness.Repository.GetCheckpointAsync(runId));
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunFailed);
    }

    [Fact]
    public async Task LastRoundProducesToolFreeInterimAnswerAndKeepsCheckpointWithoutFalseCompletion()
    {
        using var harness = new Harness(readCalls: 30, maximumRounds: 4);
        var runId = await harness.CreateRunAsync();

        var error = Assert.IsType<AgentRunLimitException>(await harness.DriveAsync(runId));

        Assert.Contains("Arbeitsbudget erreicht", error.Message);
        Assert.Contains("Fortsetzung", error.Message);
        Assert.Equal(3, harness.ExecutedReads);
        Assert.Equal(4, harness.Handler.ChatCalls);
        Assert.True(harness.Handler.LastTurnHadNoTools);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.Equal(4, checkpoint.RoundCount);
        Assert.Equal(3, checkpoint.Messages.Count(message => message.Role == "tool"));
        Assert.True(checkpoint.BudgetWarningIssued);
        Assert.Contains(checkpoint.Messages, message => message.Role == "assistant" && message.Content == Harness.InterimAnswer);
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunCompleted);
        var visibleText = string.Concat(events.Where(item => item.Type == RunEventTypes.TextDelta).Select(item => item.Data.GetProperty("delta").GetString()));
        Assert.Contains(Harness.InterimAnswer, visibleText);
        Assert.Equal(1, visibleText.Split("Das Arbeitsbudget dieses Laufs nähert sich dem Ende.", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task DuplicateQueueEntryAfterCompletionCannotRestartDeletedCheckpoint()
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync();
        Assert.Null(await harness.DriveAsync(runId));
        Assert.Null(await harness.Repository.GetCheckpointAsync(runId));
        var requests = harness.Handler.Requests;
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);

        await harness.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(requests, harness.Handler.Requests);
        Assert.Equal(1, harness.ExecutedReads);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(events.Count, (await harness.Repository.GetEventsAfterAsync(runId, 0)).Count);
        Assert.Null(await harness.Repository.GetCheckpointAsync(runId));
    }

    [Theory]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Interrupted)]
    public async Task TerminalQueueReplayPreservesPendingCheckpointWithoutExecutingIt(RunState state)
    {
        using var harness = new Harness(readCalls: 0, maximumRounds: 4);
        var runId = await harness.CreateRunAsync();
        var call = new LmToolCall("pending-mutation", ClientToolNames.CodingWrite,
            JsonSerializer.SerializeToElement(new { path = "src/new.cs", content = "must not be written", expectedSha256 = "missing" }));
        var checkpoint = new AgentRunCheckpoint([], 4, 1, 16, 4, ActiveToolCalls: [call]);
        await harness.Repository.SaveCheckpointAsync(runId, checkpoint);
        await harness.Repository.UpdateStateAsync(runId, state, errorCode: state == RunState.Failed ? "agent.run_limit" : null);

        await harness.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(0, harness.Handler.Requests);
        Assert.Equal(state, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Empty(await harness.Repository.GetEventsAfterAsync(runId, 0));
        var saved = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.Equal(4, saved.RoundCount);
        Assert.Equal("pending-mutation", Assert.Single(saved.ActiveToolCalls!).Id);
        Assert.Null(saved.PendingProposalId);
    }

    [Fact]
    public async Task AlreadyProducedReadAtPersistedRoundBoundaryIsExecutedAndConsumedBeforeLimit()
    {
        using var harness = new Harness(readCalls: 0, maximumRounds: 4);
        var runId = await harness.CreateRunAsync();
        var call = new LmToolCall("pending-read", ClientToolNames.CodingRead, JsonSerializer.SerializeToElement(new { path = "src/final.cs" }));
        await harness.Repository.SaveCheckpointAsync(runId, new AgentRunCheckpoint(
            [new LmChatMessage("user", "Prüfe das Projekt."), new LmChatMessage("assistant", null, ToolCalls: [call])],
            4, 1, 16, 4, ActiveToolCalls: [call]));

        Assert.IsType<AgentRunLimitException>(await harness.DriveAsync(runId));

        Assert.Equal(1, harness.ExecutedReads);
        Assert.Equal(0, harness.Handler.ChatCalls);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.Null(checkpoint.ActiveToolCalls);
        Assert.Null(checkpoint.PendingProposalId);
        var receipt = Assert.Single(checkpoint.Messages, message => message.Role == "tool");
        Assert.Equal("pending-read", receipt.ToolCallId);
        Assert.Contains("completed", receipt.Content);
    }

    [Fact]
    public async Task IndependentReadBatchUsesOneModelRoundAndFinishesEveryClientResult()
    {
        using var harness = new Harness(readCalls: 3, batchSize: 3);
        var runId = await harness.CreateRunAsync();

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(3, harness.ExecutedReads);
        Assert.Equal(2, harness.Handler.ChatCalls);
        Assert.Equal(3, harness.Handler.LastMessages.Count(message => message.GetProperty("role").GetString() == "tool"));
    }

    [Fact]
    public async Task OversizedFinalBatchExecutesOnlyRemainingBudgetThenSummarizesDeniedCalls()
    {
        using var harness = new Harness(readCalls: 3, batchSize: 3, maximumTools: 2);
        var runId = await harness.CreateRunAsync();

        Assert.IsType<AgentRunLimitException>(await harness.DriveAsync(runId));

        Assert.Equal(2, harness.ExecutedReads);
        Assert.Equal(2, harness.Handler.ChatCalls);
        Assert.True(harness.Handler.LastTurnHadNoTools);
        var checkpoint = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.Equal(2, checkpoint.ToolCallCount);
        Assert.Equal(3, checkpoint.Messages.Count(message => message.Role == "tool"));
        Assert.Contains(checkpoint.Messages, message => message.Role == "tool" && message.Content!.Contains("agent.tool_budget", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RunMode.Coding, null)]
    [InlineData(RunMode.Coding, 3600)]
    [InlineData(RunMode.General, null)]
    [InlineData(RunMode.General, 0)]
    [InlineData(RunMode.General, 3600)]
    public async Task SevenDayOldPersistedRunCompletesDespiteLegacyWallClockLimit(RunMode mode, int? legacyTimeoutSeconds)
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, mode, science: mode == RunMode.General);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(1, harness.ExecutedReads);
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.DoesNotContain(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunFailed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task NormalGeneralConversationAlsoIgnoresLegacyWallClockLimits(int? legacyTimeoutSeconds)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, RunMode.General);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));

        Assert.Null(await harness.DriveAsync(runId));

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(1, harness.Handler.ChatCalls);
    }

    [Fact]
    public async Task ExpiredIndividualClientOperationFailsThroughSingleQueueWithoutModelContact()
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync();
        var proposal = await harness.SaveWaitingProposalAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        using var cleanup = new StorageCleanupService(harness.Context.Database, harness.Context.WrappedOptions);
        await cleanup.CleanupExpiredAsync();
        Assert.NotNull(await harness.Repository.GetToolProposalAsync(proposal.ProposalId, runId));

        Assert.Equal(1, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow));
        Assert.Equal(RunState.Queued, (await harness.Repository.GetAsync(runId))!.State);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var subscription = harness.Notifier.Subscribe(runId);
        await harness.Processor.StartAsync(deadline.Token);
        try
        {
            while ((await harness.Repository.GetAsync(runId))!.State != RunState.Failed)
                await subscription.Reader.ReadAsync(deadline.Token);
        }
        finally
        {
            await harness.Processor.StopAsync(CancellationToken.None);
        }
        var failure = Assert.Single(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunFailed);
        Assert.Equal("run.timeout", failure.Data.GetProperty("errorCode").GetString());
        Assert.Contains("Client-Werkzeugauftrags", failure.Data.GetProperty("message").GetString());
        Assert.Equal(0, harness.Handler.Requests);
        Assert.Equal(0, harness.ExecutedReads);
        Assert.NotNull(await harness.Repository.GetCheckpointAsync(runId));
    }

    [Fact]
    public async Task PermanentNativeTokenCounterFailureIsPersistedBeforeAnyToolProposal()
    {
        using var harness = new Harness(readCalls: 0);
        harness.Handler.FailTokenCounting = true;
        harness.Handler.TokenCountingFailureStatus = HttpStatusCode.BadRequest;
        var runId = await harness.CreateRunAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var subscription = harness.Notifier.Subscribe(runId);
        await harness.Processor.StartAsync(deadline.Token);
        try
        {
            while ((await harness.Repository.GetAsync(runId))!.State != RunState.Failed)
                await subscription.Reader.ReadAsync(deadline.Token);
        }
        finally
        {
            await harness.Processor.StopAsync(CancellationToken.None);
        }

        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        var failure = Assert.Single(events, item => item.Type == RunEventTypes.RunFailed);
        Assert.Equal("provider.http_failed", failure.Data.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ModelGeneration
            && item.Data.GetProperty("state").GetString() == "providerRetryWaiting");
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.ClientToolProposed || item.Type == RunEventTypes.RunCompleted);
        Assert.Equal(0, harness.Handler.ChatCalls);
    }

    [Theory]
    [InlineData(RunState.WaitingForClient)]
    [InlineData(RunState.Running)]
    [InlineData(RunState.Queued)]
    [InlineData(RunState.Completed)]
    [InlineData(RunState.Failed)]
    [InlineData(RunState.Cancelled)]
    [InlineData(RunState.Interrupted)]
    public async Task DeadlineSweepDoesNotTouchNotDueWaitingOrOtherRunStates(RunState state)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync();
        await harness.SaveWaitingProposalAsync(runId, DateTimeOffset.UtcNow.AddMinutes(10));
        await harness.Repository.UpdateStateAsync(runId, state);
        if (state != RunState.WaitingForClient) await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow));

        Assert.Equal(state, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Empty(await harness.Repository.GetEventsAfterAsync(runId, 0));
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Fact]
    public async Task StoredClientReceiptSurvivesProposalExpiryAndRunAgeWithoutAnotherQueue()
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync();
        var now = DateTimeOffset.UtcNow;
        var proposal = await harness.SaveWaitingProposalAsync(runId, now.AddMinutes(1));
        await harness.Repository.SaveClientToolResultAsync(runId,
            new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { content = "saved" })));

        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(now.AddMinutes(2)));
        Assert.Equal(RunState.WaitingForClient, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(now.AddDays(7)));
        Assert.NotNull(await harness.Repository.GetClientToolResultAsync(proposal.ProposalId));

        Assert.Null(await harness.DriveAsync(runId));
        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
    }

    [Fact]
    public async Task DeadlineSweepClaimsAtMostSixtyFourAndNeverQueuesSameRunTwice()
    {
        using var harness = new Harness(readCalls: 0);
        for (var index = 0; index < 65; index++)
        {
            var runId = await harness.CreateRunAsync();
            await harness.SaveWaitingProposalAsync(runId, DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        var future = DateTimeOffset.UtcNow.AddHours(2);

        Assert.Equal(64, await harness.Deadlines.QueueExpiredAsync(future));
        Assert.Equal(1, await harness.Deadlines.QueueExpiredAsync(future));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(future));
    }

    [Theory]
    [InlineData(RunMode.Coding, 0)]
    [InlineData(RunMode.General, null)]
    [InlineData(RunMode.General, 0)]
    [InlineData(RunMode.General, 3600)]
    public async Task TemporaryNativeFailureWaitsDurablyAndResumesWithoutRepeatingConsumedClientTool(RunMode mode, int? legacyTimeoutSeconds)
    {
        using var harness = new Harness(readCalls: 1);
        var runId = await harness.CreateRunAsync(legacyTimeoutSeconds, mode, science: mode == RunMode.General);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        await Assert.ThrowsAsync<RunWaitingForClientException>(() => harness.Processor.ProcessAsync(runId, CancellationToken.None));
        var pending = (await harness.Repository.GetCheckpointAsync(runId))!;
        await harness.Repository.SaveClientToolResultAsync(runId, new ClientToolResult(pending.PendingProposalId!, "completed", JsonSerializer.SerializeToElement(new { content = "already executed" })));
        harness.Handler.FailTokenCounting = true;
        var failure = await Assert.ThrowsAsync<ModelProviderRequestException>(() => harness.Processor.ProcessAsync(runId, CancellationToken.None));

        Assert.True(await harness.Processor.TryScheduleProviderRetryAsync(runId, failure));
        var retryAt = (await harness.Repository.GetProviderRetryTimeAsync(runId))!.Value;
        Assert.Equal(RunState.Queued, (await harness.Repository.GetAsync(runId))!.State);
        var saved = (await harness.Repository.GetCheckpointAsync(runId))!;
        Assert.Null(saved.PendingProposalId);
        Assert.Single(saved.Messages, message => message.Role == "tool" && message.Content!.Contains("already executed", StringComparison.Ordinal));
        Assert.Contains(runId, await harness.Repository.RecoverAsync());
        var requests = harness.Handler.Requests;
        await harness.Processor.ProcessAsync(runId, CancellationToken.None);
        Assert.Equal(requests, harness.Handler.Requests);
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(retryAt.AddSeconds(-1)));
        Assert.Equal(1, await harness.Deadlines.QueueExpiredAsync(retryAt.AddSeconds(1)));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(retryAt.AddSeconds(1)));
        harness.Handler.FailTokenCounting = false;

        await harness.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Null(await harness.Repository.GetProviderRetryTimeAsync(runId));
        var events = await harness.Repository.GetEventsAfterAsync(runId, 0);
        Assert.Single(events, item => item.Type == RunEventTypes.ClientToolProposed);
        Assert.Contains(events, item => item.Type == RunEventTypes.ModelGeneration && item.Data.GetProperty("state").GetString() == "providerRetryWaiting");
        Assert.DoesNotContain(events, item => item.Type == RunEventTypes.RunFailed);
    }

    [Fact]
    public async Task RetryBackoffIsBoundedAndCancellationCannotBeRequeued()
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(timeoutSeconds: 0);
        var now = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            var retry = (await harness.Repository.ScheduleProviderRetryAsync(runId, now))!.Value;
            Assert.Equal(attempt, retry.Attempt);
            Assert.Equal(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempt - 1, 6))), retry.Delay.TotalSeconds);
        }
        await harness.Repository.UpdateStateAsync(runId, RunState.Cancelled);
        Assert.Null(await harness.Repository.ScheduleProviderRetryAsync(runId, now));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(now.AddDays(1)));
        Assert.Equal(RunState.Cancelled, (await harness.Repository.GetAsync(runId))!.State);
    }

    [Fact]
    public async Task NativeConnectionReadFailureAlsoUsesPersistentRetry()
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(timeoutSeconds: 0);
        Assert.True(await harness.Processor.TryScheduleProviderRetryAsync(runId,
            new ModelProviderRequestException("generation", 3, new IOException("connection closed"))));
        Assert.NotNull(await harness.Repository.GetProviderRetryTimeAsync(runId));
    }

    [Theory]
    [InlineData(RunMode.Coding, true)]
    [InlineData(RunMode.Coding, false)]
    [InlineData(RunMode.General, true)]
    [InlineData(RunMode.General, false)]
    public async Task PersistedCancellationWinsOnEitherSideOfRetryScheduling(RunMode mode, bool retryFirst)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(3600, mode, science: mode == RunMode.General);
        await harness.Repository.UpdateStateAsync(runId, RunState.Running);
        var failure = new ModelProviderRequestException("generation", 3,
            new HttpRequestException("temporary outage", null, HttpStatusCode.ServiceUnavailable));
        if (retryFirst) Assert.True(await harness.Processor.TryScheduleProviderRetryAsync(runId, failure));
        Assert.True(await harness.Repository.CancelAsync(runId));
        Assert.False(await harness.Repository.CancelAsync(runId));
        if (!retryFirst) Assert.True(await harness.Processor.TryScheduleProviderRetryAsync(runId, failure));
        await harness.Repository.UpdateStateAsync(runId, RunState.Running);
        await harness.Processor.ProcessAsync(runId, CancellationToken.None);

        Assert.Equal(RunState.Cancelled, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Null(await harness.Repository.GetProviderRetryTimeAsync(runId));
        Assert.Equal(0, await harness.Deadlines.QueueExpiredAsync(DateTimeOffset.UtcNow.AddDays(1)));
        Assert.Single(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCancelled);
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(3600, HttpStatusCode.BadRequest)]
    public async Task PermanentProviderErrorsDoNotRetryRegardlessOfLegacyTimeLimit(int timeout, HttpStatusCode status)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(timeout);
        Assert.False(await harness.Processor.TryScheduleProviderRetryAsync(runId,
            new ModelProviderRequestException("generation", 3, new HttpRequestException("native error", null, status))));
        Assert.False(await harness.Processor.TryScheduleProviderRetryAsync(runId, new JsonException("invalid tool JSON")));
        Assert.Null(await harness.Repository.GetProviderRetryTimeAsync(runId));
    }

    [Theory]
    [InlineData(RunState.Running, null, true)]
    [InlineData(RunState.Interrupted, "run.gateway_stopped", true)]
    [InlineData(RunState.Interrupted, "run.gateway_restarted", true)]
    [InlineData(RunState.Interrupted, "custom.interruption", false)]
    [InlineData(RunState.Completed, null, false)]
    [InlineData(RunState.Cancelled, "run.cancelled", false)]
    [InlineData(RunState.Failed, "agent.run_limit", false)]
    public async Task GatewayRecoveryResumesOnlyInterruptedCodingWorkAndPreservesItsReceipt(RunState state, string? errorCode, bool recover)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(timeoutSeconds: 0);
        var proposal = await harness.SaveWaitingProposalAsync(runId, DateTimeOffset.MaxValue);
        await harness.Repository.SaveClientToolResultAsync(runId,
            new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { applied = true })));
        await harness.Repository.UpdateStateAsync(runId, state, errorCode: errorCode);

        var recovered = await harness.Repository.RecoverAsync();

        Assert.Equal(recover, recovered.Contains(runId, StringComparer.Ordinal));
        Assert.Equal(recover ? RunState.Queued : state, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(proposal.ProposalId, (await harness.Repository.GetCheckpointAsync(runId))!.PendingProposalId);
        Assert.NotNull(await harness.Repository.GetClientToolResultAsync(proposal.ProposalId));
        Assert.Equal(0, harness.Handler.Requests);
    }

    [Theory]
    [InlineData(RunState.Running, null, true)]
    [InlineData(RunState.Interrupted, "run.gateway_stopped", true)]
    [InlineData(RunState.Interrupted, "run.gateway_restarted", true)]
    [InlineData(RunState.Interrupted, "custom.interruption", false)]
    [InlineData(RunState.Cancelled, "run.cancelled", false)]
    [InlineData(RunState.Failed, "agent.run_limit", false)]
    public async Task SevenDayScienceGatewayRecoveryPreservesReceiptAndNeverRestartsUserCancellation(RunState state, string? errorCode, bool recover)
    {
        using var harness = new Harness(readCalls: 0);
        var runId = await harness.CreateRunAsync(3600, RunMode.General, science: true);
        await harness.SetCreatedAtAsync(runId, DateTimeOffset.UtcNow.AddDays(-7));
        var proposal = await harness.SaveWaitingProposalAsync(runId, DateTimeOffset.MaxValue);
        await harness.Repository.SaveClientToolResultAsync(runId,
            new ClientToolResult(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new { applied = true })));
        await harness.Repository.UpdateStateAsync(runId, state, errorCode: errorCode);

        var recovered = await harness.Repository.RecoverAsync();

        Assert.Equal(recover, recovered.Contains(runId, StringComparer.Ordinal));
        Assert.Equal(recover ? RunState.Queued : state, (await harness.Repository.GetAsync(runId))!.State);
        Assert.Equal(proposal.ProposalId, (await harness.Repository.GetCheckpointAsync(runId))!.PendingProposalId);
        Assert.NotNull(await harness.Repository.GetClientToolResultAsync(proposal.ProposalId));
        if (recover)
        {
            Assert.Null(await harness.DriveAsync(runId));
            Assert.Equal(RunState.Completed, (await harness.Repository.GetAsync(runId))!.State);
            Assert.Single(await harness.Repository.GetEventsAfterAsync(runId, 0), item => item.Type == RunEventTypes.RunCompleted);
        }
        else Assert.Equal(0, harness.Handler.Requests);
    }

    private sealed class Harness : IDisposable
    {
        internal const string InterimAnswer = "Zwischenstand: Dateien gelesen; Umsetzung und Tests sind noch offen. Nächster Schritt: die erkannte Startfunktion gezielt ändern.";
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;
        private readonly ModelRuntimeClient _runtime;
        public TestServerContext Context { get; } = new();
        public NativeHandler Handler { get; }
        public RunRepository Repository { get; }
        public RunProcessor Processor { get; }
        public ClientToolDeadlineService Deadlines { get; }
        public RunEventNotifier Notifier { get; }
        public int ExecutedReads { get; private set; }
        public long HighestSavedCompaction { get; private set; }

        private readonly string _readContent;
        public Harness(int readCalls, int batchSize = 1, int maximumRounds = 0, int maximumTools = 0, int readContentCharacters = 0,
            int contextTokens = 32768)
        {
            _readContent = "1: return await LoadAsync();" + new string('x', readContentCharacters);
            Context.Options.ModelRuntimeUri = new Uri("http://native.test");
            Context.Options.CodingMaximumModelRounds = maximumRounds;
            Context.Options.CodingMaximumToolCalls = maximumTools;
            Handler = new NativeHandler(readCalls, batchSize, contextTokens);
            _http = new HttpClient(Handler);
            _runtime = new ModelRuntimeClient(_http, Context.WrappedOptions, NullLogger<ModelRuntimeClient>.Instance);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(Context.Options, includeHostedServices: false);
            services.AddSingleton(Context.Database);
            services.AddSingleton(_runtime);
            _services = services.BuildServiceProvider();
            Repository = _services.GetRequiredService<RunRepository>();
            Processor = _services.GetRequiredService<RunProcessor>();
            Deadlines = _services.GetRequiredService<ClientToolDeadlineService>();
            Notifier = _services.GetRequiredService<RunEventNotifier>();
        }

        public async Task<string> CreateRunAsync(int? timeoutSeconds = 3600, RunMode mode = RunMode.Coding, bool science = false) => (await Repository.CreateAsync(new RunRequest(
            MissumAiProtocol.Version, mode,
            [new RunMessage("user", [new ContentPart("text", "Analysiere das Projekt und korrigiere den langsamen Programmstart.")])],
            ClientCapabilities: science ? ["coding", "workspace", "research.sandbox"] : ["coding"],
            Limits: new RunLimits(TimeoutSeconds: timeoutSeconds),
            AllowedServerTools: [], PreferredCodingModelId: ModelId, PreferredGeneralModelId: ModelId,
            DeepResearch: science, ResearchOptions: science ? new DeepResearchOptions(
                ProjectId: "research-fixture", AutonomyLevel: ResearchAutonomyLevel.SandboxResearch) : null), null)).Snapshot.RunId;

        public async Task<ToolProposal> SaveWaitingProposalAsync(string runId, DateTimeOffset expiresAt)
        {
            var call = new LmToolCall("waiting-call", ClientToolNames.CodingRead,
                JsonSerializer.SerializeToElement(new { path = "src/waiting.cs" }));
            var proposal = new ToolProposal("proposal-" + Guid.NewGuid().ToString("N"), runId, call.Name,
                call.Arguments, ToolRiskClass.ReadOnly, "Datei lesen", expiresAt);
            await Repository.SaveToolProposalAsync(proposal);
            await Repository.SaveCheckpointAsync(runId, new AgentRunCheckpoint(
                [new LmChatMessage("user", "Prüfe den gespeicherten Projektstand."),
                    new LmChatMessage("assistant", "Ich lese die nächste Datei.", ToolCalls: [call])],
                1, 1, 16, 4, ActiveToolCalls: [call], PendingProposalId: proposal.ProposalId, PendingToolCallId: call.Id));
            await Repository.UpdateStateAsync(runId, RunState.WaitingForClient);
            return proposal;
        }

        public async Task SetCreatedAtAsync(string runId, DateTimeOffset createdAt)
        {
            await using var connection = await Context.Database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE runs SET created_at = $created WHERE run_id = $run;";
            command.Parameters.AddWithValue("$created", MissumAiDatabase.FormatTimestamp(createdAt));
            command.Parameters.AddWithValue("$run", runId);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<Exception?> DriveAsync(string runId)
        {
            for (var continuation = 0; continuation < 512; continuation++)
            {
                try
                {
                    await Processor.ProcessAsync(runId, CancellationToken.None);
                    return null;
                }
                catch (RunWaitingForClientException)
                {
                    var checkpoint = (await Repository.GetCheckpointAsync(runId))!;
                    HighestSavedCompaction = Math.Max(HighestSavedCompaction, checkpoint.CompactionCount);
                    var proposal = (await Repository.GetToolProposalAsync(checkpoint.PendingProposalId!, runId))!;
                    Assert.Equal(ClientToolNames.CodingRead, proposal.Name);
                    await Repository.SaveClientToolResultAsync(runId, new ClientToolResult(proposal.ProposalId, "completed",
                        JsonSerializer.SerializeToElement(new { path = proposal.Arguments.GetProperty("path").GetString(), content = _readContent, sha256 = new string('a', 64), totalLines = 1, startLine = 1, truncated = false })));
                    ExecutedReads++;
                }
                catch (AgentRunLimitException exception)
                {
                    return exception;
                }
            }
            throw new InvalidOperationException("Deterministic run did not terminate within its bounded continuation budget.");
        }

        public void Dispose()
        {
            _services.Dispose();
            _runtime.Dispose();
            _http.Dispose();
            Context.Dispose();
        }
    }

    private sealed class NativeHandler(int readCalls, int batchSize, int contextTokens) : HttpMessageHandler
    {
        // The runtime moves the German reasoning rule to the beginning of the
        // system message. Identify the whole unchanged summary task, not its
        // previous wire placement relative to that shared language policy.
        private static readonly string SummaryTask = CodingContextCompactor.SummaryInstruction
            .Replace(CodingAgentPolicy.ReasoningLanguagePrompt, "", StringComparison.Ordinal).Trim();
        public int ChatCalls { get; private set; }
        public int Requests { get; private set; }
        public bool FailTokenCounting { get; set; }
        public HttpStatusCode TokenCountingFailureStatus { get; set; } = HttpStatusCode.InternalServerError;
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource GenerationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LastTurnHadNoTools { get; private set; }
        public int Compactions { get; private set; }
        public JsonElement[] LastMessages { get; private set; } = [];
        private int _issuedReads;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath is "/sessions/prepare" or "/sessions/save")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/models") return Json(new { data = new[] { new { id = ModelId,
                tags = new[] { $"missum-context-train:{contextTokens}" }, status = new { value = "loaded" } } } });
            if (path == "/props") return Json(new { default_generation_settings = new { n_ctx = contextTokens } });
            if (path == "/v1/chat/completions/input_tokens") return FailTokenCounting
                ? new HttpResponseMessage(TokenCountingFailureStatus) { Content = new StringContent("proxy error: Failed to read connection") }
                : Json(new { input_tokens = 16 });
            if (path != "/v1/chat/completions") throw new InvalidOperationException("Unexpected endpoint " + request.RequestUri);
            if (WaitForCancellation)
            {
                GenerationStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            LastMessages = body.RootElement.GetProperty("messages").EnumerateArray().Select(item => item.Clone()).ToArray();
            LastTurnHadNoTools = !body.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0;
            ChatCalls++;
            object delta;
            string finish;
            if (LastMessages.Any(message => message.GetProperty("role").GetString() == "system"
                && message.GetProperty("content").GetString()?.Contains(SummaryTask, StringComparison.Ordinal) == true))
            {
                Assert.True(LastTurnHadNoTools);
                Compactions++;
                delta = new { content = "Bisherige Dateien wurden geprüft. Auftrag: langsamen Programmstart korrigieren. Noch offen: weitere Dateien prüfen, Änderung und Tests." };
                finish = "stop";
            }
            else if (!LastTurnHadNoTools && _issuedReads < readCalls
                && tools.EnumerateArray().Any(tool => tool.GetProperty("function").GetProperty("name").GetString()
                    == ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName)))
            {
                delta = new { tool_calls = new[] { new { index = 0, id = "select-" + (_issuedReads + 1), type = "function", function = new
                {
                    name = ModelRuntimeClient.ToTransportToolName(AgentToolCatalog.SelectorToolName),
                    arguments = JsonSerializer.Serialize(new { name = ClientToolNames.CodingRead }),
                } } } };
                finish = "tool_calls";
            }
            else if (!LastTurnHadNoTools && _issuedReads < readCalls)
            {
                var calls = Enumerable.Range(0, Math.Min(batchSize, readCalls - _issuedReads)).Select(index =>
                {
                    var number = ++_issuedReads;
                    return new { index, id = "read-" + number, type = "function", function = new
                    {
                        name = ModelRuntimeClient.ToTransportToolName(ClientToolNames.CodingRead),
                        arguments = JsonSerializer.Serialize(new { path = $"src/file{number}.cs" }),
                    } };
                }).ToArray();
                delta = new { content = "Ich prüfe die nächste Datei.", tool_calls = calls };
                finish = "tool_calls";
            }
            else
            {
                delta = new { content = LastTurnHadNoTools ? Harness.InterimAnswer : "Projektanalyse vollständig abgeschlossen." };
                finish = "stop";
            }
            var frame = JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } }, usage = new { prompt_tokens = 16, completion_tokens = 4 } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + frame + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}

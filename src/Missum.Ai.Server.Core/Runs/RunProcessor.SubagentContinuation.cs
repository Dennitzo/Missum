using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    /// <summary>Starts a new owned attempt from an authorized child's own durable history.</summary>
    private async Task<AgentToolExecutionResult> ResumeSubagentAsync(JsonElement arguments, string parentRunId,
        string operationId, RunRequest parentRequest, ModelSelection selection,
        List<LmChatMessage> parentMessages, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(parentMessages);
        // The dispatcher owns _subagentSpawnGate. Authorization is derived from
        // the parent's committed continuation state, never from model-supplied ownership.
        var sourceRunId = arguments.GetProperty("resumeRunId").GetString()!;
        var authorized = await _repository.GetAuthorizedSubagentContinuationAsync(parentRunId, sourceRunId, token).ConfigureAwait(false);
        if (authorized is not { } source || source.Request.Subagent is not { } sourceRelation)
            return SubagentFailure("subagent.invalid_continuation_owner",
                "Dieser frühere Subagent ist für die Fortsetzung des aktuellen Hauptlaufs nicht freigegeben.");
        if (!arguments.TryGetProperty("task", out var requestedTask) || requestedTask.ValueKind != JsonValueKind.String
            || !string.Equals(requestedTask.GetString()?.Trim(), sourceRelation.AssignedTask.Trim(), StringComparison.Ordinal))
            return SubagentFailure("subagent.continuation_task_changed",
                "Beim Fortsetzen bleibt die ursprüngliche Teilaufgabe unverändert. Übernimm task exakt aus dem gespeicherten Subagentenstand; eine neue Teilaufgabe benötigt einen eigenen spawn ohne resumeRunId.");

        var continuationOperation = "resume:" + parentRunId + ":" + sourceRunId;
        if (await _repository.FindSubagentByOperationAsync(continuationOperation, token).ConfigureAwait(false) is { } previous)
        {
            var previousRequest = await _repository.GetRequestAsync(previous.RunId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Der gespeicherte Subagenten-Fortsetzungsauftrag fehlt.");
            if (previousRequest.Subagent?.ParentRunId != parentRunId
                || previousRequest.Subagent.AgentId != sourceRelation.AgentId)
                return SubagentFailure("subagent.invalid_continuation_owner", "Der gespeicherte Fortsetzungsversuch gehört zu einem anderen Hauptlauf.");
            if (previous.State == RunState.Completed)
                return await ReadCompletedSubagentContinuationAsync(previous, previousRequest, sourceRunId, token).ConfigureAwait(false);
            if (!IsTerminalSubagentState(previous.State))
            {
                await EnsureSubagentStartedAsync(previous, previousRequest, token).ConfigureAwait(false);
                _ = StartSubagentRunner(previous.RunId, previousRequest);
            }
            return SubagentContinuationReceipt(previous, previousRequest, sourceRunId);
        }
        if (source.Snapshot.State == RunState.Completed)
            return await ReadCompletedSubagentContinuationAsync(source.Snapshot, source.Request, sourceRunId, token).ConfigureAwait(false);
        if (source.Snapshot.State is not (RunState.Cancelled or RunState.Interrupted or RunState.Failed))
            return SubagentFailure("subagent.source_still_running",
                "Der ursprüngliche Subagent ist noch aktiv. Warte auf seinen tatsächlichen Endzustand; es wurde kein paralleler Fortsetzungsversuch gestartet.");
        if (await _repository.GetSubagentRunsAsync(activeOnly: true, cancellationToken: token).ConfigureAwait(false) is { Count: > 0 })
            return SubagentFailure("subagent.gpu_busy",
                "GPU1 bearbeitet bereits einen Subagenten. Der gespeicherte abgebrochene Auftrag bleibt erhalten und wurde noch nicht fortgesetzt.");
        var availability = await _modelRuntime.GetSubagentAvailabilityAsync(selection.ModelId, selection.ContextLength, token).ConfigureAwait(false);
        if (!availability.Allowed)
            return SubagentFailure("subagent.insufficient_vram", availability.Reason
                ?? "Das aktuelle Modell lässt derzeit keinen GPU1-Subagenten zu; der frühere Auftrag bleibt gespeichert.");
        var sourceCheckpoint = await _repository.GetSubagentContinuationCheckpointAsync(sourceRunId, token).ConfigureAwait(false);
        if (sourceCheckpoint is null || sourceCheckpoint.Messages.Count == 0)
            return SubagentFailure("subagent.continuation_checkpoint_missing",
                "Für diesen Subagenten fehlt ein gespeicherter Arbeitskontext. Kein Wiederaufnahmezustand wurde erfunden; ein neuer expliziter Auftrag bleibt möglich.");

        var recovered = await RecoverSubagentToolReceiptsAsync(sourceRunId, sourceCheckpoint, token).ConfigureAwait(false);
        var childMessages = CloseInterruptedSubagentToolCalls(sourceCheckpoint.Messages, recovered).ToList();
        // Keep the evaluated child prefix and completed receipts intact. Do not
        // fork today's parent transcript over this branch's own progressed work.
        childMessages.Add(new("user", "MISSUM_SUBAGENT_FORTSETZUNG\n" + JsonSerializer.Serialize(new
        {
            sourceRunId, previousState = source.Snapshot.State, source.Snapshot.ErrorCode,
            agentId = sourceRelation.AgentId, task = sourceRelation.AssignedTask,
        }, MissumAiProtocol.CreateJsonOptions())
            + "\nSetze ausschließlich deine ursprüngliche Teilaufgabe mit diesem gespeicherten Arbeitsstand fort. "
            + "Übernimm abgeschlossene Schritte und echte Werkzeugbelege. Prüfe unbekannte Werkzeugausgänge vor einer erneuten Mutation; "
            + "eine Unterbrechung ist kein erfolgreicher Abschluss. Wiederhole keine erledigten Aufgaben oder vollständigen Texte. "
            + "Nutze die aktuell angebotenen Werkzeuge und Rechte. Liefere die noch offenen Ergebnisse an den aktuellen Hauptagenten."));
        if (parentRequest.ClientCapabilities?.Contains("research.deliverables", StringComparer.OrdinalIgnoreCase) == true)
            ScientificDerivationPolicy.EnsureAtNewRunBoundary(childMessages);

        var childRequest = parentRequest with
        {
            SessionId = source.Request.SessionId,
            PreferredGeneralModelId = selection.ModelId,
            PreferredCodingModelId = selection.ModelId,
            Workload = null,
            DeepResearch = false,
            ResearchOptions = ScientificStateCompletionPolicy.Enabled(parentRequest)
                ? parentRequest.ResearchOptions! with { ProjectId = ScientificStateCompletionPolicy.ProjectId(parentRequest) }
                : parentRequest.ResearchOptions,
            Subagent = sourceRelation with
            {
                ParentRunId = parentRunId, ParentSessionId = parentRequest.SessionId,
                RuntimeInstanceId = null, ParentSessionCacheKey = BuildRunSessionCacheKey(parentRequest, parentRunId, selection.Role),
            },
            // Logical task history belongs to the child, not to the new parent.
            Messages = source.Request.Messages,
            ContextProfileVersion = sourceCheckpoint.ContextProfileVersion,
        };
        var childTools = CreateModelToolDefinitions(_toolCatalog.GetAvailableTools(childRequest, subagentAvailable: true),
            selectedToolName: null, directTools: true, sourceCheckpoint.ContextProfileVersion);
        var childCacheKey = BuildRunSessionCacheKey(childRequest, sourceRelation.AgentId, selection.Role);
        childMessages = ModelRuntimeClient.PrepareLanguageBoundMessages(childMessages).ToList();
        SubagentRuntimePreparation prepared;
        try
        {
            // Null source key restores this session's progressed GPU1 snapshot;
            // it never copies the current main-agent KV cache over the child.
            prepared = await _modelRuntime.PrepareSubagentAsync(selection.ModelId, null, childCacheKey,
                childMessages, childTools, selection.Role,
                _modelRuntime.ResolveReasoningEffort(selection.ModelId, selection.Role, childRequest.ReasoningEffort),
                cancellationToken: token, childSessionId: childRequest.SessionId,
                parentSessionId: parentRequest.SessionId ?? parentRunId).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            return SubagentFailure("subagent.preparation_failed", exception.Message);
        }
        childRequest = childRequest with { Subagent = childRequest.Subagent! with { RuntimeInstanceId = prepared.InstanceId } };
        var checkpoint = sourceCheckpoint with
        {
            Messages = childMessages,
            // The new run owns its accounting/budget; the old totals remain in
            // its immutable journal and the continuation provenance below.
            RoundCount = 0, ToolCallCount = 0, InputTokens = 0, OutputTokens = 0, BudgetWarningIssued = false,
            ActiveToolCalls = null, NextToolIndex = 0, PendingProposalId = null, PendingToolCallId = null,
            ActiveCallRound = null, ActiveCallsReadOnly = false, SelectedToolName = null,
            StreamingTurnStartEventId = null, VisibleTextLength = 0,
            RequiredToolCallRetryCount = 0, EmptyResponseRetryCount = 0, IncompleteResponseRetryCount = 0,
            InvalidToolTurnCount = 0, AppliedSteeringSequence = 0, AppliedModelSelectionEventId = 0,
            EarlySubagentDelegationPending = false, EarlySubagentDelegationRetryCount = 0,
            SelectedModelId = selection.ModelId, SelectedReasoningEffort = childRequest.ReasoningEffort,
            PreserveSessionPromptPrefix = true, UseStableSubagentToolCatalog = true,
            CanonicalStateReadRequired = ScientificStateCompletionPolicy.Enabled(childRequest),
            ContextTools = childTools,
            ToolCatalogSignature = sourceCheckpoint.ContextProfileVersion is null ? null
                : ToolContextProfiles.Signature(childTools, selection.ModelId),
        };
        var child = await _repository.CreateSubagentAsync(childRequest, checkpoint, continuationOperation, token).ConfigureAwait(false);
        await EnsureSubagentStartedAsync(child, childRequest, token).ConfigureAwait(false);
        await _repository.AppendEventAsync(child.RunId, "subagent.contextResumed", new
        {
            sourceRunId, parentRunId, operationId, agentId = sourceRelation.AgentId,
            previousState = source.Snapshot.State, source.Snapshot.ErrorCode,
            childCacheKey, messageCount = childMessages.Count, recoveredToolResults = recovered.Count,
            previousRoundCount = sourceCheckpoint.RoundCount, previousToolCallCount = sourceCheckpoint.ToolCallCount,
            previousInputTokens = sourceCheckpoint.InputTokens, previousOutputTokens = sourceCheckpoint.OutputTokens,
            prepared.InstanceId, prepared.GpuIndex, prepared.CacheStatus, prepared.CachedTokens,
        }, token).ConfigureAwait(false);
        _ = StartSubagentRunner(child.RunId, childRequest);
        return SubagentContinuationReceipt(child, childRequest, sourceRunId);
    }

    private async Task<Dictionary<string, string>> RecoverSubagentToolReceiptsAsync(string sourceRunId,
        AgentRunCheckpoint checkpoint, CancellationToken token)
    {
        var recovered = new Dictionary<string, string>(StringComparer.Ordinal);
        var calls = checkpoint.ActiveToolCalls ?? [];
        if (checkpoint.PendingProposalId is { } proposal && checkpoint.PendingToolCallId is { } pendingCall
            && calls.Skip(checkpoint.NextToolIndex).Any(call => call.Id == pendingCall)
            && await _repository.GetToolProposalAsync(proposal, sourceRunId, token).ConfigureAwait(false) is { } ownedProposal
            && calls.Skip(checkpoint.NextToolIndex).Any(call => call.Id == pendingCall && call.Name == ownedProposal.Name
                && call.Arguments.GetRawText() == ownedProposal.Arguments.GetRawText())
            && await _repository.GetClientToolResultAsync(proposal, token).ConfigureAwait(false) is { } clientResult)
            recovered[pendingCall] = SerializeClientToolResult(clientResult);
        var completed = await _repository.GetServerToolCompletedEventsAsync(sourceRunId, cancellationToken: token).ConfigureAwait(false);
        for (var index = checkpoint.NextToolIndex; index < calls.Count; index++)
        {
            var call = calls[index];
            if (recovered.ContainsKey(call.Id)) continue;
            var identity = CreateServerToolOperationId(sourceRunId, "main", checkpoint.ActiveCallRound ?? checkpoint.RoundCount,
                index, call.Id);
            var receipt = completed.LastOrDefault(item => item.RunId == sourceRunId
                && item.Data.ValueKind == JsonValueKind.Object
                && ScientificStateCompletionPolicy.Text(item.Data, "toolCallId") == identity
                && ScientificStateCompletionPolicy.Text(item.Data, "tool") == call.Name
                && item.Data.TryGetProperty("result", out _));
            if (receipt is not null) recovered[call.Id] = receipt.Data.GetProperty("result").GetRawText();
        }
        return recovered;
    }

    /// <summary>Closes only outstanding provider calls; a missing receipt is never replay permission.</summary>
    internal static IReadOnlyList<LmChatMessage> CloseInterruptedSubagentToolCalls(IReadOnlyList<LmChatMessage> source,
        IReadOnlyDictionary<string, string> recovered)
    {
        var messages = new List<LmChatMessage>();
        var pending = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        foreach (var message in source)
        {
            if (message.Role != "tool") ClosePending(useRecovered: false);
            messages.Add(message);
            foreach (var call in message.ToolCalls ?? []) pending[call.Id] = call;
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        ClosePending(useRecovered: true);
        return messages;

        void ClosePending(bool useRecovered)
        {
            foreach (var call in pending.Values)
                messages.Add(new("tool", useRecovered && recovered.TryGetValue(call.Id, out var receipt) ? receipt
                    : "{\"status\":\"interrupted\",\"outcomeUnknown\":true,\"message\":\"Für diesen unterbrochenen Aufruf liegt kein zugeordneter dauerhafter Werkzeugausgang vor. Prüfe den tatsächlichen Zustand vor einer erneuten Änderung; nicht automatisch wiederholen.\"}",
                    ToolCallId: call.Id));
            pending.Clear();
        }
    }

    private static AgentToolExecutionResult SubagentContinuationReceipt(RunSnapshot child, RunRequest request, string sourceRunId) =>
        new(JsonSerializer.SerializeToElement(new
        {
            status = !IsTerminalSubagentState(child.State) ? "started"
                : child.State == RunState.Completed ? "completed" : child.State.ToString().ToLowerInvariant(),
            state = child.State, runId = child.RunId, agentId = request.Subagent!.AgentId,
            resumedFromRunId = sourceRunId, task = request.Subagent.AssignedTask,
            sessionId = request.SessionId, modelId = child.SelectedModel ?? request.PreferredGeneralModelId,
            gpuIndex = 1, errorCode = child.ErrorCode,
            instruction = "Der tatsächliche Zustand dieses Fortsetzungsversuchs gilt. Arbeite parallel an deiner eigenen Aufgabe; verwende subagent.wait mit dieser runId für das neue Ergebnis. Abgeschlossene Teilaufgaben werden nicht erneut ausgeführt.",
        }, MissumAiProtocol.CreateJsonOptions()), [],
            Succeeded: child.State is not (RunState.Cancelled or RunState.Interrupted or RunState.Failed),
            ErrorCode: child.State is RunState.Cancelled or RunState.Interrupted or RunState.Failed ? child.ErrorCode : null);

    private async Task<AgentToolExecutionResult> ReadCompletedSubagentContinuationAsync(RunSnapshot child,
        RunRequest request, string sourceRunId, CancellationToken token)
    {
        var visible = await _repository.GetVisibleTextEventsAsync(child.RunId, cancellationToken: token).ConfigureAwait(false);
        var events = await _repository.GetEventsAfterAsync(child.RunId, 0, token).ConfigureAwait(false);
        var evidence = await ReadSubagentCompletionEvidenceAsync(child.RunId, events, token).ConfigureAwait(false);
        var artifactIds = events.Where(static item => item.Type == RunEventTypes.ArtifactCreated)
            .Select(static item => item.Data.TryGetProperty("artifactId", out var id) ? id.GetString() : null)
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        return new(JsonSerializer.SerializeToElement(new
        {
            status = "completed", runId = child.RunId, agentId = request.Subagent!.AgentId,
            resumedFromRunId = sourceRunId,
            task = request.Subagent.AssignedTask, result = Missum.Ai.Server.Core.Coding.CodingTextReconciler.Project(visible),
            artifactIds, completionEvidence = evidence,
            instruction = "Dieser Subagent hat seine Aufgabe bereits abgeschlossen. Übernimm das gespeicherte Ergebnis; es wurde keine neue Ausführung gestartet.",
        }, MissumAiProtocol.CreateJsonOptions()), []);
    }
}

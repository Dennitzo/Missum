using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    private readonly SemaphoreSlim _subagentSpawnGate = new(1, 1);
    private readonly Dictionary<string, SubagentExecution> _subagentRuns = new(StringComparer.Ordinal);
    private CancellationToken _serviceStoppingToken;

    private sealed record SubagentExecution(string ParentRunId, CancellationTokenSource Cancellation, Task Completion);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Task[] completions;
        lock (_activeGate) completions = _subagentRuns.Values.Select(static child => child.Completion).ToArray();
        if (completions.Length > 0)
            await Task.WhenAll(completions).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        base.Dispose();
        lock (_activeGate)
            foreach (var child in _subagentRuns.Values) child.Cancellation.Dispose();
        _subagentSpawnGate.Dispose();
    }

    private static bool IsTerminalSubagentState(RunState state) => state is
        RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Interrupted;

    /// <summary>A terminal main run owns the shutdown of every unfinished delegated branch.</summary>
    internal async Task CancelSubagentsForTerminalParentAsync(string parentRunId)
    {
        // MarkFailed/MarkCancelled also run inside a child runner. Never await
        // that runner from its own failure path or cancel its parent/siblings.
        if ((await _repository.GetRequestAsync(parentRunId).ConfigureAwait(false))?.Subagent is not null
            || await _repository.GetAsync(parentRunId).ConfigureAwait(false) is not { } parent
            || !IsTerminalSubagentState(parent.State)) return;

        var completions = new List<Task>();
        foreach (var (child, request) in await _repository.GetSubagentRunsAsync(parentRunId,
            activeOnly: true).ConfigureAwait(false))
        {
            if (child.RunId == parentRunId || request.Subagent is not { } relation
                || relation.ParentRunId != parentRunId) continue;
            // Commit cancellation before signalling inference: a racing late
            // completion cannot overwrite it. Completed branches stay intact.
            _ = await _repository.CancelAsync(child.RunId).ConfigureAwait(false);
            _ = Cancel(child.RunId);
            SubagentExecution? execution;
            lock (_activeGate) _ = _subagentRuns.TryGetValue(child.RunId, out execution);
            if (execution is not null)
                completions.Add(execution.Completion);
            else
            {
                // A persisted queued/suspended branch may have no runner on
                // this host. Its terminal receipt must still reach the parent.
                await DrainSubagentEventsAsync(child.RunId, relation, CancellationToken.None).ConfigureAwait(false);
                var final = await _repository.GetAsync(child.RunId).ConfigureAwait(false);
                if (final is not null)
                    await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted,
                        new(parentRunId, child.RunId, relation.AgentId, relation.AssignedTask,
                            final.SelectedModel ?? request.PreferredGeneralModelId ?? request.PreferredCodingModelId ?? "",
                            final.State, request.SessionId, final.ErrorCode)).ConfigureAwait(false);
            }
        }
        if (completions.Count > 0) await Task.WhenAll(completions).ConfigureAwait(false);
    }

    internal static string BuildRunSessionCacheKey(RunRequest request, string runId, string role) =>
        ModelRuntimeClient.BuildSessionCacheKey(request.SessionId ?? runId, role,
            request.Mode == RunMode.Coding ? request.CodingOptions?.WorkspacePath : request.WorkspacePath);

    /// <summary>Preserves every evaluated parent message and closes outstanding parent-only tool calls.</summary>
    internal static IReadOnlyList<LmChatMessage> ForkSubagentMessages(IReadOnlyList<LmChatMessage> parentMessages, string task)
    {
        var messages = parentMessages.ToList();
        var pendingCalls = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        foreach (var message in parentMessages)
        {
            if (message.Role == "assistant")
                foreach (var call in message.ToolCalls ?? []) pendingCalls[call.Id] = call;
            else if (message.Role == "tool" && message.ToolCallId is not null)
                _ = pendingCalls.Remove(message.ToolCallId);
        }
        foreach (var call in pendingCalls.Values)
            messages.Add(new("tool", JsonSerializer.Serialize(new
                {
                    status = "parent_owned",
                    message = "Dieser Aufruf gehört zum Hauptagenten. Im Kontextzweig nicht erneut ausführen.",
                }), ToolCallId: call.Id));
        messages.Add(new("user", "MISSUM_SUBAGENT_AUFTRAG\n"
            + "Du bist der selbstständig arbeitende Subagent. Der vorangehende Verlauf ist dein gemeinsamer Kontextstand. "
            + "Führe ausschließlich die folgende vom Hauptagenten zugewiesene Teilaufgabe vollständig aus. "
            + "Du besitzt dieselben autorisierten Werkzeuge und Berechtigungen: systemweit Dateien lesen, "
            + "Dateien im übernommenen Workspace erstellen oder bearbeiten. Halte dich an die zugewiesenen Schreibpfade, "
            + "damit du und der Hauptagent parallel arbeiten können. Starte keine weiteren Subagenten. "
            + "Berichte dein tatsächlich abgeschlossenes Ergebnis, erzeugte Dateien, Werkzeugbelege und gegebenenfalls offene Punkte. "
            + "Der Hauptagent verwendet dein Ergebnis direkt ohne Wiederholung deiner abgeschlossenen Aufgabe.\n\n" + task));
        return messages;
    }

    private async Task<bool> CanOfferSubagentToolsAsync(RunRequest request, ModelSelection selection, CancellationToken cancellationToken)
    {
        if (request.ConversationProfile is ConversationProfile.ContextPreparation
            || request.ClientCapabilities?.Contains("subagents", StringComparer.OrdinalIgnoreCase) != true)
            return false;
        // Only the gateway can create this persisted relation. Inherit the
        // parent's schemas even if GPU1 has little free memory after loading.
        if (request.Subagent?.RuntimeInstanceId is not null) return true;
        try
        {
            var availability = await _modelRuntime.GetSubagentAvailabilityAsync(selection.ModelId, selection.ContextLength, cancellationToken).ConfigureAwait(false);
            if (!availability.Allowed)
                _runtime.WriteLog("Information", "subagent.unavailable", $"Modell {selection.ModelId}: {availability.Reason}");
            return availability.Allowed;
        }
        catch (HttpRequestException exception)
        {
            _runtime.WriteLog("Warning", "subagent.admission_unreachable", exception.Message);
            return false;
        }
    }

    private async Task<AgentToolExecutionResult> ExecuteSubagentToolAsync(string toolName, JsonElement arguments,
        string parentRunId, string operationId, RunRequest parentRequest, ModelSelection selection,
        List<LmChatMessage> parentMessages, CancellationToken cancellationToken)
    {
        if (parentRequest.Subagent is not null)
            return SubagentFailure("subagent.recursion_denied", "Der GPU1-Subagent kann keine weiteren Subagenten starten.");
        if (toolName == SubagentToolNames.Wait)
            return await WaitForSubagentAsync(parentRunId, arguments.GetProperty("runId").GetString()!, cancellationToken).ConfigureAwait(false);
        await _subagentSpawnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _repository.FindSubagentByOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is { } previous)
            {
                var previousRequest = await _repository.GetRequestAsync(previous.RunId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Der Subagentenauftrag fehlt.");
                await EnsureSubagentStartedAsync(previous, previousRequest, cancellationToken).ConfigureAwait(false);
                _ = StartSubagentRunner(previous.RunId, previousRequest);
                return SubagentSpawnReceipt(previous, previousRequest);
            }
            var active = await _repository.GetSubagentRunsAsync(activeOnly: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (active.Count > 0)
                return SubagentFailure("subagent.gpu_busy", "GPU1 bearbeitet bereits einen Subagenten. Arbeite weiter und warte auf dessen Ergebnis.");
            var availability = await _modelRuntime.GetSubagentAvailabilityAsync(selection.ModelId, selection.ContextLength, cancellationToken).ConfigureAwait(false);
            if (!availability.Allowed)
                return SubagentFailure("subagent.insufficient_vram", availability.Reason ?? "Das aktuelle Modell belegt die für den Subagenten benötigten GPU-Ressourcen.");
            var task = arguments.GetProperty("task").GetString()!;
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operationId))).ToLowerInvariant();
            var agentId = "subagent-" + identity[..16];
            var childSessionId = new Guid(Convert.FromHexString(identity[..32])).ToString();
            var parentCacheKey = BuildRunSessionCacheKey(parentRequest, parentRunId, selection.Role);
            var childRequest = parentRequest with
            {
                SessionId = childSessionId,
                PreferredGeneralModelId = selection.ModelId,
                PreferredCodingModelId = selection.ModelId,
                Workload = null,
                // Explicit delegated work uses the ordinary complete tool loop.
                // Starting a second staged research pipeline would change its assigned scope.
                DeepResearch = false,
                Subagent = new(parentRunId, agentId, task, parentRequest.SessionId, ParentSessionCacheKey: parentCacheKey),
                Messages = [.. parentRequest.Messages, new("user", [new("text", Text: task)])],
            };
            var childCacheKey = BuildRunSessionCacheKey(childRequest, agentId, selection.Role);
            var childWorkingState = childRequest.Mode == RunMode.Coding ? CodingWorkingState.Create(task) : null;
            var childMessages = ModelRuntimeClient.PrepareLanguageBoundMessages(
                WithWorkingState(ForkSubagentMessages(parentMessages, task), childWorkingState));
            var childTools = CreateModelToolDefinitions(_toolCatalog.GetAvailableTools(childRequest,
                subagentAvailable: true), selectedToolName: null, directTools: true);
            SubagentRuntimePreparation prepared;
            try
            {
                prepared = await _modelRuntime.PrepareSubagentAsync(selection.ModelId, parentCacheKey, childCacheKey,
                    childMessages, childTools, selection.Role,
                    _modelRuntime.ResolveReasoningEffort(selection.ModelId, selection.Role, childRequest.ReasoningEffort),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
            {
                return SubagentFailure("subagent.preparation_failed", exception.Message);
            }
            childRequest = childRequest with { Subagent = childRequest.Subagent! with { RuntimeInstanceId = prepared.InstanceId } };
            var checkpoint = new AgentRunCheckpoint(childMessages, 0, 0, 0, 0,
                WorkingState: childWorkingState, WorkingStatePromptIncluded: childWorkingState is not null,
                PreserveSessionPromptPrefix: true, SelectedModelId: selection.ModelId,
                SelectedReasoningEffort: childRequest.ReasoningEffort, UseStableSubagentToolCatalog: true);
            var child = await _repository.CreateSubagentAsync(childRequest, checkpoint, operationId, cancellationToken).ConfigureAwait(false);
            await EnsureSubagentStartedAsync(child, childRequest, cancellationToken).ConfigureAwait(false);
            await _repository.AppendEventAsync(child.RunId, "subagent.contextForked", new
            {
                parentRunId, parentCacheKey, childCacheKey, messageCount = parentMessages.Count,
                prepared.InstanceId, prepared.GpuIndex, prepared.CacheStatus, prepared.CachedTokens,
                prepared.SourceCachedTokens, prepared.PreparationSampledTokens, prepared.EvaluatedGeneratedTokens,
            }, cancellationToken).ConfigureAwait(false);
            _ = StartSubagentRunner(child.RunId, childRequest);
            return SubagentSpawnReceipt(child, childRequest);
        }
        finally { _subagentSpawnGate.Release(); }
    }

    private static AgentToolExecutionResult SubagentFailure(string code, string message) =>
        new(JsonSerializer.SerializeToElement(new { status = "failed", errorCode = code, message }), [],
            Succeeded: false, ErrorCode: code, ErrorMessage: message);

    private static AgentToolExecutionResult SubagentSpawnReceipt(RunSnapshot child, RunRequest request) =>
        new(JsonSerializer.SerializeToElement(new
        {
            status = "started", runId = child.RunId, agentId = request.Subagent!.AgentId,
            modelId = child.SelectedModel ?? request.PreferredGeneralModelId, gpuIndex = 1,
            task = request.Subagent.AssignedTask, sessionId = request.SessionId,
            instruction = "Arbeite parallel an deiner eigenen Aufgabe. Hole dieses Ergebnis mit subagent.wait ab und verwende es direkt, ohne die abgeschlossene Teilaufgabe erneut auszuführen oder zu prüfen.",
        }, MissumAiProtocol.CreateJsonOptions()), []);

    private async Task EnsureSubagentStartedAsync(RunSnapshot child, RunRequest request, CancellationToken cancellationToken)
    {
        var relation = request.Subagent!;
        await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentStarted,
            new(relation.ParentRunId, child.RunId, relation.AgentId, relation.AssignedTask,
                child.SelectedModel ?? request.PreferredGeneralModelId ?? request.PreferredCodingModelId ?? "", child.State,
                request.SessionId, child.ErrorCode), cancellationToken).ConfigureAwait(false);
    }

    private Task StartSubagentRunner(string runId, RunRequest request)
    {
        lock (_activeGate)
        {
            if (_subagentRuns.TryGetValue(runId, out var existing)) return existing.Completion;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_serviceStoppingToken);
            var completion = Task.Run(() => RunSubagentAsync(runId, request, cancellation), CancellationToken.None);
            _subagentRuns[runId] = new(request.Subagent!.ParentRunId, cancellation, completion);
            return completion;
        }
    }

    private async Task RunSubagentAsync(string runId, RunRequest request, CancellationTokenSource cancellation)
    {
        var relation = request.Subagent!;
        using var relayCancellation = new CancellationTokenSource();
        var relay = RelaySubagentEventsAsync(runId, relation, relayCancellation.Token);
        try
        {
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var snapshot = await _repository.GetAsync(runId, cancellation.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Der Subagentenlauf fehlt.");
                if (IsTerminalSubagentState(snapshot.State)) break;
                var parent = await _repository.GetAsync(relation.ParentRunId, cancellation.Token).ConfigureAwait(false);
                if (parent is null || IsTerminalSubagentState(parent.State))
                {
                    await MarkCancelledAsync(runId).ConfigureAwait(false);
                    break;
                }
                if (await _repository.GetProviderRetryTimeAsync(runId, cancellation.Token).ConfigureAwait(false) is { } retryTime
                    && retryTime > DateTimeOffset.UtcNow)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                var checkpoint = await _repository.GetCheckpointAsync(runId, cancellation.Token).ConfigureAwait(false);
                if (checkpoint?.PendingProposalId is { } pending
                    && await _repository.GetClientToolResultAsync(pending, cancellation.Token).ConfigureAwait(false) is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token).ConfigureAwait(false);
                    continue;
                }
                await _repository.UpdateStateAsync(runId, RunState.Running, checkpoint?.SelectedModelId,
                    cancellationToken: cancellation.Token).ConfigureAwait(false);
                try { await ProcessAsync(runId, cancellation.Token).ConfigureAwait(false); }
                catch (RunWaitingForClientException) { }
                catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
                {
                    if (!await TryScheduleProviderRetryAsync(runId, exception).ConfigureAwait(false)) throw;
                }
            }
        }
        catch (OperationCanceledException) when (_serviceStoppingToken.IsCancellationRequested)
        { await MarkInterruptedAsync(runId).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { await MarkCancelledAsync(runId).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { await MarkFailedAsync(runId, exception).ConfigureAwait(false); }
        finally
        {
            await relayCancellation.CancelAsync().ConfigureAwait(false);
            try { await relay.ConfigureAwait(false); }
            catch (OperationCanceledException) when (relayCancellation.IsCancellationRequested) { }
            await DrainSubagentEventsAsync(runId, relation, CancellationToken.None).ConfigureAwait(false);
            var final = await _repository.GetAsync(runId).ConfigureAwait(false);
            if (final is not null)
                await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted,
                    new(relation.ParentRunId, runId, relation.AgentId, relation.AssignedTask,
                        final.SelectedModel ?? request.PreferredGeneralModelId ?? request.PreferredCodingModelId ?? "",
                        final.State, request.SessionId, final.ErrorCode)).ConfigureAwait(false);
        }
    }

    private async Task RelaySubagentEventsAsync(string childRunId, SubagentRunContext relation, CancellationToken cancellationToken)
    {
        while (true)
        {
            await DrainSubagentEventsAsync(childRunId, relation, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainSubagentEventsAsync(string childRunId, SubagentRunContext relation, CancellationToken cancellationToken)
    {
        var cursor = await _repository.GetSubagentForwardCursorAsync(relation.ParentRunId, childRunId, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var page = await _repository.GetEventsPageAfterAsync(childRunId, cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                _ = await _repository.ForwardSubagentEventAsync(new(relation.AgentId, relation.ParentRunId, childRunId, item), cancellationToken).ConfigureAwait(false);
                cursor = item.Id;
            }
            if (page.Count < 256) break;
        }
    }

    private async Task<AgentToolExecutionResult> WaitForSubagentAsync(string parentRunId, string childRunId, CancellationToken cancellationToken)
    {
        var request = await _repository.GetRequestAsync(childRunId, cancellationToken).ConfigureAwait(false);
        if (request?.Subagent is not { } relation || relation.ParentRunId != parentRunId)
            return SubagentFailure("subagent.invalid_owner", "Dieser Subagent gehört nicht zum aufrufenden Hauptagenten.");
        var completion = StartSubagentRunner(childRunId, request);
        await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        var child = await _repository.GetAsync(childRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Subagentenlauf fehlt.");
        var text = CodingTextReconciler.Project(await _repository.GetVisibleTextEventsAsync(childRunId, cancellationToken: cancellationToken).ConfigureAwait(false));
        var events = await _repository.GetEventsAfterAsync(childRunId, 0, cancellationToken).ConfigureAwait(false);
        var completionEvidence = child.State == RunState.Completed
            ? await ReadSubagentCompletionEvidenceAsync(childRunId, events, cancellationToken).ConfigureAwait(false) : [];
        var artifactIds = events.Where(static item => item.Type == RunEventTypes.ArtifactCreated)
            .Select(static item => item.Data.TryGetProperty("artifactId", out var id) ? id.GetString() : null)
            .Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        return new(JsonSerializer.SerializeToElement(new
        {
            status = child.State == RunState.Completed ? "completed" : "failed",
            runId = childRunId, agentId = relation.AgentId, task = relation.AssignedTask,
            result = text, artifactIds, completionEvidence, errorCode = child.ErrorCode,
            instruction = "Übernimm das abgeschlossene Ergebnis und die bereits ausgeführten Dateiänderungen direkt. Wiederhole oder überprüfe die zugewiesene abgeschlossene Teilaufgabe nicht. Verarbeite nur ausdrücklich offene Punkte weiter.",
        }, MissumAiProtocol.CreateJsonOptions()), [], Succeeded: child.State == RunState.Completed,
            ErrorCode: child.State == RunState.Completed ? null : child.ErrorCode ?? "subagent.failed");
    }

    internal async Task<ScientificCompletionAssessment> AssessScientificCompletionAsync(string parentRunId,
        RunRequest request, IReadOnlyList<LmChatMessage> messages, IReadOnlyList<AgentToolSpec> tools,
        CancellationToken cancellationToken)
    {
        var evidence = new Dictionary<string, IReadOnlyList<LmChatMessage>>(StringComparer.Ordinal);
        foreach (var (child, childRequest) in await _repository.GetSubagentRunsAsync(parentRunId,
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            // The model's summary and any user-supplied JSON cannot establish
            // execution. Only a completed owned branch already committed to
            // the parent's checkpoint contributes its durable tool receipts.
            if (child.State != RunState.Completed || childRequest.Subagent?.ParentRunId != parentRunId
                || !await _repository.HasConsumedSubagentResultAsync(parentRunId, child.RunId, cancellationToken).ConfigureAwait(false))
                continue;
            var events = await _repository.GetEventsAfterAsync(child.RunId, 0, cancellationToken).ConfigureAwait(false);
            evidence[child.RunId] = await ReadSubagentCompletionEvidenceAsync(child.RunId, events, cancellationToken).ConfigureAwait(false);
        }
        return ScientificRunCompletionPolicy.Assess(request, messages, tools, evidence);
    }

    private async Task<List<LmChatMessage>> ReadSubagentCompletionEvidenceAsync(string childRunId,
        IReadOnlyList<RunEvent> events, CancellationToken cancellationToken)
    {
        var evidence = new List<LmChatMessage>();
        var serverCalls = new Dictionary<string, (string Tool, JsonElement Arguments)>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            if (item.RunId != childRunId) continue;
            if (item.Type == RunEventTypes.ClientToolProposed)
            {
                var proposal = item.Data.Deserialize<ToolProposal>(MissumAiProtocol.CreateJsonOptions());
                if (proposal is null || proposal.RunId != childRunId || !ScientificRunCompletionPolicy.IsEvidenceTool(proposal.Name)) continue;
                var result = await _repository.GetClientToolResultAsync(proposal.ProposalId, cancellationToken).ConfigureAwait(false);
                if (result is not null)
                    Add(proposal.Name, proposal.Arguments, SerializeClientToolResult(result), item.Id);
            }
            else if (item.Type == RunEventTypes.ServerToolStarted
                && item.Data.TryGetProperty("tool", out var startedTool)
                && startedTool.GetString() is { } tool && ScientificRunCompletionPolicy.IsEvidenceTool(tool)
                && item.Data.TryGetProperty("toolCallId", out var startedId) && startedId.GetString() is { } operationId
                && item.Data.TryGetProperty("arguments", out var arguments))
                serverCalls[operationId] = (tool, arguments.Clone());
            else if (item.Type == RunEventTypes.ServerToolCompleted
                && item.Data.TryGetProperty("toolCallId", out var completedId) && completedId.GetString() is { } completedOperation
                && serverCalls.Remove(completedOperation, out var call)
                && item.Data.TryGetProperty("result", out var result))
                Add(call.Tool, call.Arguments, JsonSerializer.Serialize(new
                {
                    status = item.Data.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True ? "completed" : "failed",
                    result,
                }, MissumAiProtocol.CreateJsonOptions()), item.Id);
        }
        return evidence;

        void Add(string tool, JsonElement arguments, string receipt, long eventId)
        {
            var id = "subagent-evidence-" + childRunId + "-" + eventId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            evidence.Add(new("assistant", ToolCalls: [new(id, tool, arguments.Clone())]));
            evidence.Add(new("tool", receipt, ToolCallId: id));
        }
    }

    private async Task<IReadOnlyList<JsonElement>> CollectOutstandingSubagentResultsAsync(string parentRunId, CancellationToken cancellationToken)
    {
        var children = await _repository.GetSubagentRunsAsync(parentRunId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<JsonElement>();
        foreach (var (child, _) in children)
            if (!await _repository.HasConsumedSubagentResultAsync(parentRunId, child.RunId, cancellationToken).ConfigureAwait(false))
                results.Add((await WaitForSubagentAsync(parentRunId, child.RunId, cancellationToken).ConfigureAwait(false)).Result);
        return results;
    }

    private async Task MarkSubagentResultsConsumedAsync(string parentRunId, IReadOnlyList<JsonElement> results,
        CancellationToken cancellationToken)
    {
        foreach (var result in results)
            if (result.TryGetProperty("runId", out var childId) && childId.GetString() is { } childRunId)
                _ = await _repository.TryConsumeSubagentResultAsync(parentRunId, childRunId, cancellationToken).ConfigureAwait(false);
    }

    internal static bool AppendSubagentResultAcceptanceInstruction(List<LmChatMessage> messages, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
            || status.GetString() != "completed"
            || !result.TryGetProperty("runId", out var id) || id.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(id.GetString())) return false;
        var marker = "[MISSUM_SUBAGENT_RESULT_ACCEPTED:" + id.GetString() + "]\n";
        // Append after the authentic receipt, never rewrite the evaluated
        // system prefix. Persisted identity prevents reinjection on restart
        // or a repeated wait without marking its result consumed a second time.
        if (messages.Any(message => message.Role == "system"
            && message.Content?.StartsWith(marker, StringComparison.Ordinal) == true)) return false;
        messages.Add(new LmChatMessage("system", marker + SubagentAgentPolicy.CompletedWork));
        return true;
    }
}

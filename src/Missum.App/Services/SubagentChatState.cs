using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>A session-scoped child transcript, persisted with its parent's delegation receipt.</summary>
public sealed partial record SubagentChatState(
    string AgentId, string RunId, string ParentRunId, Guid SessionId, string Title, string Status,
    ChatMessage UserMessage, ChatMessage AssistantMessage,
    long LastEventId = 0, string? Model = null, int? ContextUsed = null, int? ContextLimit = null,
    string? RunStatus = null, string? RunDetail = null, string? GenerationState = null,
    int? GeneratedTokens = null, DateTimeOffset? GenerationUpdatedAt = null,
    IReadOnlyList<ChatArtifact>? Artifacts = null, bool? ResultDelivered = null,
    IReadOnlyList<ChatMessage>? PreviousMessages = null, IReadOnlyList<string>? PreviousRunIds = null,
    long LifecycleEventId = 0, long ProjectionRevision = 0, string? StoredReceiptId = null,
    IReadOnlyDictionary<string, IReadOnlyList<ChatArtifact>>? PreviousMessageArtifacts = null,
    IReadOnlyList<string>? AuthorizedConsumerRunIds = null,
    int? ProcessedPromptTokens = null, int? TotalPromptTokens = null, double? PromptProgress = null,
    [property: JsonIgnore] bool TranscriptDeferred = false,
    [property: JsonIgnore] Guid? ParentMessageId = null,
    string Kind = "subagent", string? OriginToolStepId = null, long DeepResearchSequence = 0, Guid? OriginMessageId = null,
    long TranscriptRevision = 0, int? GpuIndex = null)
{
    internal const string ReceiptTool = "subagent";
    internal const string CompletionTool = "subagent.completed";
    internal string ReceiptId => StoredReceiptId ?? "subagent:" + AgentId + ":" + RunId;
    internal string CompletionReceiptId => "subagent-completed:" + AgentId + ":" + RunId;
    [JsonIgnore] internal IReadOnlyDictionary<Guid, ConversationToolStepWindow>? ToolStepWindows { get; init; }
    public bool IsRunning => Status is "queued" or "running" or "waitingForClient";
    [JsonIgnore] public bool IsLegacyDeepResearch => AgentId.StartsWith(DeepResearchAgentPrefix, StringComparison.Ordinal)
        || RunId.StartsWith("deep-research-run:", StringComparison.Ordinal);
    public string LifecycleText => TaskTitle(Title) + " hat die Arbeit begonnen";
    internal static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();

    internal static bool HasActiveRun(string? status, bool declaredRunning = false)
    {
        var normalized = status?.ToLowerInvariant();
        return normalized is not ("completed" or "failed" or "denied" or "cancelled" or "interrupted"
            or "disabled" or "unavailable" or "rejected")
            && (declaredRunning || normalized is "running" or "pending" or "queued" or "waiting" or "waitingforclient");
    }

    internal static bool OpenTabOnFirstObservation(string? status, bool declaredRunning, bool manuallyClosed) =>
        !manuallyClosed && HasActiveRun(status, declaredRunning);

    internal static bool AcceptSnapshot(string currentRunId, DateTimeOffset? currentStartedAt, long currentRevision,
        IEnumerable<string> previousRunIds, string incomingRunId, DateTimeOffset? incomingStartedAt, long incomingRevision)
    {
        if (currentRunId.Length == 0) return true;
        if (incomingRunId.Length == 0) return false;
        if (currentRunId == incomingRunId) return incomingRevision >= currentRevision;
        return !previousRunIds.Contains(incomingRunId, StringComparer.Ordinal)
            && (currentStartedAt is null || incomingStartedAt is { } started && started >= currentStartedAt);
    }

    internal static SubagentChatState Create(SubagentRunEvent data, Guid session, DateTimeOffset at)
    {
        var title = TaskTitle(data.Task);
        return new(data.AgentId, data.RunId, data.ParentRunId, session, title, StateName(data.State),
            new(StableId(data.RunId + ":user"), session, ChatRole.User, data.Task, MessageStatus.Completed, at, at),
            new(StableId(data.RunId + ":assistant"), session, ChatRole.Assistant, "", MessageStatus.Streaming, at, at),
            Model: data.ModelId, RunStatus: "Aufgabe übernommen", ResultDelivered: false,
            Kind: data.Specialization ?? "subagent", OriginToolStepId: data.OriginToolStepId, GpuIndex: data.GpuIndex);
    }

    internal SubagentChatState ObserveLifecycle(SubagentRunEvent data, long eventId, DateTimeOffset at, bool started)
    {
        if (data.AgentId != AgentId) return this;
        if (data.RunId != RunId)
        {
            // A resumed attempt is a new SSE stream. Its cursor and receipts must
            // never inherit the cancelled attempt's identities or terminal state.
            if (!started || at < AssistantMessage.CreatedAt || (PreviousRunIds ?? []).Contains(data.RunId, StringComparer.Ordinal))
                return this;
            var previousAssistant = IsRunning ? Finish(AssistantMessage, MessageStatus.Cancelled, at) : AssistantMessage;
            var previousArtifacts = (PreviousMessageArtifacts ?? new Dictionary<string, IReadOnlyList<ChatArtifact>>())
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            if (Artifacts is { Count: > 0 }) previousArtifacts[AssistantMessage.Id.ToString()] = Artifacts;
            return Create(data, SessionId, at) with
            {
                PreviousMessages = (PreviousMessages ?? []).Concat([UserMessage, previousAssistant]).ToArray(),
                PreviousRunIds = (PreviousRunIds ?? []).Append(RunId).Distinct(StringComparer.Ordinal).ToArray(),
                PreviousMessageArtifacts = previousArtifacts,
                LifecycleEventId = eventId,
                ProjectionRevision = ProjectionRevision,
                ParentMessageId = ParentMessageId, OriginMessageId = OriginMessageId,
            };
        }
        if (data.ParentRunId != ParentRunId || eventId <= LifecycleEventId) return this;
        var next = this with { Status = StateName(data.State), Model = data.ModelId, LifecycleEventId = eventId,
            Kind = data.Specialization ?? Kind, OriginToolStepId = data.OriginToolStepId ?? OriginToolStepId,
            GpuIndex = data.GpuIndex ?? GpuIndex };
        if (!next.IsRunning)
            next = next with { TranscriptRevision = next.TranscriptRevision + 1, AssistantMessage = Finish(next.AssistantMessage,
                data.State == RunState.Completed ? MessageStatus.Completed
                    : data.State == RunState.Cancelled ? MessageStatus.Cancelled : MessageStatus.Failed, at) };
        return next;
    }

    internal static string TaskTitle(string? task)
    {
        var line = task?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))?.Trim();
        if (string.IsNullOrWhiteSpace(line)) return "Subagent";
        line = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        for (var index = 12; index < line.Length; index++)
            if (line[index] is '.' or '!' or '?' && (index + 1 == line.Length || char.IsWhiteSpace(line[index + 1])))
            { line = line[..index]; break; }
        if (line.Length <= 100)
        {
            var title = line.TrimEnd('.', '!', '?');
            return title.Length == 0 ? "Subagent" : title;
        }
        var end = line.LastIndexOf(' ', 99, 40);
        if (end < 60) end = char.IsHighSurrogate(line[99]) ? 99 : 100;
        return line[..end].TrimEnd() + "…";
    }

    internal static string LifecycleDescription(string? status, bool? resultDelivered = null) => status?.ToLowerInvariant() switch
    {
        "completed" when resultDelivered is not false => "hat die Arbeit beendet",
        "failed" => "konnte die Arbeit nicht beenden",
        "cancelled" => "hat die Arbeit abgebrochen",
        "interrupted" => "hat die Arbeit unterbrochen",
        "disabled" or "unavailable" => "ist derzeit nicht verfügbar",
        _ => "hat die Arbeit begonnen",
    };

    internal bool IsAuthorizedConsumer(string parentRunId) => parentRunId == ParentRunId
        || (AuthorizedConsumerRunIds ?? []).Contains(parentRunId, StringComparer.Ordinal);

    internal SubagentChatState AdoptConsumer(RunEvent item, string parentRunId, Guid sessionId)
    {
        if (item.Type != "subagent.continuationState" || item.RunId != parentRunId || SessionId != sessionId
            || item.Data.ValueKind != JsonValueKind.Object || Text(item.Data, "sourceParentRunId") != ParentRunId
            || !item.Data.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array) return this;
        var confirmed = children.EnumerateArray().FirstOrDefault(child => child.ValueKind == JsonValueKind.Object
            && Text(child, "runId") == RunId && Text(child, "agentId") == AgentId
            && Text(child, "task") == UserMessage.Content);
        if (confirmed.ValueKind != JsonValueKind.Object) return this;
        var next = IsAuthorizedConsumer(parentRunId) ? this
            : this with { AuthorizedConsumerRunIds = (AuthorizedConsumerRunIds ?? []).Append(parentRunId).ToArray() };
        var actual = Text(confirmed, "state")?.ToLowerInvariant();
        if (actual is not ("completed" or "cancelled" or "interrupted" or "failed")) return next;
        var (messageStatus, runStatus) = actual switch
        {
            "completed" => (MessageStatus.Completed, "Abgeschlossen"),
            "cancelled" => (MessageStatus.Cancelled, "Gestoppt"),
            "interrupted" => (MessageStatus.Interrupted, "Unterbrochen"),
            _ => (MessageStatus.Failed, "Fehlgeschlagen"),
        };
        if (next.Status == actual && next.AssistantMessage.Status == messageStatus && next.RunStatus == runStatus && next.RunDetail is null
            && (next.GenerationState is null || next.GenerationState == actual)
            && !(next.AssistantMessage.ToolSteps ?? []).Any(step => step.Status == "running")) return next;
        // This ownership-checked gateway event reports the actual terminal state
        // even if shutdown prevented delivery of the old completion SSE. Keep
        // every received character; delivery is still confirmed separately.
        return next with
        {
            Status = actual, RunStatus = runStatus, RunDetail = null,
            TranscriptRevision = TranscriptRevision + 1,
            GenerationState = actual, GenerationUpdatedAt = item.CreatedAt,
            AssistantMessage = Finish(next.AssistantMessage, messageStatus, item.CreatedAt),
        };
    }

    internal SubagentChatState MarkResultDelivered(RunEvent item)
    {
        if (ResultDelivered is true || item.Type != "subagent.resultConsumed" || !IsAuthorizedConsumer(item.RunId) || item.Data.ValueKind != JsonValueKind.Object
            || Text(item.Data, "parentRunId") is { Length: > 0 } parent && parent != item.RunId || Text(item.Data, "agentId") != AgentId
            || Text(item.Data, "runId") != RunId || Text(item.Data, "state") != "completed"
            || Status != "completed" || AssistantMessage.Status != MessageStatus.Completed) return this;
        return this with { ResultDelivered = true };
    }

    internal AssistantToolStep? CompletionReceipt(int contentOffset, DateTimeOffset at) =>
        !IsLegacyDeepResearch && ResultDelivered is true && Status == "completed" && AssistantMessage.Status == MessageStatus.Completed
            ? new(CompletionReceiptId, CompletionTool, "completed", TaskTitle(Title) + " hat die Arbeit beendet",
                OutputJson: JsonSerializer.Serialize(new { AgentId, RunId, ParentRunId, SessionId, Title, Kind, OriginToolStepId,
                    OriginMessageId, GpuIndex, status = "completed", resultDelivered = true }, JsonOptions),
                ContentOffset: contentOffset, StartedAt: at, CompletedAt: at, UpdatedAt: at, AgentId: AgentId)
            : null;

    internal static IReadOnlyList<SubagentChatState> Read(IEnumerable<ChatMessage> messages, string? dataDirectory = null) => messages
        .SelectMany(message => message.ToolSteps ?? [])
        .Where(step => step.Tool == ReceiptTool && step.OutputJson is not null)
        .Select(step => DeserializeStored(step.OutputJson!, dataDirectory) is { } state
            ? state with { StoredReceiptId = step.Id } : null).OfType<SubagentChatState>()
        .GroupBy(state => state.AgentId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();

    // UI overview only: never touch external transcript files or deserialize a
    // nested child conversation just to draw a tab, avatar or lifecycle row.
    internal static IReadOnlyList<SubagentChatState> ReadSummaries(IEnumerable<ChatMessage> messages) => messages
        .SelectMany(message => (message.ToolSteps ?? []).Where(step => step.Tool == ReceiptTool && step.OutputJson is not null)
            .Select(step => ReadSummary(message, step))).OfType<SubagentChatState>()
        .GroupBy(state => state.AgentId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();

    private static SubagentChatState? ReadSummary(ChatMessage parent, AssistantToolStep receipt)
    {
        try
        {
            using var document = JsonDocument.Parse(receipt.OutputJson!);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "agentId") is not { Length: > 0 } agentId
                || Text(root, "runId") is not { Length: > 0 } runId) return null;
            if (Guid.TryParse(Text(root, "sessionId"), out var session) && session != parent.SessionId) return null;
            var status = Text(root, "status") ?? receipt.Status;
            var title = TaskTitle(Text(root, "title"));
            var at = parent.CreatedAt;
            if (DateTimeOffset.TryParse(Text(root, "attemptStartedAt"), out var started)) at = started;
            else if (root.TryGetProperty("assistantMessage", out var assistant) && assistant.ValueKind == JsonValueKind.Object
                && DateTimeOffset.TryParse(Text(assistant, "createdAt"), out started)) at = started;
            var messageStatus = status.ToLowerInvariant() switch
            {
                "completed" => MessageStatus.Completed, "cancelled" => MessageStatus.Cancelled,
                "interrupted" => MessageStatus.Interrupted, "failed" or "denied" => MessageStatus.Failed,
                _ => HasActiveRun(status) ? MessageStatus.Streaming : MessageStatus.Completed,
            };
            bool? delivered = root.TryGetProperty("resultDelivered", out var delivery)
                && delivery.ValueKind is JsonValueKind.True or JsonValueKind.False ? delivery.GetBoolean() : null;
            if (delivery.ValueKind == JsonValueKind.Number && delivery.TryGetInt32(out var deliveryNumber) && deliveryNumber is 0 or 1)
                delivered = deliveryNumber == 1;
            var revision = root.TryGetProperty("projectionRevision", out var projection)
                && projection.ValueKind == JsonValueKind.Number && projection.TryGetInt64(out var parsed) ? parsed : 0;
            return new(agentId, runId, Text(root, "parentRunId") ?? "", parent.SessionId, title, status,
                new(StableId(runId + ":user"), parent.SessionId, ChatRole.User, "", MessageStatus.Completed, at, at),
                new(StableId(runId + ":assistant"), parent.SessionId, ChatRole.Assistant, "", messageStatus, at, at),
                ResultDelivered: delivered, ProjectionRevision: revision, StoredReceiptId: receipt.Id,
                TranscriptDeferred: true, ParentMessageId: parent.Id,
                Kind: Text(root, "kind") ?? (agentId.StartsWith(DeepResearchAgentPrefix, StringComparison.Ordinal) ? DeepResearchKind : "subagent"),
                OriginToolStepId: Text(root, "originToolStepId"),
                OriginMessageId: Guid.TryParse(Text(root, "originMessageId"), out var originMessage) ? originMessage : null,
                TranscriptRevision: root.TryGetProperty("transcriptRevision", out var transcriptRevision)
                    && transcriptRevision.ValueKind == JsonValueKind.Number
                    && transcriptRevision.TryGetInt64(out var parsedTranscriptRevision) ? parsedTranscriptRevision : 0,
                GpuIndex: root.TryGetProperty("gpuIndex", out var gpuIndex) && gpuIndex.ValueKind == JsonValueKind.Number
                    && gpuIndex.TryGetInt32(out var parsedGpu) ? parsedGpu : null);
        }
        catch (JsonException) { return null; }
    }

    internal static async Task<SubagentChatState?> ReadAsync(ChatMessage parentMessage, string agentId,
        string? dataDirectory = null, CancellationToken cancellationToken = default)
    {
        foreach (var receipt in (parentMessage.ToolSteps ?? []).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipt.Tool != ReceiptTool || receipt.OutputJson is null
                || ReadSummary(parentMessage, receipt)?.AgentId != agentId) continue;
            var state = await DeserializeStoredAsync(receipt.OutputJson, dataDirectory, cancellationToken).ConfigureAwait(false);
            if (state is not null && state.AgentId == agentId && state.SessionId == parentMessage.SessionId)
                return state with { StoredReceiptId = receipt.Id, ParentMessageId = parentMessage.Id };
        }
        return null;
    }

    internal AssistantToolStep? FindToolStep(Guid messageId, string stepId) =>
        (PreviousMessages ?? []).Concat([UserMessage, AssistantMessage]).FirstOrDefault(message => message.Id == messageId)
            ?.ToolSteps?.FirstOrDefault(step => step.Id == stepId);

    internal SubagentChatState ForDisplay() => ToolStepWindows is not null ? this : this with
    {
        ToolStepWindows = (PreviousMessages ?? []).Concat([UserMessage, AssistantMessage])
            .Where(message => message.ToolSteps is { Count: > 0 }).ToDictionary(message => message.Id, message =>
                new ConversationToolStepWindow(message.ToolSteps!.Count, message.ToolSteps.Count > 64,
                    message.ToolSteps.Count > 64 ? message.ToolSteps.Count - 64 : null)),
        PreviousMessages = PreviousMessages?.Select(ProjectMessage).ToArray(),
        UserMessage = ProjectMessage(UserMessage), AssistantMessage = ProjectMessage(AssistantMessage),
    };

    private static ChatMessage ProjectMessage(ChatMessage message)
    {
        // Only compact activity metadata reaches the display. Stored transcripts
        // retain their original reasoning without copying it on every UI update.
        return message with
        {
            ToolExecution = null, ContextSummary = null,
            ToolSteps = message.ToolSteps?.TakeLast(64).Select(step => step.Tool is "assistant.narration" or "assistant.progress"
                ? step : step with
            {
                DisplayHeader = AssistantToolStepHeaders.Create(step),
                Detail = null, InputJson = null, OutputJson = null, PreviewHtml = null,
                Explanation = step.Explanation is { Length: > 400 } explanation ? explanation[..400] : step.Explanation,
                DetailsDeferred = true,
            }).ToArray(),
        };
    }

    internal ConversationToolStepPage? GetToolStepPage(Guid messageId, int pageSize = 64, long? beforeOrdinal = null)
    {
        var message = (PreviousMessages ?? []).Concat([UserMessage, AssistantMessage]).FirstOrDefault(item => item.Id == messageId);
        if (message is null) return null;
        var steps = message.ToolSteps ?? [];
        var end = (int)Math.Clamp(beforeOrdinal ?? steps.Count, 0, steps.Count);
        var start = Math.Max(0, end - Math.Clamp(pageSize, 1, 64));
        var display = ProjectMessage(message with { ToolSteps = steps.Skip(start).Take(end - start).ToArray() });
        return new(messageId, display.ToolSteps ?? [], new(steps.Count, start > 0, start));
    }

    private static async Task<SubagentChatState?> DeserializeStoredAsync(string json, string? dataDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("subagentStorageKey", out var keyValue)) return Deserialize(json);
            if (dataDirectory is null || keyValue.ValueKind != JsonValueKind.String) return null;
            var key = keyValue.GetString();
            if (key is not { Length: 64 } || key.Any(character => !char.IsAsciiHexDigit(character))) return null;
            var state = await ReadTranscriptAsync(Path.Combine(dataDirectory, "Subagents", key + ".json"), cancellationToken).ConfigureAwait(false);
            return state?.StorageKey == key ? state : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    internal async Task<string> PersistAsync(string dataDirectory)
    {
        // Small transcripts remain compatible inline. Growing conversations use bounded append-only patches.
        if (CanRemainInline()) return JsonSerializer.Serialize(this, JsonOptions);
        var key = StorageKey;
        var directory = Path.Combine(dataDirectory, "Subagents");
        Directory.CreateDirectory(directory);
        await PersistTranscriptAsync(Path.Combine(directory, key + ".json")).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { subagentStorageKey = key, agentId = AgentId, runId = RunId,
            ParentRunId, SessionId, title = Title, status = Status, resultDelivered = ResultDelivered,
            ProjectionRevision, attemptStartedAt = AssistantMessage.CreatedAt, Kind, OriginToolStepId, OriginMessageId, TranscriptRevision, GpuIndex }, JsonOptions);
    }

    private string StorageKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SessionId + ":" + RunId + ":" + AgentId))).ToLowerInvariant();

    private static SubagentChatState? DeserializeStored(string json, string? dataDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("subagentStorageKey", out var keyValue)) return Deserialize(json);
            if (dataDirectory is null || keyValue.ValueKind != JsonValueKind.String) return null;
            var key = keyValue.GetString();
            if (key is not { Length: 64 } || key.Any(character => !char.IsAsciiHexDigit(character))) return null;
            var state = ReadTranscriptAsync(Path.Combine(dataDirectory, "Subagents", key + ".json"), CancellationToken.None).GetAwaiter().GetResult();
            return state?.StorageKey == key ? state : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static SubagentChatState? Deserialize(string json, JsonSerializerOptions? options = null)
    {
        try
        {
            var state = JsonSerializer.Deserialize<SubagentChatState>(json, options ?? JsonOptions);
            return state is not null && !string.IsNullOrWhiteSpace(state.AgentId) && !string.IsNullOrWhiteSpace(state.RunId)
                && !string.IsNullOrWhiteSpace(state.ParentRunId) && state.SessionId != Guid.Empty
                && state.UserMessage is { Role: ChatRole.User } user && state.AssistantMessage is { Role: ChatRole.Assistant } assistant
                && user.SessionId == state.SessionId && assistant.SessionId == state.SessionId
                && user.Content is not null && assistant.Content is not null ? state : null;
        }
        catch (JsonException) { return null; }
    }

    internal SubagentChatState Apply(RunEvent item)
    {
        if (item.RunId != RunId || item.Id <= LastEventId) return this;
        var next = this with { LastEventId = item.Id };
        var message = AssistantMessage;
        switch (item.Type)
        {
            case RunEventTypes.TextDelta:
                message = message with { Content = MissumAiAssistantService.ApplyTextDelta(message.Content, item.Data.Deserialize<TextDeltaEvent>(JsonOptions)) };
                break;
            case RunEventTypes.ReasoningDelta:
                if (item.Data.Deserialize<ReasoningDeltaEvent>(JsonOptions) is { } reasoning)
                {
                    var id = $"reasoning-{RunId}-{reasoning.Phase}-{reasoning.Round}";
                    var previous = message.ToolSteps?.FirstOrDefault(step => step.Id == id);
                    var step = MissumAiAssistantService.ApplyReasoningDelta(previous, reasoning, id, item.Id, message.Content.Length, item.CreatedAt);
                    if (step is not null) message = AddStep(message, step);
                }
                break;
            case RunEventTypes.ModelSelected:
                next = next with { Model = item.Data.Deserialize<ModelSelectedEvent>(JsonOptions)?.ModelId ?? Model };
                break;
            case RunEventTypes.ModelLoading:
                var loading = item.Data.Deserialize<ModelLoadingEvent>(JsonOptions);
                next = next with { ContextLimit = loading?.EffectiveContextLength ?? ContextLimit, RunStatus = loading?.State == "loaded" ? "Denke nach" : "Modell wird geladen" };
                break;
            case RunEventTypes.ContextChanged:
                var context = item.Data.Deserialize<ContextChangedEvent>(JsonOptions);
                next = next with { ContextUsed = context?.EstimatedInputTokens ?? ContextUsed, ContextLimit = context?.ContextLimit ?? ContextLimit };
                if (context?.WasCompacted == true)
                {
                    var prior = message.ToolSteps?.LastOrDefault(step => step.Tool == "context.compact" && step.Status == "running");
                    message = AddStep(message, new(prior?.Id ?? $"context-compaction:{RunId}:{item.Id}", "context.compact", "completed",
                        context.Detail, ContentOffset: prior?.ContentOffset ?? message.Content.Length, StartedAt: prior?.StartedAt ?? item.CreatedAt,
                        CompletedAt: item.CreatedAt, UpdatedAt: item.CreatedAt));
                }
                break;
            case RunEventTypes.ModelGeneration:
                var generation = item.Data.Deserialize<ModelGenerationEvent>(JsonOptions);
                if (generation?.State == "codingCompacting" && message.ToolSteps?.Any(step => step.Tool == "context.compact" && step.Status == "running") != true)
                    message = AddStep(message, new($"context-compaction:{RunId}:{item.Id}", "context.compact", "running",
                        "Der Arbeitskontext wird verdichtet.", ContentOffset: message.Content.Length, StartedAt: item.CreatedAt, UpdatedAt: item.CreatedAt));
                next = next with { GeneratedTokens = generation?.GeneratedTokens ?? GeneratedTokens, GenerationState = generation?.State,
                    GenerationUpdatedAt = item.CreatedAt, RunStatus = generation?.State == "promptProcessing" ? "Kontext wird verarbeitet" : "Modell generiert",
                    ContextUsed = generation?.CurrentTokens ?? ContextUsed,
                    ProcessedPromptTokens = generation?.ProcessedPromptTokens ?? (generation?.State == GenerationState ? ProcessedPromptTokens : null),
                    TotalPromptTokens = generation?.PromptTokens ?? (generation?.State == GenerationState ? TotalPromptTokens : null),
                    PromptProgress = generation?.PromptProgress ?? (generation?.State == GenerationState
                        && generation?.ProcessedPromptTokens is not >= 0 && generation?.PromptTokens is not >= 0 ? PromptProgress : null) };
                break;
            case RunEventTypes.ClientToolProposed:
                if (item.Data.Deserialize<ToolProposal>(JsonOptions) is { } proposal)
                    message = AddStep(message, new(proposal.ProposalId, proposal.Name, "running", proposal.Summary,
                        InputJson: proposal.Arguments.GetRawText(), Explanation: proposal.Summary, ContentOffset: message.Content.Length,
                        StartedAt: item.CreatedAt, UpdatedAt: item.CreatedAt, AgentId: AgentId));
                next = next with { RunStatus = "Werkzeug arbeitet" };
                break;
            case RunEventTypes.ServerToolStarted:
            case RunEventTypes.ServerToolCompleted:
                var tool = Text(item.Data, "tool") ?? "Werkzeug";
                var idServer = Text(item.Data, "callId") ?? Text(item.Data, "toolCallId")
                    ?? message.ToolSteps?.LastOrDefault(step => step.Tool == tool && step.Status == "running")?.Id ?? $"{RunId}:{item.Id}";
                var started = item.Type == RunEventTypes.ServerToolStarted;
                var success = !item.Data.TryGetProperty("success", out var succeeded) || succeeded.ValueKind != JsonValueKind.False;
                var result = item.Data.TryGetProperty("result", out var output) ? output : item.Data;
                message = AddStep(message, new(idServer, tool, started ? "running" : success ? "completed" : "failed",
                    started ? Text(item.Data, "target") : MissumAiAssistantService.FormatToolResultDetail(result),
                    InputJson: item.Data.TryGetProperty("arguments", out var input) ? input.GetRawText() : null,
                    OutputJson: started ? null : result.GetRawText(), Explanation: MissumAiAssistantService.DescribeServerTool(tool, Text(item.Data, "target")),
                    ContentOffset: message.ToolSteps?.FirstOrDefault(step => step.Id == idServer)?.ContentOffset ?? message.Content.Length,
                    StartedAt: item.CreatedAt, CompletedAt: started ? null : item.CreatedAt,
                    UpdatedAt: item.CreatedAt, AgentId: AgentId));
                break;
            case RunEventTypes.RunCompleted:
                next = next with { Status = "completed", RunStatus = "Abgeschlossen" };
                message = Finish(message, MessageStatus.Completed, item.CreatedAt);
                break;
            case RunEventTypes.RunFailed:
                next = next with { Status = "failed", RunStatus = "Fehlgeschlagen" };
                message = Finish(message with { Error = item.Data.Deserialize<RunFailedEvent>(JsonOptions)?.Message }, MessageStatus.Failed, item.CreatedAt);
                break;
            case RunEventTypes.RunCancelled:
                next = next with { Status = "cancelled", RunStatus = "Gestoppt" };
                message = Finish(message, MessageStatus.Cancelled, item.CreatedAt);
                break;
        }
        return ReferenceEquals(message, AssistantMessage) ? next : next with
        {
            TranscriptRevision = TranscriptRevision + 1,
            AssistantMessage = message with { UpdatedAt = item.CreatedAt, Revision = AssistantMessage.Revision + 1 },
        };
    }

    internal static ChatMessage AddStep(ChatMessage message, AssistantToolStep incoming)
    {
        var steps = (message.ToolSteps ?? []).ToList();
        var index = steps.FindIndex(step => step.Id == incoming.Id);
        if (index < 0) steps.Add(incoming); else steps[index] = AssistantToolStep.Merge(steps[index], incoming);
        return message with { ToolSteps = steps };
    }

    internal static ChatMessage Finish(ChatMessage message, MessageStatus status, DateTimeOffset at) => message with
    {
        // Parent lifecycle receipts can arrive with a clock older than the last
        // child token. The revision makes their terminal tool states authoritative.
        Status = status, UpdatedAt = at, Revision = message.Revision + 1,
        ToolSteps = (message.ToolSteps ?? []).Select(step => step.Status == "running" ? step with
            { Status = status == MessageStatus.Completed ? "completed" : status.ToString().ToLowerInvariant(), CompletedAt = at, UpdatedAt = at } : step).ToArray(),
    };

    internal static string StateName(RunState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
    private static string? Text(JsonElement data, string property) => data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

/// <summary>Retains explicit tab closures without reopening historical transcripts.</summary>
internal sealed class SubagentTabStateStore
{
    private static readonly ConcurrentDictionary<string, SharedState> States = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SharedState _state;
    internal string StoragePath { get; }

    internal SubagentTabStateStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        StoragePath = Path.GetFullPath(Path.Combine(dataDirectory, "SubagentTabs.json"));
        _state = States.GetOrAdd(StoragePath, static _ => new SharedState());
        lock (_state.Gate)
        {
            if (_state.Loaded) return;
            foreach (var key in ReadClosedTabs(StoragePath)) _state.ClosedTabs.Add(key);
            _state.Loaded = true;
        }
    }

    internal bool WasManuallyClosed(Guid sessionId, string agentId)
    {
        lock (_state.Gate) return _state.ClosedTabs.Contains(Key(sessionId, agentId));
    }

    internal void Close(Guid sessionId, string agentId) => SetClosed(sessionId, agentId, closed: true);
    internal void Open(Guid sessionId, string agentId) => SetClosed(sessionId, agentId, closed: false);

    private static string Key(Guid sessionId, string agentId) => sessionId.ToString("N") + ":" + agentId;

    private void SetClosed(Guid sessionId, string agentId, bool closed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        lock (_state.Gate)
        {
            var key = Key(sessionId, agentId);
            if (!(closed ? _state.ClosedTabs.Add(key) : _state.ClosedTabs.Remove(key))) return;
            // Per-profile windows share this state. Background snapshots only
            // read memory; filesystem writes happen on an explicit open/close.
            var snapshot = new Snapshot(1, _state.ClosedTabs.Order(StringComparer.Ordinal).ToArray());
            var temporary = StoragePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, snapshot, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, StoragePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Cannot save subagent tab closures at {0}: {1}", StoragePath, exception.Message); }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { Trace.TraceWarning("Cannot remove temporary subagent tab closure file {0}: {1}", temporary, exception.Message); }
            }
        }
    }

    private static string[] ReadClosedTabs(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4 * 1024 * 1024) return [];
            using var stream = File.OpenRead(path);
            var snapshot = JsonSerializer.Deserialize<Snapshot>(stream, JsonOptions);
            return snapshot is { Version: 1, ClosedTabs: not null }
                ? snapshot.ClosedTabs.Where(key => key is { Length: > 33 } && key[32] == ':'
                    && Guid.TryParseExact(key[..32], "N", out _) && !string.IsNullOrWhiteSpace(key[33..])).ToArray()
                : [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Cannot read subagent tab closures at {0}: {1}", path, exception.Message);
            return [];
        }
    }

    private sealed record Snapshot(int Version, string[] ClosedTabs);
    private sealed class SharedState
    {
        internal object Gate { get; } = new();
        internal HashSet<string> ClosedTabs { get; } = new(StringComparer.Ordinal);
        internal bool Loaded { get; set; }
    }
}

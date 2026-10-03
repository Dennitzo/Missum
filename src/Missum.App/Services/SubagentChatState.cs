using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>A session-scoped child transcript, persisted with its parent's delegation receipt.</summary>
public sealed record SubagentChatState(
    string AgentId, string RunId, string ParentRunId, Guid SessionId, string Title, string Status,
    ChatMessage UserMessage, ChatMessage AssistantMessage,
    long LastEventId = 0, string? Model = null, int? ContextUsed = null, int? ContextLimit = null,
    string? RunStatus = null, string? RunDetail = null, string? GenerationState = null,
    int? GeneratedTokens = null, DateTimeOffset? GenerationUpdatedAt = null,
    IReadOnlyList<ChatArtifact>? Artifacts = null, bool? ResultDelivered = null)
{
    internal const string ReceiptTool = "subagent";
    internal const string CompletionTool = "subagent.completed";
    internal string ReceiptId => "subagent:" + AgentId;
    internal string CompletionReceiptId => "subagent-completed:" + AgentId;
    public bool IsRunning => Status is "queued" or "running" or "waitingForClient";
    public string LifecycleText => TaskTitle(Title) + " hat die Arbeit begonnen";
    internal static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();

    internal static SubagentChatState Create(SubagentRunEvent data, Guid session, DateTimeOffset at)
    {
        var title = TaskTitle(data.Task);
        return new(data.AgentId, data.RunId, data.ParentRunId, session, title, StateName(data.State),
            new(StableId(data.RunId + ":user"), session, ChatRole.User, data.Task, MessageStatus.Completed, at, at),
            new(StableId(data.RunId + ":assistant"), session, ChatRole.Assistant, "", MessageStatus.Streaming, at, at),
            Model: data.ModelId, RunStatus: "Aufgabe übernommen", ResultDelivered: false);
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

    internal SubagentChatState MarkResultDelivered(RunEvent item)
    {
        if (ResultDelivered is true || item.Type != "subagent.resultConsumed" || item.RunId != ParentRunId || item.Data.ValueKind != JsonValueKind.Object
            || Text(item.Data, "parentRunId") is { Length: > 0 } parent && parent != ParentRunId || Text(item.Data, "agentId") != AgentId
            || Text(item.Data, "runId") != RunId || Text(item.Data, "state") != "completed"
            || Status != "completed" || AssistantMessage.Status != MessageStatus.Completed) return this;
        return this with { ResultDelivered = true };
    }

    internal AssistantToolStep? CompletionReceipt(int contentOffset, DateTimeOffset at) =>
        ResultDelivered is true && Status == "completed" && AssistantMessage.Status == MessageStatus.Completed
            ? new(CompletionReceiptId, CompletionTool, "completed", TaskTitle(Title) + " hat die Arbeit beendet",
                OutputJson: JsonSerializer.Serialize(new { AgentId, RunId, ParentRunId, SessionId, Title, status = "completed", resultDelivered = true }, JsonOptions),
                ContentOffset: contentOffset, StartedAt: at, CompletedAt: at, UpdatedAt: at, AgentId: AgentId)
            : null;

    internal static IReadOnlyList<SubagentChatState> Read(IEnumerable<ChatMessage> messages, string? dataDirectory = null) => messages
        .SelectMany(message => message.ToolSteps ?? [])
        .Where(step => step.Tool == ReceiptTool && step.OutputJson is not null)
        .Select(step => DeserializeStored(step.OutputJson!, dataDirectory)).OfType<SubagentChatState>()
        .GroupBy(state => state.AgentId, StringComparer.Ordinal).Select(group => group.Last()).ToArray();

    internal async Task<string> PersistAsync(string dataDirectory)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        if (json.Length < AssistantToolStep.MaximumStructuredJsonCharacters - 1024) return json;
        // Large transcripts remain lossless without exceeding the repository's tool-result display limit.
        var key = StorageKey;
        var directory = Path.Combine(dataDirectory, "Subagents");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false)).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return JsonSerializer.Serialize(new { subagentStorageKey = key, agentId = AgentId, runId = RunId, title = Title, status = Status, resultDelivered = ResultDelivered }, JsonOptions);
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
            var state = Deserialize(File.ReadAllText(Path.Combine(dataDirectory, "Subagents", key + ".json")));
            return state?.StorageKey == key ? state : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static SubagentChatState? Deserialize(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<SubagentChatState>(json, JsonOptions);
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
        var message = AssistantMessage with { UpdatedAt = item.CreatedAt, Revision = AssistantMessage.Revision + 1 };
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
                break;
            case RunEventTypes.ModelGeneration:
                var generation = item.Data.Deserialize<ModelGenerationEvent>(JsonOptions);
                next = next with { GeneratedTokens = generation?.GeneratedTokens ?? GeneratedTokens, GenerationState = generation?.State,
                    GenerationUpdatedAt = item.CreatedAt, RunStatus = "Modell generiert", ContextUsed = generation?.CurrentTokens ?? ContextUsed };
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
        return next with { AssistantMessage = message };
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
        Status = status, UpdatedAt = at,
        ToolSteps = (message.ToolSteps ?? []).Select(step => step.Status == "running" ? step with
            { Status = status == MessageStatus.Completed ? "completed" : status.ToString().ToLowerInvariant(), CompletedAt = at, UpdatedAt = at } : step).ToArray(),
    };

    internal static string StateName(RunState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
    private static string? Text(JsonElement data, string property) => data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static Guid StableId(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial record SubagentChatState
{
    internal const string DeepResearchKind = "deepResearch";
    internal const string DeepResearchAgentPrefix = "deep-research:";

    internal static string DeepResearchAgentId(Guid parentMessageId, string operationId) => DeepResearchAgentPrefix
        + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parentMessageId.ToString("D") + ":" + operationId)))[..32].ToLowerInvariant();

    internal static SubagentChatState CreateDeepResearch(ChatMessage parent, string parentRunId, string operationId,
        string? task, DateTimeOffset at)
    {
        var agent = DeepResearchAgentId(parent.Id, operationId);
        var run = "deep-research-run:" + agent[DeepResearchAgentPrefix.Length..];
        var title = string.IsNullOrWhiteSpace(task) ? "Deep Research" : TaskTitle(task);
        return new(agent, run, parentRunId, parent.SessionId, title, "running",
            new(StableId(run + ":user"), parent.SessionId, ChatRole.User, task ?? "Deep Research", MessageStatus.Completed, at, at),
            new(StableId(run + ":assistant"), parent.SessionId, ChatRole.Assistant, "", MessageStatus.Streaming, at, at),
            RunStatus: "Recherche gestartet", ResultDelivered: false, ParentMessageId: parent.Id,
            Kind: DeepResearchKind, OriginToolStepId: operationId, OriginMessageId: parent.Id);
    }

    internal SubagentChatState ApplyDeepResearchProgress(RunEvent item, DeepResearchProgressEvent progress)
    {
        if (Kind != DeepResearchKind || item.RunId != ParentRunId || progress.ParentRunId != ParentRunId
            || progress.OperationId != OriginToolStepId || item.Id <= LastEventId || progress.Sequence <= DeepResearchSequence)
            return this;
        var phase = progress.Phase;
        var id = "deep-research-step:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            OriginToolStepId + ":" + (progress.StepId ?? phase))))[..32].ToLowerInvariant();
        var previous = AssistantMessage.ToolSteps?.FirstOrDefault(step => step.Id == id);
        var status = progress.Status is "completed" or "failed" or "cancelled" or "interrupted" ? progress.Status : "running";
        // Counters and prefill/generation states are metadata, not new transcript
        // content. Keep the original action header and hydrated tool details
        // stable while the same model call emits its throttled progress ticks.
        var tokenOnly = previous is not null && previous.Status == status && phase == "model"
            && progress.ModelProgress is not null && status == "running"
            && progress.Detail is null && progress.Text is null && progress.Query is null && progress.Url is null
            && progress.Sources is null && progress.ErrorCode is null;
        var detail = new StringBuilder(progress.Detail ?? progress.Title);
        if (!string.IsNullOrWhiteSpace(progress.Query)) detail.Append("\nSuchanfrage: ").Append(progress.Query);
        if (!string.IsNullOrWhiteSpace(progress.Url)) detail.Append("\nAdresse: ").Append(progress.Url);
        if (progress.Completed is { } completed && progress.Total is { } total)
            detail.Append("\nFortschritt: ").Append(completed).Append(" / ").Append(total);
        if (!string.IsNullOrWhiteSpace(progress.Text)) detail.Append("\n\n").Append(progress.Text);
        var modelProgress = progress.ModelProgress;
        var output = JsonSerializer.Serialize(new { progress.Phase, progress.Status, progress.Sources, progress.ErrorCode }, JsonOptions);
        var step = new AssistantToolStep(id, progress.Tool ?? "research." + phase, status, detail.ToString(),
            InputJson: JsonSerializer.Serialize(new { progress.Query, progress.Url }, JsonOptions), OutputJson: output,
            Explanation: progress.Title, ContentOffset: previous?.ContentOffset ?? AssistantMessage.Content.Length,
            StartedAt: previous?.StartedAt ?? item.CreatedAt, CompletedAt: status == "running" ? null : item.CreatedAt,
            UpdatedAt: item.CreatedAt, AgentId: AgentId);
        var message = tokenOnly ? AssistantMessage : AddStep(AssistantMessage, step) with { UpdatedAt = item.CreatedAt };
        return this with
        {
            AssistantMessage = message, LastEventId = item.Id, DeepResearchSequence = progress.Sequence,
            TranscriptRevision = tokenOnly ? TranscriptRevision : TranscriptRevision + 1,
            Model = progress.ModelId ?? Model, RunStatus = progress.Title, RunDetail = progress.Detail,
            ContextUsed = modelProgress?.CurrentTokens is > 0 ? modelProgress.CurrentTokens
                : modelProgress?.PromptTokens is > 0 ? modelProgress.PromptTokens + Math.Max(0, modelProgress.GeneratedTokens ?? 0) : ContextUsed,
            GenerationState = modelProgress?.State ?? GenerationState,
            GeneratedTokens = modelProgress?.GeneratedTokens ?? GeneratedTokens,
            ProcessedPromptTokens = modelProgress?.ProcessedPromptTokens ?? ProcessedPromptTokens,
            TotalPromptTokens = modelProgress?.PromptTokens ?? TotalPromptTokens,
            PromptProgress = modelProgress?.PromptProgress ?? PromptProgress,
            GenerationUpdatedAt = modelProgress is null ? GenerationUpdatedAt : item.CreatedAt,
        };
    }

    internal SubagentChatState CompleteDeepResearch(RunEvent item, JsonElement result, string status)
    {
        if (Kind != DeepResearchKind || item.RunId != ParentRunId || item.Id < LastEventId
            || item.Id == LastEventId && Status == status && AssistantMessage.Status is not (MessageStatus.Pending or MessageStatus.Streaming)) return this;
        var content = DeepResearchResultText(result);
        if (content.Length == 0) content = status == "completed" ? "Die Recherche ist abgeschlossen." : "Die Recherche wurde nicht vollständig abgeschlossen.";
        var id = "deep-research-result:" + AgentId[DeepResearchAgentPrefix.Length..];
        var previous = AssistantMessage.ToolSteps?.FirstOrDefault(step => step.Id == id);
        var message = AddStep(AssistantMessage, new(id, "web.deepResearch", status, content,
            OutputJson: result.ValueKind == JsonValueKind.Undefined ? null : result.GetRawText(),
            Explanation: "Rechercheergebnis", ContentOffset: previous?.ContentOffset ?? AssistantMessage.Content.Length,
            StartedAt: previous?.StartedAt ?? AssistantMessage.CreatedAt, CompletedAt: item.CreatedAt,
            UpdatedAt: item.CreatedAt, AgentId: AgentId));
        return this with
        {
            Status = status, LastEventId = Math.Max(item.Id, LastEventId), ResultDelivered = status == "completed",
            TranscriptRevision = TranscriptRevision + 1,
            RunStatus = status == "completed" ? "Recherche abgeschlossen" : "Recherche unvollständig",
            RunDetail = null, GenerationState = status,
            AssistantMessage = Finish(message with { Content = content }, status == "completed" ? MessageStatus.Completed
                : status == "cancelled" ? MessageStatus.Cancelled : status == "interrupted" ? MessageStatus.Interrupted : MessageStatus.Failed, item.CreatedAt),
        };
    }

    internal static SubagentChatState FromDeepResearchTool(ChatMessage parent, AssistantToolStep original)
    {
        var state = CreateDeepResearch(parent, "historical:" + parent.Id.ToString("N"), original.Id,
            DeepResearchTask(original), original.StartedAt ?? parent.CreatedAt);
        var status = original.Status;
        if (status == "running" && parent.Status is not (MessageStatus.Pending or MessageStatus.Streaming))
            status = parent.Status == MessageStatus.Cancelled ? "cancelled" : parent.Status == MessageStatus.Failed ? "failed" : "interrupted";
        var at = original.CompletedAt ?? original.UpdatedAt ?? parent.UpdatedAt;
        if (status == "running") return state;
        JsonElement result = default;
        if (original.OutputJson is { } output)
            try
            {
                using var json = JsonDocument.Parse(output);
                result = json.RootElement.TryGetProperty("result", out var nested) ? nested.Clone() : json.RootElement.Clone();
            }
            catch (JsonException) { }
        if (result.ValueKind == JsonValueKind.Undefined && !string.IsNullOrWhiteSpace(original.Detail))
            result = JsonSerializer.SerializeToElement(new { message = original.Detail }, JsonOptions);
        return state.CompleteDeepResearch(new(0, state.ParentRunId, RunEventTypes.ServerToolCompleted, at, default), result, status);
    }

    internal static string? DeepResearchTask(AssistantToolStep step)
    {
        if (step.InputJson is { } input)
            try
            {
                using var document = JsonDocument.Parse(input);
                foreach (var name in new[] { "task", "question", "query", "topic", "prompt" })
                    if (Text(document.RootElement, name) is { Length: > 0 } task) return task;
            }
            catch (JsonException) { }
        return step.Explanation ?? step.Detail;
    }

    private static string DeepResearchResultText(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.String) return result.GetString() ?? "";
        if (result.ValueKind != JsonValueKind.Object) return "";
        var text = new StringBuilder();
        if (Text(result, "message") is { Length: > 0 } message) text.AppendLine(message).AppendLine();
        if (Text(result, "summary") is { Length: > 0 } summary && summary != Text(result, "message"))
            text.AppendLine(summary).AppendLine();
        if (Text(result, "assessmentReason") is { Length: > 0 } assessment) text.AppendLine(assessment).AppendLine();
        if (result.TryGetProperty("problem", out var problem) && problem.ValueKind == JsonValueKind.Object
            && Text(problem, "interpretedQuestion") is { Length: > 0 } question) text.AppendLine(question).AppendLine();
        AppendResearchList(text, result, "findings", "Erkenntnisse", ["claim", "text", "summary"]);
        AppendResearchList(text, result, "verifications", "Prüfung", ["status", "method", "reason"]);
        AppendResearchList(text, result, "counterexamples", "Gegenbeispiele", ["text", "claim", "reason"]);
        AppendResearchList(text, result, "uncertainties", "Offene Fragen", ["text", "reason"]);
        return text.ToString().Trim();
    }

    private static void AppendResearchList(StringBuilder text, JsonElement result, string property, string heading, string[] fields)
    {
        if (!result.TryGetProperty(property, out var rows) || rows.ValueKind != JsonValueKind.Array) return;
        var lines = rows.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.String ? row.GetString()
            : row.ValueKind == JsonValueKind.Object ? string.Join(" – ", fields.Select(field => Text(row, field)).OfType<string>()) : null)
            .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (lines.Length == 0) return;
        text.Append("### ").AppendLine(heading);
        foreach (var line in lines) text.Append("- ").AppendLine(line);
        text.AppendLine();
    }
}

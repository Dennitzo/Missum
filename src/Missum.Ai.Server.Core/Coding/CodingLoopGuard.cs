using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Coding;

public static class CodingLoopGuard
{
    public const int MaximumToolResultCharacters = 32_000;
    private static readonly string[] ReceiptMetadataFields =
    [
        "status", "success", "errorCode", "exitCode", "timedOut", "outcomeUnknown", "applied",
        "evidenceId", "stream", "offset", "nextOffset", "hasMore",
        "storedCharacters", "storedBytes", "truncated", "tool", "provider", "isFallback", "found",
        "path", "nextLine", "startLine", "endLine", "totalLines",
    ];
    private static readonly string[] ReceiptNestedFields = ["result", "evidence"];
    public static string BoundToolResult(string value, string? toolName = null)
    {
        // Bounded file searches, fresh reads, complete plans and delegated results must reach context planning intact,
        // including JSON escaping. A finished agent's report is the parent's only
        // complete result, not a disposable console preview. This also covers a
        // completed resume returned by subagent.spawn. The normal model-context
        // planner still handles actual context pressure after this handoff.
        if (toolName is "coding.read" or "coding.search" or CodingWorkingStateTools.PlanTool or CodingDeepResearchPipeline.ToolName
            or SubagentToolNames.Wait or SubagentToolNames.Spawn) return value;
        var maximum = toolName == "coding.readOutput" ? 256_000 : MaximumToolResultCharacters;
        if (value.Length <= maximum) return value;
        var metadataBudget = 6000;
        var metadataTruncated = false;
        Dictionary<string, object?> bounded = [];
        try
        {
            using var document = JsonDocument.Parse(value);
            bounded = CopyReceiptMetadata(document.RootElement, 0, ref metadataBudget, ref metadataTruncated);
        }
        catch (JsonException) { }
        bounded["truncated"] = true;
        bounded["originalCharacters"] = value.Length;
        bounded["message"] = "Die Ausgabe wurde begrenzt. Nutze vorhandene Beleg-IDs mit coding.readOutput oder lies gezielt kleinere Ausschnitte. Vorschau und Ende sind unvollständiger Ausgabetext.";
        if (metadataTruncated) bounded["metadataTruncated"] = true;
        var previewField = bounded.ContainsKey("preview") ? "textPreview" : "preview";
        var previewCharacters = 1000;
        var tailCharacters = 400;
        while (true)
        {
            bounded[previewField] = value[..previewCharacters];
            bounded["tail"] = value[(value.Length - tailCharacters)..];
            var serialized = JsonSerializer.Serialize(bounded);
            if (serialized.Length <= MaximumToolResultCharacters) return serialized;
            previewCharacters /= 2;
            tailCharacters /= 2;
        }
    }

    private static Dictionary<string, object?> CopyReceiptMetadata(JsonElement source, int depth, ref int budget,
        ref bool truncated)
    {
        Dictionary<string, object?> result = [];
        if (source.ValueKind != JsonValueKind.Object) return result;
        CopyFields(source, result, ReceiptMetadataFields, ref budget, ref truncated);
        if (depth < 2)
        {
            foreach (var name in ReceiptNestedFields)
            {
                if (!source.TryGetProperty(name, out var nested) || nested.ValueKind != JsonValueKind.Object) continue;
                result[name] = CopyReceiptMetadata(nested, depth + 1, ref budget, ref truncated);
            }
        }
        return result;
    }

    private static void CopyFields(JsonElement source, Dictionary<string, object?> result, string[] names,
        ref int budget, ref bool truncated)
    {
        foreach (var name in names)
        {
            if (!source.TryGetProperty(name, out var field) || field.ValueKind is not
                (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)) continue;
            var serialized = JsonSerializer.Serialize(field);
            var cost = serialized.Length + name.Length + 8;
            var pathField = name is "path" or "baseScene" || name.EndsWith("Path", StringComparison.Ordinal);
            if (serialized.Length > (pathField ? 2048 : 512) || cost > budget) { truncated = true; continue; }
            result[name] = field.Clone();
            budget -= cost;
        }
    }

    public static void ThrowIfRepeatedFailure(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate, CodingWorkingState? workingState = null)
    {
        if (workingState is not null)
        {
            var argumentHash = CodingWorkingStateReducer.ArgumentHash(candidate.Arguments);
            if (workingState.Failures.Any(failure => failure.Tool == candidate.Name && failure.ArgumentHash == argumentHash
                && failure.EnvironmentRevision == workingState.EnvironmentRevision && failure.Count >= 2))
            {
                if (candidate.Name == CodingWorkingStateTools.PlanTool)
                    throw new AgentRunLimitException("coding.updatePlan wurde im unveränderten Arbeitszustand bereits zweimal mit denselben Argumenten abgelehnt. "
                        + "Dieser Aufruf wurde nicht erneut ausgeführt. Korrigiere die in der letzten Fehlermeldung genannten Planfelder. "
                        + "steps und acceptanceCriteria können gemeinsam in einem korrigierten Aufruf gespeichert werden. Der Lauf bleibt aktiv.");
                throw new AgentRunLimitException($"'{candidate.Name}' wurde im unveränderten Arbeitszustand bereits zweimal mit denselben Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Prüfe den vorhandenen Fehlerbeleg mit coding.readOutput oder coding.searchRunEvidence, korrigiere Befehl, Pfad oder Umgebung und setze den Nutzerauftrag mit einem anderen Ansatz fort. Der Lauf bleibt aktiv.");
            }
            return;
        }
        var matchingIds = messages.SelectMany(static message => message.ToolCalls ?? [])
            .Where(call => call.Name == candidate.Name && JsonElement.DeepEquals(call.Arguments, candidate.Arguments))
            .Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
        var failedCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "tool" && message.ToolCallId is { } callId && matchingIds.Contains(callId)
                && IsFailure(message.Content)) failedCalls.Add(callId);
            else if (ReadDeferredResult(message) is { } deferred && matchingIds.Contains(deferred.CallId)
                && IsFailure(deferred.Result)) failedCalls.Add(deferred.CallId);
        }
        if (failedCalls.Count >= 2)
            throw new AgentRunLimitException($"'{candidate.Name}' wurde bereits zweimal mit identischen Argumenten erfolglos aufgerufen. Dieser Aufruf wurde nicht erneut ausgeführt. Lies den vorhandenen Fehlerbeleg, korrigiere die Argumente oder wähle ein anderes Werkzeug und setze den Nutzerauftrag fort. Der Lauf bleibt aktiv.");
    }

    public static void ThrowIfRenderAlreadyUsed(IReadOnlyList<LmChatMessage> messages, LmToolCall candidate)
    {
        if (candidate.Name != ClientToolNames.CodingRenderHtml) return;
        var renderCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
                if (call.Name == ClientToolNames.CodingRenderHtml) renderCalls.Add(call.Id);
            var completedId = message.Role == "tool" && !IsInterimResult(message.Content)
                ? message.ToolCallId : ReadDeferredResult(message)?.CallId;
            if (completedId is not null && renderCalls.Contains(completedId))
                throw new ArgumentException("coding.renderHtml darf höchstens einmal pro Lauf ausgeführt werden; die vorhandene Vorschau bleibt erhalten.");
        }
    }

    private static bool IsFailure(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            return IsFailure(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool IsFailure(JsonElement value) => !IsInterimResult(value) && CodingWorkingStateReducer.IsFailedResult(value);

    private static bool IsInterimResult(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("final", out var final) && final.ValueKind == JsonValueKind.False;

    private static bool IsInterimResult(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            return IsInterimResult(document.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static (string CallId, string Result)? ReadDeferredResult(LmChatMessage message)
    {
        if (message.Role != "user" || message.Content is not { } content
            || !content.StartsWith(CommandMonitoringPolicy.DeferredResultMarker, StringComparison.Ordinal)) return null;
        // The server-generated envelope ends with a single JSON line. Embedded
        // output newlines are JSON-escaped; never interpret the explanatory text
        // or an arbitrary user message as a completed tool receipt.
        var jsonStart = content.LastIndexOf('\n') + 1;
        try
        {
            using var document = JsonDocument.Parse(content[jsonStart..]);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("authoritativeFinalResult", out var final) || final.ValueKind != JsonValueKind.True
                || !value.TryGetProperty("callId", out var id) || id.ValueKind != JsonValueKind.String
                || id.GetString() is not { Length: > 0 } callId
                || !value.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String)
                return null;
            return (callId, result.GetString()!);
        }
        catch (JsonException) { return null; }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Coding;

/// <summary>One-time upgrade of model context. Original run events and client results remain untouched.</summary>
internal static class CodingPlanFormat
{
    internal const int CurrentVersion = 2;
    private const string NativeInstructionPrefix = "Missum-Laufanweisung:\n";
    private const string RetiredEvidenceMarker = "MISSUM_CODING_PLAN_EVIDENCE_V1\n";
    private static readonly JsonSerializerOptions JsonOptions = new(Missum.Ai.Contracts.MissumAiProtocol.CreateJsonOptions())
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    internal static AgentRunCheckpoint UpgradeCheckpoint(AgentRunCheckpoint checkpoint,
        IReadOnlyCollection<string>? originalUserTexts = null)
    {
        if (checkpoint.PlanFormatVersion >= CurrentVersion) return checkpoint;
        var messages = UpgradeMessages(checkpoint.Messages, originalUserTexts, out var messagesChanged);
        var contextTools = UpgradeTools(checkpoint.ContextTools);
        var lastModelTools = UpgradeTools(checkpoint.LastModelTools);
        var toolsChanged = !ReferenceEquals(contextTools, checkpoint.ContextTools) || !ReferenceEquals(lastModelTools, checkpoint.LastModelTools);
        var state = checkpoint.WorkingState;
        var stateChanged = false;
        if (state is not null)
        {
            var failures = state.Failures.Where(failure => !IsObsoletePlanFailure(failure, state)).ToArray();
            var evidence = state.Evidence.Where(receipt => receipt.Tool != CodingWorkingStateTools.PlanTool
                || !HasRetiredPlanInstructions(receipt.Summary)).ToArray();
            stateChanged = failures.Length != state.Failures.Count || evidence.Length != state.Evidence.Count;
            if (stateChanged)
                state = state with { Failures = failures, Evidence = evidence, ConsecutivePlanNoOps = 0 };
        }
        // Nothing retired means nothing was rewritten: keep the evaluated prompt
        // prefix and tool catalog signature exactly as the run stored them.
        if (!messagesChanged && !stateChanged && !toolsChanged) return checkpoint with { PlanFormatVersion = CurrentVersion };
        return checkpoint with
        {
            Messages = messages, WorkingState = state, ContextTools = contextTools, LastModelTools = lastModelTools,
            PlanFormatVersion = CurrentVersion, WorkingStatePromptIncluded = false,
            PreserveSessionPromptPrefix = false, ToolCatalogSignature = null,
        };
    }

    private static IReadOnlyList<LmToolDefinition>? UpgradeTools(IReadOnlyList<LmToolDefinition>? tools)
    {
        if (tools is null) return null;
        var index = tools.ToList().FindIndex(tool => tool.Name == CodingWorkingStateTools.PlanTool
            && (HasRetiredPlanInstructions(tool.Description) || HasRetiredPlanInstructions(tool.Parameters.GetRawText())));
        if (index < 0) return tools;
        var current = CodingWorkingStateTools.CreateTools().Single();
        if (tools[index].Parameters.TryGetProperty("properties", out var properties)
            && properties.TryGetProperty("scienceStages", out _)) current = ScienceSkill.DescribePlanTool(current);
        var upgraded = tools.ToArray();
        upgraded[index] = current.ToLmDefinition();
        return upgraded;
    }

    internal static IReadOnlyList<LmChatMessage> UpgradeMessages(IReadOnlyList<LmChatMessage> messages,
        IReadOnlyCollection<string>? originalUserTexts, out bool changed)
    {
        changed = false;
        var result = new List<LmChatMessage>(messages.Count);
        var planCalls = messages.SelectMany(message => message.ToolCalls ?? [])
            .Where(call => call.Name == CodingWorkingStateTools.PlanTool).Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "user" && originalUserTexts?.Contains(message.Content!, StringComparer.Ordinal) == true)
            {
                result.Add(message);
                continue;
            }
            var content = message.Content;
            var originalContent = content;
            var instructionPrefix = content?.StartsWith(NativeInstructionPrefix, StringComparison.Ordinal) == true ? NativeInstructionPrefix
                : content?.StartsWith("Missum-Laufanweisung:\r\n", StringComparison.Ordinal) == true ? "Missum-Laufanweisung:\r\n" : null;
            var instruction = message.Role == "system" ? content
                : message.Role == "user" && instructionPrefix is not null ? content![instructionPrefix.Length..] : null;
            if (instruction is not null)
            {
                var canonical = instruction.ReplaceLineEndings("\n");
                if (canonical.StartsWith(RetiredEvidenceMarker, StringComparison.Ordinal)
                    || canonical.StartsWith("MISSUM_CODING_PLAN_MILESTONES_V1\n", StringComparison.Ordinal)
                    || canonical.StartsWith("MISSUM_SCIENCE_INITIAL_PLAN_V1\n", StringComparison.Ordinal)
                    || canonical.StartsWith("MISSUM_SCIENCE_PLAN_CONTINUATION_V1\n", StringComparison.Ordinal)
                    || canonical.StartsWith("MISSUM_SCIENCE_FOCUS_V1\n", StringComparison.Ordinal))
                {
                    changed = true; // Steering blocks are re-offered per turn, so the stored copy is dropped.
                    continue;
                }
                // The science skill is injected once when the run starts and never re-added,
                // so a stored copy is refreshed in place instead of being dropped.
                if (canonical.StartsWith(ScienceSkill.Marker, StringComparison.Ordinal) && canonical != ScienceSkill.Instructions)
                {
                    content = message.Role == "system" ? ScienceSkill.Instructions : NativeInstructionPrefix + ScienceSkill.Instructions;
                    changed = true;
                }
                if (instruction.StartsWith("MISSUM_SCIENCE_SKILL_MODULE_V1:", StringComparison.Ordinal)
                    && HasRetiredPlanInstructions(instruction))
                {
                    changed = true;
                    continue;
                }
                var cleaned = ReplaceWorkingPolicy(instruction);
                if (cleaned != instruction) content = message.Role == "system" ? cleaned : NativeInstructionPrefix + cleaned;
            }
            if (message.Role == "user" && content is not null)
            {
                if (content.StartsWith(RetiredEvidenceMarker, StringComparison.Ordinal))
                {
                    changed = true;
                    continue;
                }
                if (content.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal))
                    content = UpgradeWorkingContext(content);
                else if (content.StartsWith(CodingSessionContext.StateMarker, StringComparison.Ordinal))
                    content = UpgradeLegacyStateEnvelope(content);
                else if (content.StartsWith(CommandMonitoringPolicy.DeferredResultMarker, StringComparison.Ordinal)
                    || content.StartsWith(SubagentToolNames.ResultContextMarker, StringComparison.Ordinal))
                    content = UpgradeJsonEnvelope(content);
            }
            else if (message.Role == "tool" && content is not null)
                content = UpgradeToolJson(content, message.ToolCallId is not null && planCalls.Contains(message.ToolCallId));
            var calls = message.ToolCalls;
            if (calls?.Any(call => call.Name == CodingWorkingStateTools.PlanTool && HasRetiredPlanInstructions(call.Arguments.GetRawText())) == true)
                calls = calls.Select(call => call.Name == CodingWorkingStateTools.PlanTool
                    ? call with { Arguments = UpgradePlanJson(call.Arguments) } : call).ToArray();
            if (!ReferenceEquals(content, originalContent) || !ReferenceEquals(calls, message.ToolCalls)) changed = true;
            result.Add(message with { Content = content, ToolCalls = calls });
        }
        return result;
    }

    private static bool IsObsoletePlanFailure(CodingFailureSignature failure, CodingWorkingState state) => failure.Tool == CodingWorkingStateTools.PlanTool
        && (failure.Error.Contains("evidence", StringComparison.OrdinalIgnoreCase)
            || failure.Error.Contains("Beleg", StringComparison.OrdinalIgnoreCase)
            || failure.Error == "agent.invalid_state_action"
                && state.Evidence.Any(receipt => receipt.Id == failure.EvidenceId && HasRetiredPlanInstructions(receipt.Summary)));

    private static bool HasRetiredPlanInstructions(string content) => content.Contains("evidenceIds", StringComparison.OrdinalIgnoreCase)
        || content.Contains("planEvidenceId", StringComparison.OrdinalIgnoreCase) || content.Contains("evidenceQuery", StringComparison.OrdinalIgnoreCase);

    private static string ReplaceWorkingPolicy(string content)
    {
        if (!HasRetiredPlanInstructions(content)) return content;
        const string start = "Beginne jeden Codex- und Science-Hauptlauf verpflichtend mit coding.updatePlan";
        var from = content.IndexOf(start, StringComparison.Ordinal);
        if (from < 0)
        {
            if (!content.StartsWith(CompactAgentContextPolicy.Marker, StringComparison.Ordinal)) return content;
            from = content.IndexOf("Coding-Agent:", StringComparison.Ordinal);
            if (from < 0) return content;
            var end = NextParagraph(content, from);
            if (end < 0) end = content.Length;
            return content[..from] + CompactAgentContextPolicy.Coding + content[end..];
        }
        var through = NextParagraph(content, from);
        if (through < 0) through = content.Length;
        return content[..from] + CodingAgentPolicy.WorkingStatePrompt + content[through..];
    }

    private static int NextParagraph(string content, int from)
    {
        var lf = content.IndexOf("\n\n", from, StringComparison.Ordinal);
        var crlf = content.IndexOf("\r\n\r\n", from, StringComparison.Ordinal);
        return lf < 0 ? crlf : crlf < 0 ? lf : Math.Min(lf, crlf);
    }

    private static string UpgradeWorkingContext(string content)
    {
        var lines = content.Split('\n');
        for (var index = 1; index < lines.Length; index++)
        {
            try
            {
                if (JsonNode.Parse(lines[index]) is not JsonObject entry
                    || entry["section"]?.GetValue<string>() is not ("plan" or "acceptance" or "scienceStage" or "evidenced_fact" or "rejected_hypothesis")) continue;
                if (!HasRetiredPlanInstructions(lines[index]) && !lines[index].Contains("ReopenedAtSequence", StringComparison.Ordinal)
                    && !lines[index].Contains("reopenedAtSequence", StringComparison.Ordinal)) continue;
                if (entry["data"] is { } data) RemoveRetiredPlanFields(data);
                lines[index] = entry.ToJsonString();
            }
            catch (JsonException) { } // Source excerpts and explanatory lines are ordinary text.
        }
        return string.Join('\n', lines);
    }

    private static string UpgradeLegacyStateEnvelope(string content)
    {
        var start = content.IndexOf('{');
        if (start < 0) return content;
        try
        {
            var state = JsonSerializer.Deserialize<CodingWorkingState>(content[start..], JsonOptions);
            return state is null ? content : content[..start] + JsonSerializer.Serialize(state, JsonOptions);
        }
        catch (JsonException) { return content; }
    }

    private static string UpgradeJsonEnvelope(string content)
    {
        var start = content.IndexOfAny(['{', '['], content.IndexOf('\n') + 1);
        if (start < 0) return content;
        var cleaned = UpgradeToolJson(content[start..], plan: false);
        return content[..start] + cleaned;
    }

    private static string UpgradeToolJson(string content, bool plan)
    {
        if (!content.Contains("_missumEvidence", StringComparison.Ordinal)
            && !content.Contains("delegatedPlanEvidence", StringComparison.Ordinal)
            && !content.Contains("MISSUM_TOOL_EVIDENCE_V1", StringComparison.Ordinal)
            && (!plan || !HasRetiredPlanInstructions(content) && !content.Contains("reopenedAtSequence", StringComparison.Ordinal)))
            return content;
        try
        {
            var node = JsonNode.Parse(content);
            if (node is null) return content;
            RemoveAnnotations(node);
            if (plan)
            {
                RemoveRetiredPlanFields(node);
                if (node is JsonObject obj && obj["error"] is JsonValue error && error.TryGetValue<string>(out var errorText)
                    && HasRetiredPlanInstructions(errorText))
                {
                    obj.Remove("error");
                    obj.Remove("errorCode");
                    obj["message"] = "Historischer Planaufruf im früheren Format. Für weitere Arbeit gilt der aktuelle gespeicherte Plan.";
                }
            }
            return node.ToJsonString();
        }
        catch (JsonException)
        {
            var marker = content.LastIndexOf("\nMISSUM_TOOL_EVIDENCE_V1\n", StringComparison.Ordinal);
            return marker < 0 ? content : content[..marker];
        }
    }

    private static JsonElement UpgradePlanJson(JsonElement value)
    {
        var node = JsonNode.Parse(value.GetRawText());
        if (node is null) return value;
        RemoveRetiredPlanFields(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static void RemoveRetiredPlanFields(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("evidenceIds");
            obj.Remove("evidenceQuery");
            obj.Remove("reopenedAtSequence");
            obj.Remove("ReopenedAtSequence");
            obj.Remove("EvidenceIds");
            obj.Remove("validationIssues");
            obj.Remove("candidates");
            foreach (var property in obj.ToArray())
                if (property.Value is { } child) RemoveRetiredPlanFields(child);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) RemoveRetiredPlanFields(child);
    }

    private static void RemoveAnnotations(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("_missumEvidence");
            obj.Remove("delegatedPlanEvidence");
            foreach (var property in obj.ToArray())
                if (property.Value is { } child) RemoveAnnotations(child);
            // Deferred command results store the authentic JSON as a string.
            if (obj["authoritativeFinalResult"]?.GetValueKind() == JsonValueKind.True
                && obj["result"] is JsonValue result && result.TryGetValue<string>(out var text))
                obj["result"] = UpgradeToolJson(text, plan: false);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null) RemoveAnnotations(child);
    }
}

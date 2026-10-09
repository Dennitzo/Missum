using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Reduces research history without truncating its current scientific or execution evidence.</summary>
internal static class ScientificRunContextPlanner
{
    internal const int CanonicalWorkingInputTokens = 128 * 1024;

    internal static ContextPlan Prepare(IReadOnlyList<LmChatMessage> source, int contextLength, int? maximumOutputTokens,
        bool preserveConversationPrefix = false, int? maximumInputTokens = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = ContextPlanner.ComputeInputTokenBudget(contextLength, maximumOutputTokens);
        if (maximumInputTokens is { } workingBudget)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(workingBudget, 1024);
            budget = Math.Min(budget, workingBudget);
        }
        var originalTokens = ContextPlanner.EstimateTokens(source);
        // Already evaluated scientific turns include reasoning and chronological
        // host instructions. Removing either below the working limit rewrites the
        // KV prefix and makes every following tool turn prefill old research again.
        if (preserveConversationPrefix && originalTokens <= budget)
            return new(source.ToArray(), originalTokens, budget, false, null);
        var messages = source.ToArray();
        var keep = Enumerable.Repeat(true, messages.Length).ToArray();
        var protect = new HashSet<int>();
        var groups = BindToolPairs(messages);
        var byAssistant = groups.ToDictionary(static group => group.AssistantIndex);
        var pairedIndices = groups.SelectMany(static group => group.Indices).ToHashSet();
        var publications = new List<int>();
        string? publication = null;
        var publicationChange = -1;
        var lastLanguageReminder = -1;
        var lastPlainAssistant = -1;
        var lastRuntimeContext = -1;
        var lastNativeInstructions = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Length; index++)
        {
            if (IsNativeRuntimeInstruction(messages[index])) lastNativeInstructions[messages[index].Content!] = index;
            if (IsRuntimeContext(messages[index])) lastRuntimeContext = index;
        }

        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            if (message.Role == "system") protect.Add(index);
            if (ModelRuntimeClient.IsLanguageReminder(message)) lastLanguageReminder = index;
            else if (message.Role == "user" && !IsEvidenceDossier(message.Content)
                && (!IsRuntimeContext(message) || index == lastRuntimeContext)
                && (!IsNativeRuntimeInstruction(message) || lastNativeInstructions[message.Content!] == index)) protect.Add(index);
            if (message.Role == "assistant" && message.ToolCalls is not { Count: > 0 }) lastPlainAssistant = index;
            if (message.Role != "assistant" || message.Content is not { } content || !TryPublication(content, out var body)) continue;
            publications.Add(index);
            // Identical manuscript repetitions after verify must not manufacture
            // a new epoch by removing the earlier, already verified occurrence.
            if (!string.Equals(publication, body, StringComparison.Ordinal)) publicationChange = index;
            publication = body;
        }
        if (publicationChange >= 0) protect.Add(publicationChange);
        if (publications.Count > 0) protect.Add(publications[^1]);
        if (lastPlainAssistant >= 0) protect.Add(lastPlainAssistant);
        if (lastLanguageReminder >= 0) protect.Add(lastLanguageReminder);
        ProtectCurrentToolEvidence(messages, groups, protect);
        // Keeping an assistant with several tool calls always keeps its complete
        // result group. An old pair is likewise removed only as a whole group.
        foreach (var group in groups)
            if (group.Indices.Any(protect.Contains)) protect.UnionWith(group.Indices);

        var changed = false;
        var removed = 0;
        void Remove(int index)
        {
            if (!keep[index] || protect.Contains(index)) return;
            keep[index] = false;
            changed = true;
            removed++;
        }
        void RemoveGroup(ToolGroup group)
        {
            if (group.Indices.Any(protect.Contains)) return;
            foreach (var index in group.Indices) Remove(index);
        }
        LmChatMessage[] Remaining() => messages.Where((_, index) => keep[index]).ToArray();
        int Tokens() => ContextPlanner.EstimateTokens(Remaining());

        foreach (var index in publications)
        {
            if (protect.Contains(index)) continue;
            if (byAssistant.TryGetValue(index, out var group)) RemoveGroup(group);
            else Remove(index);
        }
        for (var index = 0; index < messages.Length; index++)
        {
            if (ModelRuntimeClient.IsLanguageReminder(messages[index]) && index != lastLanguageReminder) Remove(index);
            if (IsNativeRuntimeInstruction(messages[index]) && lastNativeInstructions[messages[index].Content!] != index) Remove(index);
            if (keep[index] && !protect.Contains(index) && messages[index].ReasoningContent is not null)
            {
                messages[index] = messages[index] with { ReasoningContent = null };
                changed = true;
            }
        }
        if (Tokens() > budget)
            for (var index = 0; index < messages.Length; index++)
                if (keep[index] && messages[index].ReasoningContent is not null)
                {
                    // Current visible manuscript and structured receipts remain
                    // byte-for-byte intact; private reasoning is not evidence.
                    messages[index] = messages[index] with { ReasoningContent = null };
                    changed = true;
                }
        // Retain citations and original dossiers until ordinary narration and
        // obsolete execution logs have been removed. No replacement claim or
        // invented semantic summary is inserted into the model's conversation.
        for (var index = 0; index < lastRuntimeContext; index++)
            if (IsRuntimeContext(messages[index]))
            {
                if (Tokens() <= budget) break;
                Remove(index);
            }
        foreach (var index in Enumerable.Range(0, messages.Length).Where(index =>
            messages[index].Role == "assistant" && !pairedIndices.Contains(index) && !HasCitation(messages[index].Content)))
        {
            if (Tokens() <= budget) break;
            Remove(index);
        }
        foreach (var group in groups)
        {
            if (Tokens() <= budget) break;
            RemoveGroup(group);
        }
        foreach (var index in Enumerable.Range(0, messages.Length).Where(index =>
            messages[index].Role == "assistant" && !pairedIndices.Contains(index)))
        {
            if (Tokens() <= budget) break;
            Remove(index);
        }
        foreach (var index in Enumerable.Range(0, messages.Length).Where(index =>
            messages[index].Role == "user" && IsEvidenceDossier(messages[index].Content)))
        {
            if (Tokens() <= budget) break;
            Remove(index);
        }
        var result = Remaining();
        var estimated = ContextPlanner.EstimateTokens(result);
        if (estimated > budget) throw new ContextBudgetException(estimated, budget);
        return new(result, estimated, budget, changed, changed
            ? $"Der Forschungs-Arbeitskontext wurde gezielt auf {estimated:N0} von {budget:N0} Eingabetoken verdichtet. Der aktuelle Forschungsauftrag, die aktuelle Publikation und ihre Werkzeugbelege bleiben vollständig erhalten. {removed} ältere Kontextnachrichten wurden aus dem Arbeitskontext entfernt; das vollständige Laufjournal bleibt erhalten. Forschungsdetails können mit research.read erneut geladen werden." : null);
    }

    private static void ProtectCurrentToolEvidence(LmChatMessage[] messages, IReadOnlyList<ToolGroup> groups, HashSet<int> protect)
    {
        var latestWrite = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestStateRead = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestStateUpdate = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestPython = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestVerify = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestVerifyReceipt = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestMutation = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var latestMutationReceipt = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        ToolGroup? latestDocumentMutation = null;
        foreach (var group in groups)
        {
            if (group.Calls.Any(call => !group.Receipts.ContainsKey(call.Id))) protect.UnionWith(group.Indices);
            foreach (var call in group.Calls)
            {
                var project = Text(call.Arguments, "projectId");
                if (call.Name == "document.create") latestDocumentMutation = group;
                if (project.Length == 0) continue;
                if (call.Name == ClientToolNames.ResearchRead) latestStateRead[project] = group;
                if (call.Name == ClientToolNames.ResearchUpdate) latestStateUpdate[project] = group;
                if (call.Name == ClientToolNames.ResearchCodeWrite) latestWrite[project] = group;
                if (call.Name is ClientToolNames.ResearchCodeWrite or ClientToolNames.ResearchCodeExecute
                    or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark) latestMutation[project] = group;
                var readableReceipt = group.Receipts.TryGetValue(call.Id, out var receipts)
                    && receipts.Any(index => IsObjectReceipt(messages[index].Content));
                if (readableReceipt && call.Name is ClientToolNames.ResearchCodeWrite or ClientToolNames.ResearchCodeExecute
                    or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark or "document.create") latestMutationReceipt[project] = group;
                if (readableReceipt && call.Name is ClientToolNames.ResearchCodeExecute or ClientToolNames.ResearchCodeTest or ClientToolNames.ResearchCodeBenchmark
                    && Path.GetFileName(Text(call.Arguments, "executable").Replace('\\', '/')).StartsWith("python", StringComparison.OrdinalIgnoreCase))
                    latestPython[project] = group;
                if (call.Name != ClientToolNames.ResearchDeliverablesVerify) continue;
                latestVerify[project] = group;
                if (readableReceipt) latestVerifyReceipt[project] = group;
            }
        }
        foreach (var group in latestWrite.Values.Concat(latestPython.Values).Concat(latestVerify.Values)
            .Concat(latestVerifyReceipt.Values).Concat(latestMutation.Values).Concat(latestMutationReceipt.Values)
            .Concat(latestStateRead.Values).Concat(latestStateUpdate.Values)) protect.UnionWith(group.Indices);
        if (latestDocumentMutation is not null) protect.UnionWith(latestDocumentMutation.Indices);
        ProtectCanonicalReadContext(messages, groups, protect);
    }

    private static void ProtectCanonicalReadContext(LmChatMessage[] messages, IReadOnlyList<ToolGroup> groups,
        HashSet<int> protect)
    {
        var overviews = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        var taskPages = new Dictionary<(string Project, string Stamp, int Total), Dictionary<int, TaskPage>>();
        foreach (var group in groups)
            foreach (var call in group.Calls)
            {
                if (call.Name != ClientToolNames.ResearchRead || !group.Receipts.TryGetValue(call.Id, out var receipts)) continue;
                var project = Text(call.Arguments, "projectId");
                if (project.Length == 0) continue;
                foreach (var index in receipts)
                {
                    if (!ScientificStateCompletionPolicy.TryReceipt(messages[index].Content, out var result, out var success)
                        || !success || Text(result, "projectId") != project
                        || Text(result, "protocol") != ScientificStateCompletionPolicy.Protocol) continue;
                    var stamp = Text(result, "stateStamp");
                    if (stamp.Length != 64 || !stamp.All(Uri.IsHexDigit)) continue;
                    var view = Text(call.Arguments, "view");
                    if (view != Text(result, "view")) continue;
                    if (view == "overview"
                        && (!call.Arguments.TryGetProperty("ids", out var ids) || ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 0)
                        && !call.Arguments.TryGetProperty("cursor", out _)
                        && (!call.Arguments.TryGetProperty("offset", out var offset) || offset.ValueKind == JsonValueKind.Number && offset.TryGetInt32(out var number) && number == 0)
                        && !ScientificStateCompletionPolicy.Boolean(result, "unchanged")
                        && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        overviews[project] = group;
                    if (view != "task" || !TryNonnegativeInt(result, "characterOffset", out var start)
                        || !TryNonnegativeInt(result, "totalCharacters", out var total)
                        || !result.TryGetProperty("originalQuestion", out var content) || content.ValueKind != JsonValueKind.String
                        || !result.TryGetProperty("nextCursor", out var next) || next.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) continue;
                    var length = content.GetString()!.Length;
                    if (start > total || length > total - start || length == 0 && start != total
                        || (start + length < total) != (next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString()))) continue;
                    var key = (project, stamp, total);
                    if (!taskPages.TryGetValue(key, out var pages)) taskPages[key] = pages = [];
                    pages[start] = new TaskPage(start, length, group);
                }
            }
        // An unchanged receipt has no working items. Its last full first overview
        // must survive even when the last read was only a detail or acknowledgement.
        foreach (var overview in overviews.Values) protect.UnionWith(overview.Indices);
        foreach (var project in taskPages.GroupBy(static entry => entry.Key.Project, StringComparer.Ordinal))
        {
            var latest = project.MaxBy(static entry => entry.Value.Values.Max(static page => page.Group.AssistantIndex));
            foreach (var page in latest.Value.Values) protect.UnionWith(page.Group.Indices);
            // Keep the last complete original task while a newer stamped read is
            // still paginating. Replace it only once every new character is present.
            var complete = project.Where(static entry => IsCompleteTask(entry.Value, entry.Key.Total))
                .OrderByDescending(static entry => entry.Value.Values.Max(static page => page.Group.AssistantIndex)).FirstOrDefault();
            if (complete.Value is not null)
                foreach (var page in complete.Value.Values) protect.UnionWith(page.Group.Indices);
        }
    }

    private static bool TryNonnegativeInt(JsonElement value, string property, out int number)
    {
        number = 0;
        return value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number
            && field.TryGetInt32(out number) && number >= 0;
    }

    private static bool IsCompleteTask(Dictionary<int, TaskPage> pages, int total)
    {
        var next = 0;
        foreach (var page in pages.Values.OrderBy(static page => page.Offset))
        {
            if (page.Offset != next) return false;
            next += page.Length;
        }
        return next == total;
    }

    private sealed record TaskPage(int Offset, int Length, ToolGroup Group);

    private static List<ToolGroup> BindToolPairs(LmChatMessage[] messages)
    {
        var groups = new List<ToolGroup>();
        var pending = new Dictionary<string, ToolGroup>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Length; index++)
        {
            var message = messages[index];
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                var group = new ToolGroup(index, calls);
                groups.Add(group);
                foreach (var call in calls) pending[call.Id] = group;
            }
            if (message.Role != "tool" || message.ToolCallId is not { } id || !pending.TryGetValue(id, out var owner)) continue;
            owner.Indices.Add(index);
            if (!owner.Receipts.TryGetValue(id, out var receipts)) owner.Receipts[id] = receipts = [];
            receipts.Add(index);
        }
        return groups;
    }

    private static bool IsObjectReceipt(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var receipt = JsonDocument.Parse(content);
            return receipt.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsEvidenceDossier(string? content) => content?.TrimStart().StartsWith("[MISSUM_SCIENTIFIC_RESEARCH_DOSSIER]", StringComparison.Ordinal) == true
        || content?.TrimStart().StartsWith("[MISSUM_WEB_RESEARCH_DOSSIER]", StringComparison.Ordinal) == true;
    private static bool IsNativeRuntimeInstruction(LmChatMessage message) => message.Role == "user"
        && message.Content?.StartsWith("Missum-Laufanweisung:\n", StringComparison.Ordinal) == true;
    private static bool IsRuntimeContext(LmChatMessage message) => message.Role == "user"
        && message.Content?.StartsWith(CompactAgentContextPolicy.RuntimeMarker, StringComparison.Ordinal) == true;
    private static bool HasCitation(string? content) => content?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true
        || content?.Contains("http://", StringComparison.OrdinalIgnoreCase) == true || content?.Contains("doi:", StringComparison.OrdinalIgnoreCase) == true;
    private static string Text(JsonElement owner, string name) => owner.ValueKind == JsonValueKind.Object
        && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    // The marker boundaries deliberately match the completion gate: a newer
    // unfinished manuscript supersedes an older complete one, and examples in
    // code fences never become a publication.
    private static bool TryPublication(string content, out string publication)
    {
        publication = "";
        var candidate = content.Trim();
        if (candidate.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = candidate.IndexOf('\n');
            var end = candidate.LastIndexOf("```", StringComparison.Ordinal);
            if (newline >= 0 && end > newline) candidate = candidate[(newline + 1)..end].Trim();
        }
        if (candidate.StartsWith('{'))
            try
            {
                using var envelope = JsonDocument.Parse(candidate);
                if (Text(envelope.RootElement, "schema") is "assistant.agent.response.v1" or "go.ai.agent.response.v1"
                    && Text(envelope.RootElement, "type") == "message") content = Text(envelope.RootElement, "message");
            }
            catch (JsonException) { }
        var found = false;
        StringBuilder? current = null;
        char fence = '\0';
        var fenceLength = 0;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (fence == '\0' && trimmed == ScientificRunCompletionPolicy.PublicationBegin)
            {
                found = true;
                publication = "";
                current = new();
                continue;
            }
            if (fence == '\0' && trimmed == ScientificRunCompletionPolicy.PublicationEnd && current is not null)
            {
                publication = current.ToString().Trim();
                current = null;
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fence == '\0') { fence = trimmed[0]; fenceLength = trimmed.TakeWhile(character => character == trimmed[0]).Count(); }
                else if (trimmed[0] == fence && trimmed.Length >= fenceLength && trimmed.All(character => character == trimmed[0])) fence = '\0';
            }
            current?.AppendLine(line);
        }
        return found;
    }

    private sealed class ToolGroup(int assistantIndex, IReadOnlyList<LmToolCall> calls)
    {
        internal int AssistantIndex { get; } = assistantIndex;
        internal IReadOnlyList<LmToolCall> Calls { get; } = calls;
        internal List<int> Indices { get; } = [assistantIndex];
        internal Dictionary<string, List<int>> Receipts { get; } = new(StringComparer.Ordinal);
    }
}

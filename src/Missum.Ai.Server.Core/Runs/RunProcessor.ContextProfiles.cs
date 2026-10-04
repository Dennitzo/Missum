using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal static string? ResolveNewContextProfile(RunRequest request)
    {
        // Missing profile means the established representation. The client opts in
        // only after capability negotiation; an existing checkpoint wins on resume.
        if (request.ContextProfileVersion != ToolContextProfiles.Current
            || request.ConversationProfile is ConversationProfile.Audiobook or ConversationProfile.ContextPreparation)
            return null;
        if (request.ClientCapabilities?.Contains("research.deliverables", StringComparer.OrdinalIgnoreCase) == true
            && !ScientificStateCompletionPolicy.Enabled(request))
            return null; // Manuscript-marker clients cannot use section-delta instructions.
        return ToolContextProfiles.Current;
    }

    private static bool CanDescribeSubagents(RunRequest request) =>
        request.ConversationProfile != ConversationProfile.ContextPreparation
        && request.ClientCapabilities?.Contains("subagents", StringComparer.OrdinalIgnoreCase) == true;

    private static object? RejectedToolHelp(string name, IReadOnlyList<AgentToolSpec> tools) =>
        tools.FirstOrDefault(tool => tool.Name == name) is { } tool
            ? new { tool.Name, tool.Description, parameters = tool.Schema } : null;

    internal static void AppendRuntimeContext(List<LmChatMessage> messages, RunRequest request,
        bool subagentAvailable, bool beforeLatestUser = false)
    {
        var runtime = CompactAgentContextPolicy.RuntimeData(request, subagentAvailable);
        if (messages.LastOrDefault(message => message.Role == "user"
            && message.Content?.StartsWith(CompactAgentContextPolicy.RuntimeMarker, StringComparison.Ordinal) == true)?.Content == runtime)
            return;
        var index = beforeLatestUser ? messages.FindLastIndex(message => message.Role == "user"
            && !ModelRuntimeClient.IsLanguageReminder(message)
            && !ContextPlanner.IsNativeRuntimeInstruction(message)
            && message.Content?.StartsWith(CompactAgentContextPolicy.RuntimeMarker, StringComparison.Ordinal) != true) : -1;
        var data = new LmChatMessage("user", runtime);
        if (index >= 0) messages.Insert(index, data);
        else messages.Add(data);
    }

    // Only a full overview still present in the effective context can justify
    // an unchanged response. A persisted stamp alone is insufficient after compaction.
    internal static string? FindResearchOverviewStamp(IReadOnlyList<LmChatMessage> messages, string projectId)
    {
        var pendingReads = new HashSet<string>(StringComparer.Ordinal);
        string? latestStamp = null;
        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
                if (message.Role == "assistant" && call.Name == ClientToolNames.ResearchRead
                    && ScientificStateCompletionPolicy.Text(call.Arguments, "projectId") == projectId
                    && ScientificStateCompletionPolicy.Text(call.Arguments, "view") == "overview"
                    && !call.Arguments.TryGetProperty("cursor", out _)
                    && (!call.Arguments.TryGetProperty("offset", out var offset)
                        || offset.ValueKind == JsonValueKind.Number && offset.TryGetInt32(out var start) && start == 0)
                    && (!call.Arguments.TryGetProperty("ids", out var ids)
                        || ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() == 0))
                    pendingReads.Add(call.Id);
            if (message.Role != "tool" || message.ToolCallId is not { } callId || !pendingReads.Remove(callId)
                || string.IsNullOrWhiteSpace(message.Content)) continue;
            if (!ScientificStateCompletionPolicy.TryReceipt(message.Content, out var result, out var succeeded)
                || !succeeded || ScientificStateCompletionPolicy.Text(result, "projectId") != projectId
                || ScientificStateCompletionPolicy.Text(result, "view") != "overview"
                || ScientificStateCompletionPolicy.Boolean(result, "unchanged")
                || !result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
            if (ScientificStateCompletionPolicy.Text(result, "stateStamp") is { Length: 64 } stamp
                && stamp.All(char.IsAsciiHexDigit)) latestStamp = stamp;
        }
        return latestStamp;
    }

    internal static JsonElement CreateResearchStartArguments(RunRequest request, IReadOnlyList<LmChatMessage> messages,
        bool compactContext)
    {
        var projectId = ScientificStateCompletionPolicy.ProjectId(request);
        if (!compactContext && !ScientificStateCompletionPolicy.Enabled(request))
            return JsonSerializer.SerializeToElement(new { projectId });
        var arguments = new Dictionary<string, object?> { ["projectId"] = projectId, ["view"] = "overview" };
        if (FindResearchOverviewStamp(messages, projectId) is { } stamp) arguments["knownStateStamp"] = stamp;
        return JsonSerializer.SerializeToElement(arguments);
    }

    internal static bool IsEarlyResearchContextRead(LmChatResult response, RunRequest request,
        IReadOnlyList<AgentToolSpec> tools, AgentToolCatalog catalog, IReadOnlyList<LmChatMessage>? messages = null)
    {
        if (response.ToolCalls.Count != 1 || response.ToolCalls[0] is not { Name: ClientToolNames.ResearchRead } read)
            return false;
        try
        {
            catalog.Validate(catalog.Resolve(read.Name, tools), read.Arguments);
            return ScientificStateCompletionPolicy.Text(read.Arguments, "projectId") == ScientificStateCompletionPolicy.ProjectId(request)
                && ScientificStateCompletionPolicy.Text(read.Arguments, "view") is "overview" or "objects" or "task"
                && (messages is null || !HasCompletedResearchRead(messages, read));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        { return false; }
    }

    private static bool HasCompletedResearchRead(IReadOnlyList<LmChatMessage> messages, LmToolCall read)
    {
        var pending = new Dictionary<string, (bool MatchingRead, bool Update)>(StringComparer.Ordinal);
        var loaded = false;
        foreach (var message in messages)
        {
            if (message.Role == "assistant")
                foreach (var call in message.ToolCalls ?? [])
                    if (call.Name is ClientToolNames.ResearchRead or ClientToolNames.ResearchUpdate)
                        pending[call.Id] = (call.Name == ClientToolNames.ResearchRead && JsonElement.DeepEquals(call.Arguments, read.Arguments),
                            call.Name == ClientToolNames.ResearchUpdate);
            if (message.Role == "tool" && message.ToolCallId is { } id && pending.Remove(id, out var operation)
                && ScientificStateCompletionPolicy.TryReceipt(message.Content, out var result, out var completed)
                && completed
                && ScientificStateCompletionPolicy.Text(result, "projectId") == ScientificStateCompletionPolicy.Text(read.Arguments, "projectId"))
            {
                if (operation.MatchingRead) loaded = true;
                if (operation.Update && (ScientificStateCompletionPolicy.Boolean(result, "publicationChanged")
                    || result.TryGetProperty("changedIds", out var changes) && changes.ValueKind == JsonValueKind.Array && changes.GetArrayLength() > 0))
                    loaded = false; // Accepted state changes make an earlier read stale.
            }
        }
        return loaded;
    }
}

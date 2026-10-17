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
        return ToolContextProfiles.Current;
    }

    private static bool CanDescribeSubagents(RunRequest request) =>
        request.ConversationProfile != ConversationProfile.ContextPreparation
        && request.ClientCapabilities?.Contains("subagents", StringComparer.OrdinalIgnoreCase) == true;

    internal static bool HaveEquivalentContextTools(IReadOnlyList<LmToolDefinition> saved,
        IReadOnlyList<LmToolDefinition> current)
    {
        if (saved.Count != current.Count) return false;
        var candidates = new Dictionary<string, LmToolDefinition>(StringComparer.Ordinal);
        foreach (var tool in current)
            if (!candidates.TryAdd(tool.Name, tool)) return false;
        foreach (var tool in saved)
            if (!candidates.Remove(tool.Name, out var candidate)
                || tool.Description != candidate.Description
                || tool.ContextProfileVersion != candidate.ContextProfileVersion
                || !JsonElement.DeepEquals(tool.Parameters, candidate.Parameters)) return false;
        return true;
    }

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

}

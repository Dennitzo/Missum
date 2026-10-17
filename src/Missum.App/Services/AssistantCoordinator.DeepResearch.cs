using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    // Historical headers stay cheap. A pre-feature web.deepResearch operation is
    // hydrated only after the user explicitly opens its own activity view.
    private static List<SubagentChatState> ReadActivitySummaries(IEnumerable<ChatMessage> messages)
    {
        var parents = messages.ToArray();
        var states = SubagentChatState.ReadSummaries(parents).ToList();
        var identities = states.Select(state => state.AgentId).ToHashSet(StringComparer.Ordinal);
        foreach (var parent in parents)
            foreach (var step in (parent.ToolSteps ?? []).Where(step => step.Tool == "web.deepResearch"))
            {
                if (states.Any(state => !state.IsLegacyDeepResearch && state.OriginToolStepId == step.Id
                    && (state.OriginMessageId ?? state.ParentMessageId) == parent.Id)) continue;
                var agent = SubagentChatState.DeepResearchAgentId(parent.Id, step.Id);
                if (!identities.Add(agent)) continue;
                var state = SubagentChatState.FromDeepResearchTool(parent, step);
                states.Add(state with
                {
                    TranscriptDeferred = true,
                    UserMessage = state.UserMessage with { Content = "", ToolSteps = [] },
                    AssistantMessage = state.AssistantMessage with { Content = "", ToolSteps = [] },
                });
            }
        return states;
    }

    private async Task<SubagentChatState?> ReadHistoricalDeepResearchAsync(Guid sessionId, Guid parentId, string agentId,
        CancellationToken token)
    {
        if (!agentId.StartsWith(SubagentChatState.DeepResearchAgentPrefix, StringComparison.Ordinal)) return null;
        var parent = await chats.GetMessageTextAsync(parentId, token).ConfigureAwait(false);
        if (parent?.SessionId != sessionId) return null;
        var auxiliary = await conversationSnapshots.GetAuxiliaryMessagesAsync(sessionId, token).ConfigureAwait(false);
        var header = auxiliary.FirstOrDefault(message => message.Id == parentId)?.ToolSteps?.FirstOrDefault(step =>
            step.Tool == "web.deepResearch" && SubagentChatState.DeepResearchAgentId(parentId, step.Id) == agentId);
        if (header is null) return null;
        var original = await conversationSnapshots.GetToolStepAsync(sessionId, parentId, header.Id, token).ConfigureAwait(false);
        return original is null ? null : SubagentChatState.FromDeepResearchTool(parent, original);
    }
}

using System.Text.Json;
using Missum.App.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Shared display-cache allowance: 32 MiB native inactive projections plus
    // SubagentChatState's 16 Mi UTF-16 characters (32 MiB), 64 MiB in total.
    private const long InactiveProjectionBudget = 32L * 1024 * 1024;
    private long _inactiveProjectionBytes;

    private static long MessagePayloadBytes(JsonElement message) => S(message, "content").Length * 4L
        + Items(message, "toolSteps").Sum(step => (S(step, "detail").Length
            + S(step, "inputJson").Length + S(step, "outputJson").Length) * 4L + 512);

    private void EnforceInactiveProjectionBudget()
    {
        // The cache owns presentation only. Eviction never writes history,
        // prepared context or model KV data, and can always hydrate from SQLite.
        var candidates = _subagents.Values.Where(child => !ReferenceEquals(child, ActiveSubagent) && child.Messages.Count > 0 && child.HydrationTask is null)
            .OrderBy(child => child.ProjectionLastAccess).ThenBy(child => child.ObservationOrder).ToArray();
        _inactiveProjectionBytes = candidates.Sum(child => child.RetainedBytes);
        foreach (var child in candidates)
        {
            if (_inactiveProjectionBytes <= InactiveProjectionBudget && child.SessionId == _session) continue;
            _inactiveProjectionBytes -= child.RetainedBytes;
            ReleaseSubagentProjection(child);
        }
        // A live session follows its newest page, rather than retaining every
        // message produced over days. The DB cursor makes the evicted page
        // available again on upward scroll. Explicit reading/selection pins it.
        if (_conversationSelection?.IsSelecting != true && ActiveSubagent is null && _messages.Count > 96
            && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90)
        {
            var ordered = _messages.Values.OrderBy(MessageCreatedAt).ToArray();
            foreach (var message in ordered.Take(ordered.Length - 72))
            {
                var id = S(message, "id");
                if (S(message, "status") is "streaming" or "pending" || id.StartsWith("pending:", StringComparison.Ordinal)) continue;
                _messages.Remove(id); ReleaseMessageProjection(id);
            }
            var first = _messages.Values.OrderBy(MessageCreatedAt).First();
            _historyBefore = JsonSerializer.SerializeToElement(new { createdAt = MessageCreatedAt(first), id = S(first, "id") });
            _historyHasMore = true;
            _historyPagingInitialized = true;
        }
    }

    private void ReleaseSubagentProjection(NativeSubagentState child)
    {
        foreach (var id in child.Messages.Keys.ToArray()) ReleaseMessageProjection(id);
        child.Messages.Clear();
        child.RetainedBytes = 0;
        child.TranscriptDeferred = true;
        if (child.Snapshot.ValueKind == JsonValueKind.Object)
        {
            var metadata = child.Snapshot.EnumerateObject().Where(property => property.Name != "messages")
                .ToDictionary(property => property.Name, property => property.Value);
            metadata["transcriptDeferred"] = JsonSerializer.SerializeToElement(true);
            child.Snapshot = JsonSerializer.SerializeToElement(metadata);
        }
    }

    private void ReleaseMessageProjection(string id)
    {
        if (_messageViews.Remove(id, out var view)) MessagesPanel.Children.Remove(view.View);
        if (_virtualMessages.Remove(id, out var virtualized)) virtualized.Generation++;
        _liveMessageProjections.Remove(id); _renderedMessageVersions.Remove(id);
        _renderedMessageSources.Remove(id); _sourceMessageSignatures.Remove(id);
        _messageBlocks.Remove(id);
        foreach (var actionId in _messageActionViews.Where(pair => pair.Value.OwnerMessageId == id).Select(pair => pair.Key).ToArray())
            _messageActionViews.Remove(actionId);
        _thinkingIndicators.Remove(id); _thinkingStates.Remove(id); _modelPhases.Remove(id); _chatActivityStates.Remove(id);
        _toolStepWindows.Remove(id);
        _messageAuxiliarySteps.Remove(id);
    }
}

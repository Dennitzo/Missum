using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private JsonElement _historyBefore;
    private bool _historyHasMore, _historyLoading, _historyPagingInitialized;
    private long _historyGeneration;
    private long _toolDetailGeneration;
    private readonly SemaphoreSlim _toolDetailGate = new(2, 2);

    private void ResetHistoryPaging()
    {
        _historyGeneration++;
        _historyBefore = default;
        _historyHasMore = _historyLoading = _historyPagingInitialized = false;
    }

    private void ApplyHistoryPageCursor(JsonElement page)
    {
        _historyPagingInitialized = true;
        _historyHasMore = page.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True;
        _historyBefore = page.TryGetProperty("before", out var cursor) ? cursor.Clone() : default;
    }

    private async Task LoadEarlierMessagesAsync()
    {
        if (_disposed || _historyLoading || !_historyHasMore || _historyBefore.ValueKind != JsonValueKind.Object
            || ActiveSubagent is not null || HasPromptTimelineTarget || _conversationSelection?.IsSelecting == true) return;
        _historyLoading = true;
        var generation = _historyGeneration;
        try { await CommandAsync("conversation.page", new { sessionId = _session, before = _historyBefore }); }
        finally { if (generation == _historyGeneration) _historyLoading = false; }
    }

    private async Task<bool> LoadToolStepDetailsAsync(string messageId, string stepId)
    {
        var session = _session;
        var generation = _toolDetailGeneration;
        var child = _subagents.Values.FirstOrDefault(value => value.Messages.ContainsKey(messageId));
        try { await _toolDetailGate.WaitAsync(_lifetime.Token); }
        catch (OperationCanceledException) { return false; }
        try
        {
            if (_disposed || session != _session || generation != _toolDetailGeneration) return false;
            return await CommandAsync("tool.details", new { sessionId = session, messageId, stepId,
                parentMessageId = child is null ? null : S(child.Snapshot, "parentMessageId"), agentId = child?.AgentId });
        }
        finally { _toolDetailGate.Release(); }
    }

    private bool ApplyPagedHistoryEvent(string type, JsonElement data)
    {
        if (type is not ("conversation.prompts" or "conversation.promptPage" or "conversation.page" or "conversation.blocks.page" or "subagent.blocks.page" or "tool.details")) return false;
        if (S(data, "sessionId") != _session.ToString()) return true;
        if (type == "conversation.prompts") { ApplyPromptTimelineIndex(data); return true; }
        if (type == "conversation.promptPage" && (!HasPromptTimelineTarget || S(data, "promptId") != _promptNavigation!.Id
            || !long.TryParse(S(data, "navigationIntent"), out var intent) || intent != _promptNavigation.Intent)) return true;
        if (type is "conversation.blocks.page" or "subagent.blocks.page")
        {
            var id = S(data, "messageId");
            var messages = _messages.ContainsKey(id) ? _messages
                : _subagents.Values.FirstOrDefault(child => child.SessionId == _session && child.Messages.ContainsKey(id))?.Messages;
            if (messages is not null && messages.TryGetValue(id, out var message))
            {
                ProjectionFor(message).Prepend(Items(data, "steps"));
                if (data.TryGetProperty("window", out var window)) _toolStepWindows[id] = window.Clone();
                _renderedMessageSources.Remove(id);
                _sourceMessageSignatures.Remove(id);
                RenderMessages();
            }
            return true;
        }
        if (type is "conversation.page" or "conversation.promptPage")
        {
            // Anchor the actual visible control, so late image/math layout does not jump the viewport.
            FrameworkElement? anchor = null;
            double oldY = 0;
            foreach (var element in MessagesPanel.Children.OfType<FrameworkElement>().Where(_ => !HasPromptTimelineTarget))
            {
                var y = element.TransformToVisual(ConversationContent).TransformPoint(new Windows.Foundation.Point()).Y;
                if (y + element.ActualHeight >= ConversationScroll.VerticalOffset)
                { anchor = element; oldY = y; break; }
            }
            var offset = ConversationScroll.VerticalOffset;
            var incomingPage = Items(data, "messages").ToArray();
            var previousOldest = _messages.Values.Select(PromptMessageCursor).Where(cursor => cursor is not null).Cast<Missum.Core.Models.ConversationCursor>()
                .OrderBy(cursor => cursor.CreatedAt).ThenBy(cursor => cursor.Id.ToString("D"), StringComparer.Ordinal).FirstOrDefault();
            var previousHasMore = _historyHasMore;
            ObservePromptTimelinePage(data, incomingPage, type == "conversation.promptPage");
            foreach (var message in incomingPage)
            {
                var id = S(message, "id");
                if (!_messages.ContainsKey(id)) _messages[id] = message.Clone();
            }
            ObserveToolStepWindows(data);
            ApplyHistoryPageCursor(data);
            if (_messages.Count > 0)
            {
                var first = _messages.Values.OrderBy(MessageCreatedAt).ThenBy(message => S(message, "id"), StringComparer.Ordinal).First();
                _historyBefore = JsonSerializer.SerializeToElement(new { createdAt = MessageCreatedAt(first), id = S(first, "id") });
                if (previousOldest is not null && incomingPage.Length > 0 && ComparePromptCursor(PromptMessageCursor(first)!, previousOldest) >= 0)
                    _historyHasMore = previousHasMore;
                if (int.TryParse(S(data, "totalMessages"), out var total) && _messages.Count >= total) _historyHasMore = false;
            }
            foreach (var child in Items(data, "subagents")) ObserveSubagentSnapshot(child);
            _scrollNewSessionToLatest = false;
            RenderMessagesNow();
            if (anchor is not null && MessagesPanel.Children.Contains(anchor))
            {
                var newY = anchor.TransformToVisual(ConversationContent).TransformPoint(new Windows.Foundation.Point()).Y;
                ConversationScroll.ChangeView(null, offset + newY - oldY, null, true);
            }
            return true;
        }
        var messageId = S(data, "messageId");
        var owner = _messages.ContainsKey(messageId) ? _messages
            : _subagents.Values.FirstOrDefault(child => child.Messages.ContainsKey(messageId))?.Messages;
        if (owner is null || !owner.TryGetValue(messageId, out var current)) return true;
        var incoming = data.GetProperty("toolStep");
        var projection = ProjectionFor(current);
        var stored = projection.Steps.FirstOrDefault(step => S(step, "id") == S(incoming, "id"));
        if (stored.ValueKind != JsonValueKind.Object) return true;
        if (DateTimeOffset.TryParse(S(stored, "updatedAt"), out var storedAt)
            && DateTimeOffset.TryParse(S(incoming, "updatedAt"), out var loadedAt) && storedAt > loadedAt) return true;
        projection.PutStep(incoming.Clone());
        _renderedMessageSources.Remove(messageId);
        RenderMessages();
        return true;
    }
}

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
            || ActiveSubagent is not null || _conversationSelection?.IsSelecting == true) return;
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
        if (type is not ("conversation.page" or "tool.details")) return false;
        if (S(data, "sessionId") != _session.ToString()) return true;
        if (type == "conversation.page")
        {
            // Anchor the actual visible control, so late image/math layout does not jump the viewport.
            FrameworkElement? anchor = null;
            double oldY = 0;
            foreach (var element in MessagesPanel.Children.OfType<FrameworkElement>())
            {
                var y = element.TransformToVisual(ConversationContent).TransformPoint(new Windows.Foundation.Point()).Y;
                if (y + element.ActualHeight >= ConversationScroll.VerticalOffset)
                { anchor = element; oldY = y; break; }
            }
            var offset = ConversationScroll.VerticalOffset;
            foreach (var message in Items(data, "messages"))
            {
                var id = S(message, "id");
                if (!_messages.ContainsKey(id)) _messages[id] = message.Clone();
            }
            ApplyHistoryPageCursor(data);
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
        var steps = Items(current, "toolSteps").ToList();
        var position = steps.FindIndex(s => S(s, "id") == S(incoming, "id"));
        if (position < 0) return true;
        if (DateTimeOffset.TryParse(S(steps[position], "updatedAt"), out var storedAt)
            && DateTimeOffset.TryParse(S(incoming, "updatedAt"), out var loadedAt) && storedAt > loadedAt) return true;
        steps[position] = incoming.Clone();
        var merged = current.Deserialize<Dictionary<string, JsonElement>>()!;
        merged["toolSteps"] = JsonSerializer.SerializeToElement(steps, JsonOptions);
        owner[messageId] = JsonSerializer.SerializeToElement(merged, JsonOptions);
        RenderMessages();
        return true;
    }
}

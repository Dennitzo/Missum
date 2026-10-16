using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const int MessageStepBatch = 64;
    private readonly Dictionary<string, int> _messageStepStarts = new(StringComparer.Ordinal);
    private bool _messageWindowLoading;
    private double _lastConversationOffset;

    private int MessageStepStart(string id, JsonElement[] steps)
    {
        if (!_messageStepStarts.TryGetValue(id, out var start)) start = Math.Max(0, steps.Length - MessageStepBatch);
        start = Math.Min(start, steps.Length);
        // Keep a long-running answer bounded while following its live tail.
        // Reading or selecting older work never collapses it underneath the user.
        if (!_messageWindowLoading && steps.Length - start > MessageStepBatch * 2
            && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90
            && _conversationSelection?.IsSelecting != true)
            start = steps.Length - MessageStepBatch;
        _messageStepStarts[id] = start;
        return start;
    }

    private void QueueEarlierMessageBlocks(string id)
    {
        if (_messageWindowLoading || _conversationSelection?.IsSelecting == true
            || !_messageStepStarts.TryGetValue(id, out var start) || start <= 0) return;
        _messageWindowLoading = true;
        var owner = ConversationViewKey;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_disposed || owner != ConversationViewKey || !_messageViews.TryGetValue(id, out var entry)
                    || _conversationSelection?.IsSelecting == true) return;
                RunUiCallback("Conversation.EarlierBlocks", () =>
                {
                    var body = MessageBody(entry.View);
                    var button = _messageBlocks[id]["earlierBlocks"];
                    var position = body.Children.IndexOf(button);
                    var anchor = body.Children.Skip(position + 1).OfType<FrameworkElement>().FirstOrDefault();
                    var oldY = anchor?.TransformToVisual(ConversationContent).TransformPoint(new Point()).Y ?? 0;
                    var offset = ConversationScroll.VerticalOffset;
                    _messageStepStarts[id] = Math.Max(0, start - MessageStepBatch);
                    _renderedMessageSources.Remove(id);
                    _messageViews[id] = ("", entry.View);
                    _scrollNewSessionToLatest = false;
                    RenderMessagesNow();
                    if (anchor is not null && body.Children.Contains(anchor))
                    {
                        var newY = anchor.TransformToVisual(ConversationContent).TransformPoint(new Point()).Y;
                        ConversationScroll.ChangeView(null, offset + newY - oldY, null, true);
                    }
                });
            }
            finally { _messageWindowLoading = false; }
        })) _messageWindowLoading = false;
    }

    private bool LoadMessageBlocksOnUpwardScroll()
    {
        var offset = ConversationScroll.VerticalOffset;
        var upward = offset < _lastConversationOffset - .5;
        _lastConversationOffset = offset;
        if (!upward || _messageWindowLoading || _scrollNewSessionToLatest) return false;
        foreach (var (id, blocks) in _messageBlocks)
        {
            if (!DisplayMessages.ContainsKey(id) || !blocks.TryGetValue("earlierBlocks", out var button)) continue;
            var y = button.TransformToVisual(ConversationScroll).TransformPoint(new Point()).Y;
            if (y < -button.ActualHeight || y > 160) continue;
            QueueEarlierMessageBlocks(id);
            return true;
        }
        return false;
    }
}

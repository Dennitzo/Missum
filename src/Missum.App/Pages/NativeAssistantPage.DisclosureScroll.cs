using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private string? _readingToolConversation;
    private long _conversationScrollIntent;
    private readonly NativeAutoScrollGesture _autoScrollGesture = new();
    private bool _resumeTailOnManualScroll;

    private bool CanFollowConversationTail => _readingToolConversation != ConversationViewKey;
    private bool ShouldFollowConversationTail => CanFollowConversationTail && (_scrollNewSessionToLatest
        || _conversationSelection?.IsSelecting != true && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90);

    private void OnToolDisclosureInteraction()
    {
        // Explicitly opening a step means reading at this position. Cancel even
        // an already queued tail-follow so async details cannot pull it away.
        _readingToolConversation = ConversationViewKey;
        _scrollNewSessionToLatest = false;
        _autoScrollGesture.Reset();
        _resumeTailOnManualScroll = false;
        _conversationScrollIntent++;
    }

    private void OnConversationPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(ConversationScroll).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0
            || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift)
            || HasNestedScrollOwner(e.OriginalSource as DependencyObject)) return;
        if (TryHandleFollowingWheel(properties.MouseWheelDelta * 1.2)) e.Handled = true;
        else
        {
            OnToolDisclosureInteraction();
            _resumeTailOnManualScroll = properties.MouseWheelDelta < 0;
        }
    }

    private void OnConversationScrollSurfaceWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Padding and the scroll presenter are outside the content grid. Do not
        // process a content event twice as it bubbles through this fallback.
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != ConversationScroll;
             source = VisualTreeHelper.GetParent(source))
            if (source == ConversationContent) return;
        OnConversationPointerWheelChanged(sender, e);
    }

    private bool TryHandleFollowingWheel(double upwardDistance)
    {
        if (!ShouldFollowConversationTail || _conversationSelection?.IsSelecting == true) return false;
        _scrollNewSessionToLatest = true;
        var distance = _autoScrollGesture.Observe(upwardDistance, DateTimeOffset.UtcNow);
        if (distance <= 0) return true;
        OnToolDisclosureInteraction();
        ConversationScroll.ChangeView(null, Math.Max(0, ConversationScroll.VerticalOffset - distance), null, true);
        return true;
    }

    private bool HasNestedScrollOwner(DependencyObject? source)
    {
        for (var current = source; current is not null && current != ConversationScroll;
             current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer nested && nested.VerticalScrollMode != ScrollMode.Disabled
                && nested.ScrollableHeight > 0) return true;
        return false;
    }

    private void OnConversationScrollKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HasNestedScrollOwner(e.OriginalSource as DependencyObject)) return;
        if (e.Key is VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home)
            OnToolDisclosureInteraction();
        else if (e.Key is VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End)
        { OnToolDisclosureInteraction(); _resumeTailOnManualScroll = true; }
    }

    private void ScrollConversationToLatest()
    {
        if (!CanFollowConversationTail || _conversationSelection?.IsSelecting == true) return;
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
        SyncOuterScroll();
    }

    private sealed record ConversationViewportAnchor(FrameworkElement View, double Y, string Owner, long Intent);

    private ConversationViewportAnchor? CaptureConversationViewportAnchor()
    {
        if (ShouldFollowConversationTail || BodyGrid.Visibility != Visibility.Visible || IsPromptTimelineNavigationPending) return null;
        foreach (var message in MessagesPanel.Children.OfType<Border>())
        {
            if (!message.IsLoaded) continue;
            var messageY = message.TransformToVisual(ConversationScroll).TransformPoint(new()).Y;
            if (messageY + message.ActualHeight <= 0 || messageY >= ConversationScroll.ViewportHeight) continue;
            var candidates = MessageBody(message).Children.OfType<FrameworkElement>().SelectMany(child =>
                child is NativeVirtualizingBlockPanel panel ? panel.RealizedViews : [child]);
            foreach (var view in candidates)
            {
                if (!view.IsLoaded || view.Visibility != Visibility.Visible || view.ActualHeight <= 0) continue;
                var y = view.TransformToVisual(ConversationScroll).TransformPoint(new()).Y;
                if (y + view.ActualHeight <= 0 || y >= ConversationScroll.ViewportHeight) continue;
                return new(view, y, ConversationViewKey, _conversationScrollIntent);
            }
        }
        return null;
    }

    private void RestoreConversationViewportAnchor(ConversationViewportAnchor? anchor)
    {
        if (anchor is null || anchor.Owner != ConversationViewKey || anchor.Intent != _conversationScrollIntent
            || !anchor.View.IsLoaded || ShouldFollowConversationTail || _conversationSelection?.IsSelecting == true) return;
        var y = anchor.View.TransformToVisual(ConversationScroll).TransformPoint(new()).Y;
        if (Math.Abs(y - anchor.Y) > .5)
            ConversationScroll.ChangeView(null, Math.Clamp(ConversationScroll.VerticalOffset + y - anchor.Y,
                0, ConversationScroll.ScrollableHeight), null, true);
    }
}

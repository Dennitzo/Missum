using System.Text.RegularExpressions;
using System.Text.Json;
using Missum.App.Controls;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, PromptTimelineMarker> _promptTimelineMarkers = new(StringComparer.Ordinal);
    private string? _activePromptTimelineId;
    private string? _promptTimelineOwner;
    private readonly Dictionary<string, PromptTimelineEntry> _promptTimelineEntries = new(StringComparer.Ordinal);
    private string? _promptIndexLoadedOwner;
    private readonly HashSet<Guid> _promptIndicesLoading = [];
    private NativePromptNavigationTarget? _promptNavigation;
    private bool _promptNavigationLayoutAttached, _promptNavigationProjectionQueued;
    private Task? _promptTimelineNavigationTask;
    private readonly Dictionary<string, PromptHistoryGap> _promptHistoryGaps = new(StringComparer.Ordinal);
    private bool _promptHistoryGapLoading;
    private sealed record PromptHistoryGap(string Key, ConversationCursor Before, ConversationCursor After);
    private sealed record PromptTimelineEntry(string Id, DateTimeOffset CreatedAt, PromptTimelinePreviewContent Preview);
    private bool HasPromptTimelineTarget => _promptNavigation?.IsCurrent(ConversationViewKey, _conversationScrollIntent) == true;
    private bool IsPromptTimelineNavigationPending => HasPromptTimelineTarget;

    private void OnPromptTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RunUiCallback("PromptTimeline.SizeChanged", () => RenderPromptTimeline());

    private void RenderPromptTimeline(bool layoutCurrent = false)
    {
        if (PromptTimeline is null || ConversationContent is null || _disposed) return;
        if (_promptTimelineOwner != ConversationViewKey)
        {
            CancelPromptTimelineNavigation();
            _promptTimelineOwner = ConversationViewKey;
            _promptIndexLoadedOwner = null;
            _promptTimelineEntries.Clear();
            _promptHistoryGaps.Clear();
        }
        var messages = OrderedDisplayMessages().ToArray();
        var previews = PromptPreviewIndex(messages);
        foreach (var prompt in messages.Where(message => S(message, "role") == "user"
            && !string.IsNullOrWhiteSpace(MessageContent(message))))
        {
            var id = S(prompt, "id");
            _promptTimelineEntries[id] = new(id, MessageCreatedAt(prompt), previews[id]);
        }
        if (ActiveSubagent is null && !_selectionInputSmokeActive && _promptIndexLoadedOwner != ConversationViewKey)
            _ = LoadPromptTimelineIndexAsync();
        var prompts = _promptTimelineEntries.Values.OrderBy(prompt => prompt.CreatedAt).ThenBy(prompt => prompt.Id, StringComparer.Ordinal).ToArray();
        var visibility = prompts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PromptTimeline.Visibility != visibility) PromptTimeline.Visibility = visibility;
        if (prompts.Length == 0)
        {
            DismissPromptTimelinePreviews();
            PromptTimeline.Children.Clear();
            _promptTimelineMarkers.Clear();
            _activePromptTimelineId = null;
            return;
        }

        var promptIds = prompts.Select(prompt => prompt.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _promptTimelineMarkers.Keys
                     .Where(id => !promptIds.Contains(id)).ToArray())
        {
            if (ToolTipService.GetToolTip(_promptTimelineMarkers[stale].HitTarget) is ToolTip preview)
                preview.IsOpen = false;
            PromptTimeline.Children.Remove(_promptTimelineMarkers[stale].HitTarget);
            _promptTimelineMarkers.Remove(stale);
        }

        if (!layoutCurrent) ConversationContent.UpdateLayout();
        var available = Math.Max(0, PromptTimeline.ActualHeight - 14);
        for (var index = 0; index < prompts.Length; index++)
        {
            var prompt = prompts[index];
            var id = prompt.Id;
            var targetOffset = _messageViews.TryGetValue(id, out var messageView) && MessagesPanel.Children.Contains(messageView.View)
                ? MessageOffset(messageView.View) : double.NaN;
            if (!_promptTimelineMarkers.TryGetValue(id, out var marker))
            {
                var line = new Border
                {
                    Width = 24,
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Background = ThemeBrush("MissumMutedTextBrush", 0x82),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    RenderTransformOrigin = new Point(0, 0.5),
                };
                var scale = new ScaleTransform { ScaleX = 7d / 24d, ScaleY = 1 };
                line.RenderTransform = scale;
                var hitTarget = new Button
                {
                    Width = 30,
                    Height = 14,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Content = line,
                    CornerRadius = new CornerRadius(7),
                };
                hitTarget.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                hitTarget.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var capturedId = id;
                hitTarget.Click += (_, _) => ScrollToPrompt(capturedId);
                hitTarget.PointerEntered += (_, _) => SetTimelineMarkerPointer(capturedId, true);
                hitTarget.PointerExited += (_, _) => SetTimelineMarkerPointer(capturedId, false);
                // GettingFocus preserves the requested state. UIElement.FocusState
                // is coerced from Programmatic to Keyboard when the last input
                // device was a keyboard, which does not imply timeline navigation.
                hitTarget.GettingFocus += (_, args) => SetTimelineMarkerFocusRequest(capturedId, args.FocusState);
                hitTarget.RegisterPropertyChangedCallback(UIElement.FocusStateProperty,
                    (_, _) => RefreshTimelineMarkerFocus(capturedId));
                hitTarget.GotFocus += (_, _) => RefreshTimelineMarkerFocus(capturedId);
                hitTarget.LostFocus += (_, _) =>
                {
                    // Routed focus events can arrive after another transfer.
                    // Do not erase the intent of a newly regained focus owner.
                    if (hitTarget.FocusState == FocusState.Unfocused) ResetTimelineMarkerFocus(capturedId);
                };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(hitTarget,
                    $"Zu Prompt {index + 1} springen: {NativePromptTimelineState.PreviewText(prompt.Preview.Prompt, 80)}");
                marker = new PromptTimelineMarker(hitTarget, line, scale);
                _promptTimelineMarkers[id] = marker;
                PromptTimeline.Children.Add(hitTarget);
                ToolTipService.SetPlacement(hitTarget, PlacementMode.Right);
            }
            marker.TargetOffset = targetOffset;
            var stride = Math.Min(14, available / Math.Max(1, prompts.Length - 1));
            var top = (available - stride * (prompts.Length - 1)) / 2 + index * stride;
            if (marker.Top != top)
            {
                marker.Top = top;
                Canvas.SetTop(marker.HitTarget, top);
            }
            if (!marker.IsHovered)
            {
                var content = prompt.Preview with { Prompt = $"{index + 1}) " + prompt.Preview.Prompt };
                if (marker.PreviewContent == content) continue;
                var preview = new ToolTip { Content = CreatePromptPreview(content), Placement = PlacementMode.Right };
                preview.Opened += (_, _) =>
                {
                    // ToolTipService may also react to the coerced focus state.
                    // Only the actual pointer/keyboard intent owns this popup.
                    if (!_promptTimelineMarkers.TryGetValue(id, out var current) || !ReferenceEquals(current, marker)
                        || !marker.IsHovered || !ReferenceEquals(ToolTipService.GetToolTip(marker.HitTarget), preview))
                        preview.IsOpen = false;
                };
                ToolTipService.SetToolTip(marker.HitTarget, preview);
                marker.PreviewContent = content;
            }
        }
        UpdatePromptTimelineSelection();
        QueuePromptTimelineTargetProjection();
    }

    private readonly Dictionary<string, (long PromptVersion, string AnswerId, long AnswerVersion, PromptTimelinePreviewContent Content)> _promptPreviewCache = new(StringComparer.Ordinal);

    private Dictionary<string, PromptTimelinePreviewContent> PromptPreviewIndex(JsonElement[] messages)
    {
        var result = new Dictionary<string, PromptTimelinePreviewContent>(StringComparer.Ordinal);
        for (var index = 0; index < messages.Length; index++)
        {
            var prompt = messages[index];
            if (S(prompt, "role") != "user") continue;
            var id = S(prompt, "id");
            var answer = default(JsonElement);
            // Each message is visited at most once by the inner search: the
            // next prompt terminates this prompt's answer interval.
            for (var next = index + 1; next < messages.Length && S(messages[next], "role") != "user"; next++)
                if (S(messages[next], "role") == "assistant") { answer = messages[next]; break; }
            var answerId = S(answer, "id");
            var promptVersion = MessageVersion(prompt);
            var answerVersion = answer.ValueKind == JsonValueKind.Object ? MessageVersion(answer) : 0;
            if (!_promptPreviewCache.TryGetValue(id, out var previous) || previous.PromptVersion != promptVersion
                || previous.AnswerId != answerId || previous.AnswerVersion != answerVersion)
            {
                var content = new PromptTimelinePreviewContent(
                    NativePromptTimelineState.PreviewText(MessageContent(prompt), 118),
                    answer.ValueKind == JsonValueKind.Object ? NativePromptTimelineState.PreviewText(MessageContent(answer), 190) : "");
                previous = (promptVersion, answerId, answerVersion, content);
                _promptPreviewCache[id] = previous;
            }
            result[id] = previous.Content;
        }
        foreach (var stale in _promptPreviewCache.Keys.Where(id => !result.ContainsKey(id)).ToArray()) _promptPreviewCache.Remove(stale);
        return result;
    }

    private static Border CreatePromptPreview(PromptTimelinePreviewContent content)
    {
        var grid = new Grid { Width = 300, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 5 };
        text.Children.Add(new TextBlock
        {
            Text = content.Prompt,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        });
        var answerText = content.Answer;
        if (answerText.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = answerText,
                FontSize = 13,
                Foreground = ThemeBrush("MissumMutedTextBrush", 0x99),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
            });
        }
        grid.Children.Add(text);
        var bookmark = new FontIcon
        {
            Glyph = "\uE735",
            FontSize = 14,
            Foreground = NativeIconPalette.BrushFor("plan"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(bookmark, 1);
        grid.Children.Add(bookmark);
        return new Border
        {
            Background = ThemeBrush("MissumLayerStrongBrush", 0x32),
            BorderBrush = ThemeBrush("MissumStrokeBrush", 0x48),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 10, 12, 11),
            Child = grid,
        };
    }

    private double MessageOffset(FrameworkElement message)
    {
        try
        {
            return Math.Max(0, message.TransformToVisual(ConversationContent).TransformPoint(new Point(0, 0)).Y - 20);
        }
        catch (ArgumentException)
        {
            return 0;
        }
    }

    private void ScrollToPrompt(string id) => _promptTimelineNavigationTask = ScrollToPromptAsync(id);

    private async Task ScrollToPromptAsync(string id)
    {
        _conversationSelection?.Clear();
        OnToolDisclosureInteraction();
        var target = new NativePromptNavigationTarget(id, ConversationViewKey, _conversationScrollIntent);
        _promptNavigation = target;
        if (!_promptNavigationLayoutAttached)
        {
            ConversationContent.LayoutUpdated += OnPromptTimelineTargetLayout;
            _promptNavigationLayoutAttached = true;
        }
        _activePromptTimelineId = id;
        UpdatePromptTimelineSelection();
        if (!DisplayMessages.ContainsKey(id))
        {
            if (ActiveSubagent is { } child) await EnsureSubagentTranscriptAsync(child);
            else if (Guid.TryParse(id, out var promptId))
                await CommandAsync("conversation.promptPage", new { sessionId = _session, promptId, navigationIntent = target.Intent });
        }
        if (_disposed || !ReferenceEquals(_promptNavigation, target) || !target.IsCurrent(ConversationViewKey, _conversationScrollIntent)) return;
        if (!DisplayMessages.ContainsKey(id)) { CancelPromptTimelineNavigation(); return; }
        RenderMessagesNow();
        RefreshVirtualConversationViewport();
        QueuePromptTimelineTargetProjection();
    }

    private async Task LoadPromptTimelineIndexAsync()
    {
        var session = _session;
        var owner = ConversationViewKey;
        if (_disposed || session == Guid.Empty || ActiveSubagent is not null || !_promptIndicesLoading.Add(session)) return;
        try
        {
            if (await CommandAsync("conversation.prompts", new { sessionId = session }) && owner == ConversationViewKey)
                _promptIndexLoadedOwner = owner;
        }
        finally { _promptIndicesLoading.Remove(session); }
    }

    private void ApplyPromptTimelineIndex(JsonElement data)
    {
        if (ActiveSubagent is not null || S(data, "sessionId") != _session.ToString()) return;
        if (_promptTimelineOwner != ConversationViewKey)
        {
            _promptTimelineOwner = ConversationViewKey;
            _promptTimelineEntries.Clear();
        }
        foreach (var prompt in Items(data, "prompts"))
        {
            var id = S(prompt, "id");
            if (id.Length == 0 || _promptTimelineEntries.ContainsKey(id)) continue;
            _promptTimelineEntries[id] = new(id, MessageCreatedAt(prompt),
                new(NativePromptTimelineState.PreviewText(S(prompt, "preview"), 118), ""));
        }
        _promptIndexLoadedOwner = ConversationViewKey;
        RenderPromptTimeline();
    }

    private void OnPromptTimelineTargetLayout(object? sender, object args) => QueuePromptTimelineTargetProjection();

    private void QueuePromptTimelineTargetProjection()
    {
        if (_promptNavigation is null || _promptNavigationProjectionQueued) return;
        if (_disposed || !HasPromptTimelineTarget) { CancelPromptTimelineNavigation(); return; }
        _promptNavigationProjectionQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _promptNavigationProjectionQueued = false;
            if (_disposed || !HasPromptTimelineTarget) { CancelPromptTimelineNavigation(); return; }
            if (_conversationSelection?.IsSelecting == true || !_messageViews.TryGetValue(_promptNavigation!.Id, out var message)
                || !MessagesPanel.Children.Contains(message.View)) return;
            var offset = Math.Clamp(MessageOffset(message.View), 0, ConversationScroll.ScrollableHeight);
            if (Math.Abs(ConversationScroll.VerticalOffset - offset) > .5)
            {
                ConversationScroll.ChangeView(null, offset, null, disableAnimation: true);
                RefreshVirtualConversationViewport();
                SyncOuterScroll();
            }
            UpdatePromptTimelineSelection();
        });
    }

    private void CancelPromptTimelineNavigation()
    {
        _promptNavigation = null;
        if (!_promptNavigationLayoutAttached) return;
        ConversationContent.LayoutUpdated -= OnPromptTimelineTargetLayout;
        _promptNavigationLayoutAttached = false;
    }

    private static ConversationCursor? PromptMessageCursor(JsonElement message) => Guid.TryParse(S(message, "id"), out var id)
        && DateTimeOffset.TryParse(S(message, "createdAt"), out var at) ? new(at, id) : null;

    private static int ComparePromptCursor(ConversationCursor left, ConversationCursor right) => left.CreatedAt != right.CreatedAt
        ? left.CreatedAt.CompareTo(right.CreatedAt) : string.CompareOrdinal(left.Id.ToString("D"), right.Id.ToString("D"));

    private void ObservePromptTimelinePage(JsonElement data, JsonElement[] incoming, bool targeted)
    {
        var cursors = incoming.Select(PromptMessageCursor).Where(cursor => cursor is not null).Cast<ConversationCursor>()
            .OrderBy(cursor => cursor.CreatedAt).ThenBy(cursor => cursor.Id.ToString("D"), StringComparer.Ordinal).ToArray();
        var gapId = S(data, "timelineGap");
        if (gapId.Length > 0 && _promptHistoryGaps.TryGetValue(gapId, out var requested))
        {
            _promptHistoryGaps.Remove(gapId);
            if (cursors.Length > 0 && ComparePromptCursor(cursors[0], requested.After) > 0
                && ComparePromptCursor(cursors[0], requested.Before) < 0)
                _promptHistoryGaps[gapId] = requested with { Before = cursors[0] };
        }
        if (!targeted || cursors.Length == 0) return;
        var loaded = _messages.Values.Select(PromptMessageCursor).Where(cursor => cursor is not null).Cast<ConversationCursor>().ToArray();
        var older = loaded.Where(cursor => ComparePromptCursor(cursor, cursors[0]) < 0)
            .OrderByDescending(cursor => cursor.CreatedAt).ThenByDescending(cursor => cursor.Id.ToString("D"), StringComparer.Ordinal).FirstOrDefault();
        var newer = loaded.Where(cursor => ComparePromptCursor(cursor, cursors[^1]) > 0)
            .OrderBy(cursor => cursor.CreatedAt).ThenBy(cursor => cursor.Id.ToString("D"), StringComparer.Ordinal).FirstOrDefault();
        void AddGap(ConversationCursor after, ConversationCursor before)
        {
            var key = after.Id + ":" + before.Id;
            _promptHistoryGaps[key] = new(key, before, after);
        }
        if (older is not null) AddGap(older, cursors[0]);
        if (newer is not null) AddGap(cursors[^1], newer);
    }

    private bool LoadPromptTimelineGapNearViewport()
    {
        if (_disposed || _promptHistoryGapLoading || _historyLoading || HasPromptTimelineTarget || _scrollNewSessionToLatest
            || ActiveSubagent is not null || _conversationSelection?.IsSelecting == true || _promptTimelineOwner != ConversationViewKey) return false;
        foreach (var gap in _promptHistoryGaps.Values)
        {
            var near = false;
            if (_messageViews.TryGetValue(gap.After.Id.ToString(), out var older))
            {
                var bottom = MessageOffset(older.View) + older.View.ActualHeight + 20 - ConversationScroll.VerticalOffset;
                near = bottom >= -160 && bottom <= ConversationScroll.ViewportHeight + 480;
            }
            if (!near && _messageViews.TryGetValue(gap.Before.Id.ToString(), out var newer))
            {
                var top = MessageOffset(newer.View) + 20 - ConversationScroll.VerticalOffset;
                near = top >= -480 && top <= ConversationScroll.ViewportHeight + 160;
            }
            if (!near) continue;
            _ = LoadPromptTimelineGapAsync(gap);
            return true;
        }
        return false;
    }

    private async Task LoadPromptTimelineGapAsync(PromptHistoryGap gap)
    {
        _promptHistoryGapLoading = true;
        try { await CommandAsync("conversation.page", new { sessionId = _session, before = gap.Before, timelineGap = gap.Key }); }
        finally { _promptHistoryGapLoading = false; }
    }

    private void UpdatePromptTimelineSelection()
    {
        if (PromptTimeline is null || PromptTimeline.Visibility != Visibility.Visible) return;
        var active = HasPromptTimelineTarget ? _promptNavigation!.Id : NativePromptTimelineState.SelectActive(
            _promptTimelineMarkers.Select(item => (item.Key, item.Value.TargetOffset)),
            ConversationScroll.VerticalOffset, ConversationScroll.ViewportHeight, ConversationScroll.ScrollableHeight);
        _activePromptTimelineId = active;
        foreach (var (id, marker) in _promptTimelineMarkers)
        {
            var selected = string.Equals(id, active, StringComparison.Ordinal);
            if (marker.IsSelected != selected)
            {
                marker.IsSelected = selected;
                marker.Line.Background = selected ? ThemeBrush("MissumAccentBrush", 0xB0)
                    : ThemeBrush("MissumMutedTextBrush", 0x82);
            }
            SetTimelineMarkerWidth(id, marker.IsHovered ? 22 : selected ? 20 : 7, marker.IsHovered);
        }
    }

    private void SetTimelineMarkerPointer(string id, bool entered)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        marker.IsPointerOver = entered;
        UpdateTimelineMarkerPreview(id, marker);
    }

    private void SetTimelineMarkerFocus(string id, bool keyboardFocused)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        // WinUI relocates focus when a continuation button becomes disabled or
        // disappears. That automatic focus transfer is not timeline navigation.
        marker.IsKeyboardFocused = keyboardFocused;
        UpdateTimelineMarkerPreview(id, marker);
    }

    private void SetTimelineMarkerFocusRequest(string id, FocusState requested)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        marker.RequestedFocusState = requested;
        marker.HasKeyboardFocusIntent = requested == FocusState.Keyboard;
    }

    private void RefreshTimelineMarkerFocus(string id)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        if (marker.HitTarget.FocusState == FocusState.Unfocused)
        {
            ResetTimelineMarkerFocus(id);
            return;
        }
        SetTimelineMarkerFocus(id, marker.HasKeyboardFocusIntent && marker.HitTarget.FocusState == FocusState.Keyboard);
    }

    private void ResetTimelineMarkerFocus(string id)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        marker.RequestedFocusState = FocusState.Unfocused;
        marker.HasKeyboardFocusIntent = false;
        SetTimelineMarkerFocus(id, false);
    }

    private void DismissPromptTimelinePreviews()
    {
        foreach (var (id, marker) in _promptTimelineMarkers)
        {
            marker.IsPointerOver = false;
            marker.RequestedFocusState = FocusState.Unfocused;
            marker.HasKeyboardFocusIntent = false;
            marker.IsKeyboardFocused = false;
            UpdateTimelineMarkerPreview(id, marker);
        }
    }

    private void UpdateTimelineMarkerPreview(string id, PromptTimelineMarker marker)
    {
        var hovered = marker.IsPointerOver || marker.IsKeyboardFocused;
        marker.IsHovered = hovered;
        if (ToolTipService.GetToolTip(marker.HitTarget) is ToolTip preview && preview.IsOpen != hovered) preview.IsOpen = hovered;
        if (hovered)
        {
            SetTimelineMarkerWidth(id, 22, true);
            return;
        }
        UpdatePromptTimelineSelection();
    }

    private void SetTimelineMarkerWidth(string id, double width, bool hovered)
    {
        if (!_promptTimelineMarkers.TryGetValue(id, out var marker)) return;
        if (!hovered && string.Equals(id, _activePromptTimelineId, StringComparison.Ordinal)) width = 20;
        var targetScale = Math.Clamp(width / 24d, 0.1, 1);
        if (Math.Abs(marker.TargetScale - targetScale) < 0.001) return;
        marker.TargetScale = targetScale;
        var animation = new DoubleAnimation
        {
            To = targetScale,
            Duration = new Duration(TimeSpan.FromMilliseconds(hovered ? 120 : 150)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, marker.Scale);
        Storyboard.SetTargetProperty(animation, "ScaleX");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private readonly record struct PromptTimelinePreviewContent(string Prompt, string Answer);

    private sealed class PromptTimelineMarker(Button hitTarget, Border line, ScaleTransform scale)
    {
        public Button HitTarget { get; } = hitTarget;
        public Border Line { get; } = line;
        public ScaleTransform Scale { get; } = scale;
        public double TargetOffset { get; set; }
        public double? Top { get; set; }
        public bool? IsSelected { get; set; }
        public PromptTimelinePreviewContent? PreviewContent { get; set; }
        public double TargetScale { get; set; } = scale.ScaleX;
        public bool IsPointerOver { get; set; }
        public FocusState RequestedFocusState { get; set; } = FocusState.Unfocused;
        public bool HasKeyboardFocusIntent { get; set; }
        public bool IsKeyboardFocused { get; set; }
        public bool IsHovered { get; set; }
    }
}

internal static class NativePromptTimelineState
{
    internal static string PreviewText(string? value, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 2);
        // A tooltip needs only its first characters, not a regex pass over a
        // multi-day Science answer on every streamed update.
        var preview = new System.Text.StringBuilder(maximum);
        var space = false;
        foreach (var character in value ?? string.Empty)
        {
            if (char.IsWhiteSpace(character)) { space = preview.Length > 0; continue; }
            if (space) { preview.Append(' '); space = false; }
            preview.Append(character);
            if (preview.Length > maximum) return preview.ToString(0, maximum - 1) + "…";
        }
        return preview.ToString();
    }

    internal static string? SelectActive(IEnumerable<(string Id, double Offset)> prompts,
        double verticalOffset, double viewportHeight, double scrollableHeight)
    {
        var ordered = prompts.Where(item => double.IsFinite(item.Offset)).OrderBy(item => item.Offset).ToArray();
        if (ordered.Length == 0) return null;
        if (scrollableHeight - verticalOffset < 8) return ordered[^1].Id;
        var threshold = verticalOffset + Math.Min(96, viewportHeight * 0.3);
        return ordered.Where(item => item.Offset <= threshold).Select(item => item.Id).LastOrDefault()
            ?? ordered[0].Id;
    }
}

internal sealed record NativePromptNavigationTarget(string Id, string Owner, long Intent)
{
    internal bool IsCurrent(string owner, long intent) => Owner == owner && Intent == intent;
}

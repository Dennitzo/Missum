using System.Text.RegularExpressions;
using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, PromptTimelineMarker> _promptTimelineMarkers = new(StringComparer.Ordinal);
    private string? _activePromptTimelineId;

    private void OnPromptTimelineSizeChanged(object sender, SizeChangedEventArgs e) => RunUiCallback("PromptTimeline.SizeChanged", RenderPromptTimeline);

    private void RenderPromptTimeline()
    {
        if (PromptTimeline is null || ConversationContent is null || _disposed) return;
        var messages = DisplayMessages.Values.OrderBy(MessageCreatedAt).ToArray();
        var prompts = messages.Where(message => S(message, "role") == "user"
            && !string.IsNullOrWhiteSpace(S(message, "content"))).ToArray();
        PromptTimeline.Visibility = prompts.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (prompts.Length == 0)
        {
            DismissPromptTimelinePreviews();
            PromptTimeline.Children.Clear();
            _promptTimelineMarkers.Clear();
            _activePromptTimelineId = null;
            return;
        }

        foreach (var stale in _promptTimelineMarkers.Keys
                     .Where(id => prompts.All(prompt => S(prompt, "id") != id)).ToArray())
        {
            if (ToolTipService.GetToolTip(_promptTimelineMarkers[stale].HitTarget) is ToolTip preview)
                preview.IsOpen = false;
            PromptTimeline.Children.Remove(_promptTimelineMarkers[stale].HitTarget);
            _promptTimelineMarkers.Remove(stale);
        }

        ConversationContent.UpdateLayout();
        var available = Math.Max(0, PromptTimeline.ActualHeight - 14);
        for (var index = 0; index < prompts.Length; index++)
        {
            var prompt = prompts[index];
            var id = S(prompt, "id");
            if (!_messageViews.TryGetValue(id, out var messageView)) continue;
            var targetOffset = MessageOffset(messageView.View);
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
                    $"Zu Prompt {index + 1} springen: {NativePromptTimelineState.PreviewText(S(prompt, "content"), 80)}");
                marker = new PromptTimelineMarker(hitTarget, line, scale);
                _promptTimelineMarkers[id] = marker;
                PromptTimeline.Children.Add(hitTarget);
            }
            marker.TargetOffset = targetOffset;
            var stride = Math.Min(14, available / Math.Max(1, prompts.Length - 1));
            Canvas.SetTop(marker.HitTarget, (available - stride * (prompts.Length - 1)) / 2 + index * stride);
            ToolTipService.SetPlacement(marker.HitTarget, PlacementMode.Right);
            if (!marker.IsHovered)
            {
                var preview = new ToolTip { Content = CreatePromptPreview(messages, prompt), Placement = PlacementMode.Right };
                preview.Opened += (_, _) =>
                {
                    // ToolTipService may also react to the coerced focus state.
                    // Only the actual pointer/keyboard intent owns this popup.
                    if (!_promptTimelineMarkers.TryGetValue(id, out var current) || !ReferenceEquals(current, marker)
                        || !marker.IsHovered || !ReferenceEquals(ToolTipService.GetToolTip(marker.HitTarget), preview))
                        preview.IsOpen = false;
                };
                ToolTipService.SetToolTip(marker.HitTarget, preview);
            }
        }
        UpdatePromptTimelineSelection();
    }

    private static Border CreatePromptPreview(JsonElement[] messages, JsonElement prompt)
    {
        var promptId = S(prompt, "id");
        var absoluteIndex = Array.FindIndex(messages, message => S(message, "id") == promptId);
        var answer = absoluteIndex >= 0
            ? messages.Skip(absoluteIndex + 1).FirstOrDefault(message => S(message, "role") == "assistant")
            : default;
        var grid = new Grid { Width = 300, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 5 };
        text.Children.Add(new TextBlock
        {
            Text = $"{messages.Take(absoluteIndex + 1).Count(message => S(message, "role") == "user")}) " + NativePromptTimelineState.PreviewText(S(prompt, "content"), 118),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        });
        var answerText = NativePromptTimelineState.PreviewText(S(answer, "content"), 190);
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

    private void ScrollToPrompt(string id)
    {
        if (!_messageViews.TryGetValue(id, out var message)) return;
        ConversationContent.UpdateLayout();
        var offset = Math.Clamp(MessageOffset(message.View), 0, ConversationScroll.ScrollableHeight);
        ConversationScroll.ChangeView(null, offset, null, disableAnimation: false);
        _activePromptTimelineId = id;
        UpdatePromptTimelineSelection();
    }

    private void UpdatePromptTimelineSelection()
    {
        if (PromptTimeline is null || PromptTimeline.Visibility != Visibility.Visible) return;
        var active = NativePromptTimelineState.SelectActive(
            _promptTimelineMarkers.Select(item => (item.Key, item.Value.TargetOffset)),
            ConversationScroll.VerticalOffset, ConversationScroll.ViewportHeight, ConversationScroll.ScrollableHeight);
        _activePromptTimelineId = active;
        foreach (var (id, marker) in _promptTimelineMarkers)
        {
            var selected = string.Equals(id, active, StringComparison.Ordinal);
            marker.Line.Background = selected ? ThemeBrush("MissumAccentBrush", 0xB0)
                : ThemeBrush("MissumMutedTextBrush", 0x82);
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
        if (ToolTipService.GetToolTip(marker.HitTarget) is ToolTip preview) preview.IsOpen = hovered;
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

    private sealed class PromptTimelineMarker(Button hitTarget, Border line, ScaleTransform scale)
    {
        public Button HitTarget { get; } = hitTarget;
        public Border Line { get; } = line;
        public ScaleTransform Scale { get; } = scale;
        public double TargetOffset { get; set; }
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
        var normalized = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        return normalized.Length <= maximum ? normalized : normalized[..(maximum - 1)] + "…";
    }

    internal static string? SelectActive(IEnumerable<(string Id, double Offset)> prompts,
        double verticalOffset, double viewportHeight, double scrollableHeight)
    {
        var ordered = prompts.OrderBy(item => item.Offset).ToArray();
        if (ordered.Length == 0) return null;
        if (scrollableHeight - verticalOffset < 8) return ordered[^1].Id;
        var threshold = verticalOffset + Math.Min(96, viewportHeight * 0.3);
        return ordered.Where(item => item.Offset <= threshold).Select(item => item.Id).LastOrDefault()
            ?? ordered[0].Id;
    }
}

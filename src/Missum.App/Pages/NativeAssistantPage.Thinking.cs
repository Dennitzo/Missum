using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Keep run state independent of controls discarded during session navigation.
    private readonly Dictionary<string, NativeThinkingIndicatorState> _thinkingStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ThinkingIndicatorView> _thinkingIndicators = new(StringComparer.Ordinal);

    private void ObserveThinkingProgress(JsonElement data)
    {
        if (!data.TryGetProperty("generationState", out _)) return;
        var messageId = S(data, "messageId", S(data, "runMessageId"));
        if (messageId.Length == 0 && data.TryGetProperty("message", out var message)) messageId = S(message, "id");
        if (messageId.Length == 0) return;
        int? generated = data.TryGetProperty("generatedTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var count) ? count : null;
        DateTimeOffset? updated = DateTimeOffset.TryParse(S(data, "generationUpdatedAt"), out var at) ? at : null;
        if (!_thinkingStates.TryGetValue(messageId, out var state))
            _thinkingStates[messageId] = state = new NativeThinkingIndicatorState();
        state.ObserveProgress(messageId, S(data, "generationState"), generated, updated);
    }

    private void ClearConversationThinkingState()
    {
        foreach (var id in _messages.Keys) _thinkingStates.Remove(id);
    }

    // Token-only events never rebuild message blocks. Visibility is checked in
    // the existing timer; label changes share the header's once-per-second tick.
    private void RefreshThinkingIndicators(bool refreshTokens = false)
    {
        if (_thinkingIndicators.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var visible = IsLoaded && BodyGrid.Visibility == Visibility.Visible;
        var follow = visible && _conversationSelection?.HasSelection != true
            && ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 90;
        var changed = false;
        foreach (var (id, view) in _thinkingIndicators)
        {
            var show = _thinkingStates.TryGetValue(id, out var state)
                && state.ShouldShow(id, IsConversationMessageRunning(id) && view.IsMessageActive, view.HasBlockingTool, visible && DisplayMessages.ContainsKey(id), now);
            view.SetThinkingVisible(show);
            var wasVisible = view.Visibility == Visibility.Visible;
            // Reuse the token snapshot captured by the once-per-second header tick
            // so visibility changes do not bypass the thinking label's throttling.
            var tokens = _messageBlocks.TryGetValue(id, out var blocks) && blocks.TryGetValue("header", out var header)
                && header.Tag is double displayedTokens ? displayedTokens : DisplayContextUsed;
            if (show && (refreshTokens || !wasVisible)) view.UpdateTokens(tokens);
            if (show == wasVisible) continue;
            view.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            changed = true;
        }
        if (changed && follow)
        {
            ConversationContent.UpdateLayout();
            ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
            SyncOuterScroll();
        }
    }

    private sealed class ThinkingIndicatorView : Grid
    {
        private readonly Button _toggle;
        private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 10, Foreground = ThemeBrush("MissumMutedTextBrush", 140) };
        private readonly StackPanel _details = new() { Spacing = 8, Margin = new(28, 2, 0, 0), Visibility = Visibility.Collapsed };
        private readonly NativeStreamingMarkdown _reasoning = new("");
        private readonly TextBlock _empty = new() { Text = "Noch kein Denkprozess vom Modell übermittelt.", FontSize = 14,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextWrapping = TextWrapping.Wrap };
        private string _reasoningId = "";
        private string _reasoningText = "";
        private FrameworkElement? _historyView;
        internal bool IsExpanded => _details.Visibility == Visibility.Visible;
        internal Button ToggleButton => _toggle;
        internal NativeStreamingMarkdown ReasoningView => _reasoning;
        internal TextBlock Label { get; } = new()
        {
            FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        internal bool IsMessageActive { get; set; }
        internal bool HasBlockingTool { get; set; }

        internal ThinkingIndicatorView()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            Visibility = Visibility.Collapsed;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new FontIcon
            {
                Glyph = ToolIconGlyph("research"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ToolGlyphColor("research", "assistant.reasoning")),
            });
            Grid.SetColumn(Label, 1);
            row.Children.Add(Label);
            Grid.SetColumn(_chevron, 2); row.Children.Add(_chevron);
            _toggle = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(6, 5, 6, 5),
                BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            _toggle.Click += (_, _) => SetExpanded(!IsExpanded);
            _details.Children.Add(_reasoning); _details.Children.Add(_empty);
            Children.Add(new StackPanel { Spacing = 6, Children = { _toggle, _details } });
            UpdateAccessibility();
        }

        internal void UpdateReasoning(string id, string text, FrameworkElement? historyView)
        {
            if (!ReferenceEquals(_historyView, historyView) && _historyView is not null) _historyView.Visibility = Visibility.Visible;
            _historyView = historyView;
            if (id != _reasoningId) { _reasoningId = id; SetExpanded(false); }
            if (_reasoningText == text) return;
            _reasoningText = text;
            if (IsExpanded) RenderReasoning();
        }

        internal void SetThinkingVisible(bool visible)
        {
            // Keep one live disclosure. Its durable receipt returns to the
            // chronology once the answer starts or generation finishes.
            if (_historyView is not null) _historyView.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        internal void SetExpanded(bool expanded)
        {
            _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
            if (expanded) RenderReasoning();
            UpdateAccessibility();
        }

        private void RenderReasoning()
        {
            _reasoning.UpdateText(_reasoningText);
            _reasoning.Visibility = string.IsNullOrWhiteSpace(_reasoningText) ? Visibility.Collapsed : Visibility.Visible;
            _empty.Visibility = _reasoning.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void UpdateAccessibility() => AutomationProperties.SetName(_toggle,
            (Label.Text.Length == 0 ? "Denke nach" : Label.Text) + (IsExpanded ? ", ausgeklappt" : ", eingeklappt"));

        internal void UpdateTokens(double tokens)
        {
            var text = $"Denke nach · {tokens:N0} Token";
            if (Label.Text == text) return;
            Label.Text = text;
            UpdateAccessibility();
        }
    }
}

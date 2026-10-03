using System.Text.Json;
using Missum.App.Services;
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
            var wasVisible = view.Visibility == Visibility.Visible;
            // Reuse the published header value even when visibility changes
            // between ticks; the two labels must never display different counts.
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
        internal TextBlock Label { get; } = new()
        {
            FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        internal bool IsMessageActive { get; set; }
        internal bool HasBlockingTool { get; set; }

        internal ThinkingIndicatorView()
        {
            HorizontalAlignment = HorizontalAlignment.Left;
            Padding = new(6, 5, 6, 5);
            ColumnSpacing = 8;
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            Children.Add(new FontIcon
            {
                Glyph = ToolIconGlyph("research"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ToolGlyphColor("research", "assistant.reasoning")),
            });
            Grid.SetColumn(Label, 1);
            Children.Add(Label);
        }

        internal void UpdateTokens(double tokens)
        {
            var text = $"Denke nach · {tokens:N0} Token";
            if (Label.Text != text) Label.Text = text;
        }
    }
}

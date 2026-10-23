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
    private readonly Dictionary<string, NativeModelPhasePresentation> _modelPhases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeChatActivityState> _chatActivityStates = new(StringComparer.Ordinal);

    private void ResetToolDisclosureDefaults(IEnumerable<string> messageIds)
    {
        foreach (var id in messageIds)
            if (_virtualMessages.TryGetValue(id, out var state)) state.ExpandedSteps.Clear();
    }

    private bool WaitingForModel(string id, JsonElement message)
    {
        if (!_chatActivityStates.TryGetValue(id, out var state)) _chatActivityStates[id] = state = new();
        var visible = MessageSteps(message).LastOrDefault(step => S(step, "tool") is not
            ("assistant.reasoning" or "assistant.progress"));
        state.ObserveOutput(MessageContent(message), S(visible, "id") + ":" + S(visible, "status")
            + (S(visible, "tool") == "assistant.narration" ? S(visible, "detail") : ""),
            S(message, "status") is "streaming" or "pending");
        return state.Waiting;
    }

    private void InvalidateMessagePresentation(string id)
    {
        _renderedMessageSources.Remove(id);
        _renderedMessageVersions.Remove(id);
        if (_messageViews.TryGetValue(id, out var cached)) _messageViews[id] = ("", cached.View);
        RenderMessages();
    }

    private void ObserveThinkingProgress(JsonElement data)
    {
        if (!data.TryGetProperty("generationState", out _)) return;
        var messageId = S(data, "messageId", S(data, "runMessageId"));
        if (messageId.Length == 0 && data.TryGetProperty("message", out var message)) messageId = S(message, "id");
        if (messageId.Length == 0) return;
        if (DisplayMessages.TryGetValue(messageId, out var current)) WaitingForModel(messageId, current);
        if (!_chatActivityStates.TryGetValue(messageId, out var state)) _chatActivityStates[messageId] = state = new();
        DateTimeOffset? at = DateTimeOffset.TryParse(S(data, "generationUpdatedAt"), out var updated) ? updated : null;
        if (state.ObservePhase(S(data, "generationState"), at)) InvalidateMessagePresentation(messageId);
    }

    private void ClearConversationThinkingState()
    {
        foreach (var id in _messages.Keys) { _thinkingStates.Remove(id); _modelPhases.Remove(id); _chatActivityStates.Remove(id); }
    }

    private void RefreshThinkingIndicators(bool refreshTokens = false) { if (_disposed) return; }

    private sealed class ThinkingIndicatorView : Grid
    {
        internal event Action<bool>? ExpansionChanged;
        internal string ReasoningId { get; private set; } = "";
        internal bool IsProcessing { get; }
        internal bool IsExpanded { get; }
        internal Button ToggleButton { get; } = new() { Visibility = Visibility.Collapsed };
        internal NativeStreamingMarkdown ReasoningView { get; } = new("");
        internal string ExplanationText { get; } = "";
        internal TextBlock Label { get; } = new() { Text = "Denke nach", FontSize = 14,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160) };
        internal bool IsMessageActive { get; set; }
        internal ScrollViewer? ScrollOwner { get; set; }
        internal Func<bool>? PinSelection { get; set; }
        internal Action<Action>? Project { get; set; }
        internal Action? DisclosureInvoked { get; set; }
        internal bool HasBlockingTool { get; set; }
        internal ThinkingIndicatorView(bool initiallyExpanded = false) => Children.Add(Label);
        internal void UpdateReasoning(string id, string text, FrameworkElement? historyView, bool expansionDefault) => ReasoningId = id;
        internal void SetThinkingVisible(bool visible) => Label.Text = "Denke nach";
        internal void SetExpanded(bool expanded) => ExpansionChanged?.Invoke(false);
        internal void UpdateTokens(double tokens) => Label.Text = "Denke nach";
        internal void UpdatePhase(NativeModelPhasePresentation? phase, double tokens) => Label.Text = "Denke nach";
    }
}

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
    private bool? _observedToolExpansionDefault;

    private void ResetToolDisclosureDefaults(IEnumerable<string> messageIds)
    {
        var expanded = _settings.Current.CodingToolStepsExpanded;
        foreach (var id in messageIds)
        {
            if (_virtualMessages.TryGetValue(id, out var virtualized)) virtualized.ExpandedSteps.Clear();
            if (!_messageBlocks.TryGetValue(id, out var blocks)) continue;
            foreach (var entry in blocks.Values.ToArray())
                if ((entry is ActivitySlotView slot ? slot.Content : entry) is ToolStepView tool) tool.SetExpanded(expanded, forceDetails: false);
                else if (entry is ThinkingIndicatorView thinking) thinking.SetExpanded(expanded);
        }
    }

    private void ApplyToolExpansionDefaultIfChanged()
    {
        var expanded = _settings.Current.CodingToolStepsExpanded;
        if (_observedToolExpansionDefault == expanded) return;
        // Defer replacing selected detail runs, or projecting a hidden chat.
        // The unchanged observed value leaves the new default pending until
        // selection ends and the native conversation is visible again.
        if (!IsLoaded || BodyGrid.Visibility != Visibility.Visible || _conversationSelection?.IsSelecting == true) return;
        var initialized = _observedToolExpansionDefault.HasValue;
        _observedToolExpansionDefault = expanded;
        if (initialized)
            ResetToolDisclosureDefaults(DisplayMessages.Keys);
    }

    private void ObserveThinkingProgress(JsonElement data)
    {
        if (!data.TryGetProperty("generationState", out _)) return;
        var messageId = S(data, "messageId", S(data, "runMessageId"));
        if (messageId.Length == 0 && data.TryGetProperty("message", out var message)) messageId = S(message, "id");
        if (messageId.Length == 0) return;
        int? generated = data.TryGetProperty("generatedTokens", out var tokens) && tokens.ValueKind == JsonValueKind.Number
            && tokens.TryGetInt32(out var count) ? count : null;
        DateTimeOffset? updated = DateTimeOffset.TryParse(S(data, "generationUpdatedAt"), out var at) ? at : null;
        var priorPhase = _modelPhases.GetValueOrDefault(messageId);
        if (updated.HasValue && priorPhase?.UpdatedAt is { } priorUpdated && updated < priorUpdated) return;
        int? Count(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var count) ? count : null;
        double? fraction = data.TryGetProperty("promptProgress", out var progress) && progress.ValueKind == JsonValueKind.Number
            && progress.TryGetDouble(out var percentage) ? percentage : null;
        _modelPhases[messageId] = (priorPhase ?? new NativeModelPhasePresentation())
            .Observe(S(data, "generationState"), Count("processedPromptTokens"), Count("totalPromptTokens"), fraction, updated);
        if (!_thinkingStates.TryGetValue(messageId, out var state))
            _thinkingStates[messageId] = state = new NativeThinkingIndicatorState();
        state.ObserveProgress(messageId, S(data, "generationState"), generated, updated);
    }

    private void ClearConversationThinkingState()
    {
        foreach (var id in _messages.Keys) { _thinkingStates.Remove(id); _modelPhases.Remove(id); }
    }

    private void RefreshThinkingIndicators(bool refreshTokens = false) => ApplyToolExpansionDefaultIfChanged();

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

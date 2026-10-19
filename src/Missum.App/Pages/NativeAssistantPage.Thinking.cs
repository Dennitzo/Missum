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
            foreach (var view in blocks.Values.ToArray())
                if (view is ToolStepView tool) tool.SetExpanded(expanded, forceDetails: false);
                else if (view is ThinkingIndicatorView thinking) thinking.SetExpanded(expanded);
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

    // Token-only events never rebuild message blocks. Visibility is checked in
    // the existing timer; label changes share the header's once-per-second tick.
    private void RefreshThinkingIndicators(bool refreshTokens = false)
    {
        // The same timer observes an explicit checkbox change, including when
        // no thinking row is currently present. Ordinary ticks remain O(1).
        ApplyToolExpansionDefaultIfChanged();
        if (_thinkingIndicators.Count == 0 || _conversationSelection?.IsSelecting == true) return;
        var now = DateTimeOffset.UtcNow;
        var visible = IsLoaded && BodyGrid.Visibility == Visibility.Visible;
        var follow = visible && ShouldFollowConversationTail;
        ConversationViewportAnchor? viewportAnchor = null;
        var changed = false;
        foreach (var (id, view) in _thinkingIndicators)
        {
            if (_messageViews.TryGetValue(id, out var selected) && _conversationSelection?.PreserveSelectionWithin(selected.View) == true) continue;
            var phase = _modelPhases.GetValueOrDefault(id);
            var active = IsConversationMessageRunning(id) && view.IsMessageActive;
            var inConversation = visible && DisplayMessages.ContainsKey(id);
            var processing = phase?.IsProcessing == true;
            var show = processing ? active && inConversation && !view.HasBlockingTool
                : _thinkingStates.TryGetValue(id, out var state)
                    && state.ShouldShow(id, active, view.HasBlockingTool, inConversation, now);
            var wasVisible = view.Visibility == Visibility.Visible;
            var phaseHeightChanged = show && view.IsExpanded && view.IsProcessing != processing;
            if (!follow && (show != wasVisible || phaseHeightChanged))
                viewportAnchor ??= CaptureConversationViewportAnchor();
            view.SetThinkingVisible(show && !processing);
            // Reuse the token snapshot captured by the once-per-second header tick
            // so visibility changes do not bypass the thinking label's throttling.
            var tokens = _messageBlocks.TryGetValue(id, out var blocks) && blocks.TryGetValue("header", out var header)
                && header.Tag is double displayedTokens ? displayedTokens : DisplayContextUsed;
            if (show && (refreshTokens || !wasVisible || view.IsProcessing != processing))
                view.UpdatePhase(phase, tokens);
            if (phaseHeightChanged) changed = true;
            if (show == wasVisible) continue;
            view.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            changed = true;
        }
        if (changed) QueueConversationLayoutProjection(follow, viewportAnchor);
    }

    private sealed class ThinkingIndicatorView : Grid
    {
        private readonly Button _toggle;
        private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 10, Foreground = NativeIconPalette.BrushFor("navigation") };
        private readonly StackPanel _details = new() { Spacing = 8, Margin = new(28, 2, 0, 0), Visibility = Visibility.Collapsed };
        private readonly NativeStreamingMarkdown _reasoning = new("");
        private readonly TextBlock _empty = new() { Text = "Noch kein Denkprozess vom Modell übermittelt.", FontSize = 14,
            Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextWrapping = TextWrapping.Wrap };
        private string _reasoningId = "";
        private string _reasoningText = "";
        internal string ReasoningId => _reasoningId;
        private FrameworkElement? _historyView;
        private bool _thinkingVisible;
        internal event Action<bool>? ExpansionChanged;
        private string? _processingExplanation;
        internal bool IsProcessing => _processingExplanation is not null;
        internal bool IsExpanded => _details.Visibility == Visibility.Visible;
        internal Button ToggleButton => _toggle;
        internal NativeStreamingMarkdown ReasoningView => _reasoning;
        internal string ExplanationText => _empty.Text;
        internal TextBlock Label { get; } = new()
        {
            FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        internal bool IsMessageActive { get; set; }
        internal ScrollViewer? ScrollOwner { get; set; }
        internal Func<bool>? PinSelection { get; set; }
        internal Action<Action>? Project { get; set; }
        internal Action? DisclosureInvoked { get; set; }
        private NativeVirtualizingTextView? _largeReasoning;
        internal bool HasBlockingTool { get; set; }

        internal ThinkingIndicatorView(bool initiallyExpanded = false)
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
                Foreground = ToolGlyphBrush("research", "assistant.reasoning"),
            });
            Grid.SetColumn(Label, 1);
            row.Children.Add(Label);
            Grid.SetColumn(_chevron, 2); row.Children.Add(_chevron);
            _toggle = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Left,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(6, 5, 6, 5),
                BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            _toggle.Click += (_, _) => { DisclosureInvoked?.Invoke(); SetExpanded(!IsExpanded); };
            _details.Children.Add(_reasoning); _details.Children.Add(_empty);
            Children.Add(new StackPanel { Spacing = 6, Children = { _toggle, _details } });
            SetExpanded(initiallyExpanded);
        }

        internal void UpdateReasoning(string id, string text, FrameworkElement? historyView, bool expansionDefault)
        {
            if (!ReferenceEquals(_historyView, historyView) && _historyView is not null) _historyView.Visibility = Visibility.Visible;
            var historyChanged = !ReferenceEquals(_historyView, historyView);
            _historyView = historyView;
            var textChanged = _reasoningText != text;
            _reasoningText = text;
            // The live row is reused, but a disclosure belongs to one reasoning
            // receipt. Keep a manual choice across its deltas, never transfer it
            // into the next model turn or a newly generated tool step.
            if (id != _reasoningId)
            {
                _reasoningId = id;
                SetExpanded(historyView is ToolStepView next ? next.IsExpanded : expansionDefault);
                // The identity may change without changing the boolean. Persist
                // the new owner as well when the live row is virtualized.
                ExpansionChanged?.Invoke(IsExpanded);
            }
            else
            {
                if (historyChanged && historyView is ToolStepView history)
                    history.SetExpanded(IsExpanded, forceDetails: false);
                if (textChanged && IsExpanded) RenderReasoning();
            }
        }

        internal void SetThinkingVisible(bool visible)
        {
            // Keep one live disclosure. Its durable receipt returns to the
            // chronology once the answer starts or generation finishes.
            if (visible && !_thinkingVisible && _historyView is ToolStepView history)
                SetExpanded(history.IsExpanded);
            _thinkingVisible = visible;
            if (_historyView is not null) _historyView.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        }

        internal void SetExpanded(bool expanded)
        {
            var changed = IsExpanded != expanded;
            _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
            if (expanded) RenderReasoning();
            else if (_largeReasoning is not null)
            { _details.Children.Remove(_largeReasoning); _largeReasoning = null; }
            if (_historyView is ToolStepView history && history.IsExpanded != expanded)
                history.SetExpanded(expanded, forceDetails: false);
            UpdateAccessibility();
            if (changed) ExpansionChanged?.Invoke(expanded);
        }

        private void RenderReasoning()
        {
            if (IsProcessing)
            {
                if (_largeReasoning is not null) _largeReasoning.Visibility = Visibility.Collapsed;
                _reasoning.Visibility = Visibility.Collapsed;
                _empty.Text = _processingExplanation!;
                _empty.Visibility = Visibility.Visible;
                return;
            }
            if (_reasoningText.Length > 8192 && ScrollOwner is { } scroll)
            {
                if (_largeReasoning is null)
                {
                    _largeReasoning = new NativeVirtualizingTextView(scroll, PinSelection, Project);
                    _details.Children.Insert(0, _largeReasoning);
                }
                _largeReasoning.UpdateText(_reasoningText);
                _largeReasoning.Visibility = Visibility.Visible;
                _reasoning.Visibility = _empty.Visibility = Visibility.Collapsed;
                return;
            }
            if (_largeReasoning is not null) _largeReasoning.Visibility = Visibility.Collapsed;
            _reasoning.UpdateText(_reasoningText);
            _reasoning.Visibility = string.IsNullOrWhiteSpace(_reasoningText) ? Visibility.Collapsed : Visibility.Visible;
            _empty.Text = "Noch kein Denkprozess vom Modell übermittelt.";
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

        internal void UpdatePhase(NativeModelPhasePresentation? phase, double tokens)
        {
            var explanation = phase?.IsProcessing == true ? phase.ProcessingExplanation : null;
            var changed = _processingExplanation != explanation;
            _processingExplanation = explanation;
            if (explanation is not null)
            {
                var text = phase!.ProcessingLabel;
                if (Label.Text != text) { Label.Text = text; UpdateAccessibility(); }
            }
            else UpdateTokens(tokens);
            if (changed && IsExpanded) RenderReasoning();
        }
    }
}

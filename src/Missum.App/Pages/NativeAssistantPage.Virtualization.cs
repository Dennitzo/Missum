using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, VirtualMessageState> _virtualMessages = new(StringComparer.Ordinal);
    private sealed record NativeSourceAnchor(string MessageId, int Start, string Source);
    private sealed record LogicalBlock(string Key, string Kind, string Text, int Start, JsonElement Data,
        JsonElement[]? Artifacts = null, int Number = 0);
    private sealed class VirtualMessageState(NativeVirtualizingBlockPanel panel)
    {
        internal NativeVirtualizingBlockPanel Panel { get; } = panel;
        internal Dictionary<string, NativeTextBlockIndex> TextIndices { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, bool> ExpandedSteps { get; } = new(StringComparer.Ordinal);
        internal long Version { get; set; } = -1;
        internal bool Preparing { get; set; }
        internal long Generation { get; set; }
        internal IReadOnlyList<LogicalBlock> Blocks { get; set; } = [];
    }

    private bool UpdateVirtualMessageBlocks(string id, JsonElement message, StackPanel body)
    {
        var content = MessageContent(message);
        var steps = MessageSteps(message);
        var hasOlder = _toolStepWindows.TryGetValue(id, out var window) && S(window, "hasMore") == "True";
        if (!_virtualMessages.TryGetValue(id, out var state))
        {
            if (content.Length <= 8192 && steps.Length <= 128 && !hasOlder && DisplayMessages.Count <= 48) return false;
            var disclosureStates = _messageBlocks.TryGetValue(id, out var previousBlocks)
                ? previousBlocks.Where(pair => pair.Value is ToolStepView or ThinkingIndicatorView).ToDictionary(pair => pair.Key,
                    pair => pair.Value is ToolStepView tool ? tool.IsExpanded : ((ThinkingIndicatorView)pair.Value).IsExpanded, StringComparer.Ordinal) : [];
            body.Children.Clear();
            _messageBlocks[id] = new(StringComparer.Ordinal);
            var panel = new NativeVirtualizingBlockPanel
            {
                ScrollOwner = ConversationScroll,
                Project = projection => RunUiCallback("Conversation.VirtualViewport", projection),
                PinSelection = () => _conversationSelection?.IsSelecting == true,
                FollowLatest = () => _scrollNewSessionToLatest,
                EarlierRequested = () => { if (!_scrollNewSessionToLatest) _ = LoadEarlierToolBlocksAsync(id); },
                LayoutChanged = () =>
                {
                    _conversationSelection?.TrackSelectionWithin(body);
                    if (_scrollNewSessionToLatest) QueueSessionScrollToLatest();
                },
            };
            panel.RealizationChanged = (key, view) =>
            {
                if (!_messageBlocks.TryGetValue(id, out var blocks)) return;
                if (view is null)
                {
                    if (key == "thinkingIndicator" && blocks.TryGetValue(key, out var previous)
                        && _thinkingIndicators.TryGetValue(id, out var indicator) && ReferenceEquals(previous, indicator))
                        _thinkingIndicators.Remove(id);
                    blocks.Remove(key);
                }
                else { blocks[key] = view; _conversationSelection?.TrackSelectionWithin(view); }
            };
            body.Children.Add(panel);
            _virtualMessages[id] = state = new(panel);
            foreach (var (key, expanded) in disclosureStates) state.ExpandedSteps[key] = expanded;
        }
        if (!body.Children.Contains(state.Panel)) { body.Children.Clear(); body.Children.Add(state.Panel); }
        var version = MessageVersion(message);
        if (state.Version == version)
        {
            state.Panel.RefreshViewport();
            return true;
        }
        if (state.Preparing) return true;
        state.Preparing = true;
        var generation = ++state.Generation;
        var artifacts = VisibleNativeMessageArtifacts(Items(message, "artifacts"));
        var owner = ConversationViewKey;
        _ = PrepareVirtualMessageAsync(id, message, content, steps, artifacts, hasOlder, version, generation, owner, state);
        return true;
    }

    private async Task PrepareVirtualMessageAsync(string id, JsonElement message, string content, JsonElement[] steps,
        JsonElement[] artifacts, bool hasOlder, long version, long generation, string owner, VirtualMessageState state)
    {
        IReadOnlyList<LogicalBlock> logical;
        try
        {
            logical = await Task.Run(() => BuildLogicalBlocks(message, content, steps, artifacts, hasOlder, state.TextIndices),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                state.Preparing = false;
                if (!_disposed) ShowError("Der Nachrichtenabschnitt konnte nicht vorbereitet werden: " + exception.Message);
            });
            return;
        }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            state.Preparing = false;
            if (_disposed || owner != ConversationViewKey || generation != state.Generation
                || !_virtualMessages.TryGetValue(id, out var currentState) || !ReferenceEquals(state, currentState)
                || !DisplayMessages.TryGetValue(id, out var current)) return;
            if (MessageVersion(current) != version || _conversationSelection?.IsSelecting == true)
            { _renderedMessageSources.Remove(id); RenderMessages(); return; }
            state.Version = version;
            state.Blocks = logical;
            state.Panel.SetRows(logical.Select(block => VirtualRow(id, current, block)).ToArray());
            ObserveVirtualThinking(id, current, steps);
            _conversationSelection?.TrackSelectionWithin(state.Panel);
            QueueConversationLayoutProjection(ShouldFollowConversationTail);
        });
    }

    private static List<LogicalBlock> BuildLogicalBlocks(JsonElement message, string content,
        JsonElement[] steps, JsonElement[] artifacts, bool hasOlder, Dictionary<string, NativeTextBlockIndex> indices)
    {
        var result = new List<LogicalBlock>();
        var assistant = S(message, "role") == "assistant";
        if (assistant) result.Add(new("header", "header", "", 0, default));
        if (hasOlder) result.Add(new("earlierBlocks", "earlier", "Frühere Werkzeugschritte laden", 0, default));
        void Text(string key, string value, int offset)
        {
            if (value.Length == 0) return;
            if (!indices.TryGetValue(key, out var index)) indices[key] = index = new();
            index.Update(value);
            foreach (var block in index.Blocks)
                result.Add(new(key + ":" + block.Start, "text", block.Source, offset + block.Start, default));
        }
        if (!assistant) Text("userText", content, 0);
        else
        {
            var artifactGroups = artifacts.Where(artifact => S(artifact, "stepId").Length > 0)
                .GroupBy(artifact => S(artifact, "stepId"), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var loadedSteps = steps.Select(step => S(step, "id")).ToHashSet(StringComparer.Ordinal);
            var anchoredArtifacts = artifactGroups.Where(group => !loadedSteps.Contains(group.Key))
                .Select(group => (group.Key, Images: group.Value, Offset: NativeArtifactOffset(group.Value)))
                .Where(group => group.Offset.HasValue).OrderBy(group => group.Offset).ToArray();
            var artifactIndex = 0;
            var receipts = steps.Where(step => S(step, "tool") is "subagent" or "subagent.completed").ToArray();
            var offset = 0;
            var number = 0;
            void TextUntil(int next)
            {
                while (artifactIndex < anchoredArtifacts.Length && anchoredArtifacts[artifactIndex].Offset <= next)
                {
                    var anchor = anchoredArtifacts[artifactIndex++];
                    var position = Math.Clamp(anchor.Offset!.Value, offset, content.Length);
                    Text("text:" + offset, content[offset..position], offset);
                    offset = position;
                    result.Add(new("artifacts:step:" + anchor.Key, "artifacts", "", 0, default, anchor.Images));
                }
                Text("text:" + offset, content[offset..next], offset);
                offset = next;
            }
            foreach (var step in OrderNativeArtifactSteps(steps))
            {
                var next = TryNativeStepOffset(step, out var position) ? Math.Clamp(position, offset, content.Length) : offset;
                TextUntil(next);
                var stepId = S(step, "id");
                var tool = S(step, "tool");
                if (!IsDeepResearchReceipt(step) && !IsAcceptedSubagentManagementStep(step, receipts) && !IsAcceptedDeepResearchStep(step, receipts)
                    && (tool != "assistant.continuation" || S(step, "status") == "completed"))
                {
                    if (tool == "assistant.narration") Text("narration:" + stepId, S(step, "detail"), -1);
                    else result.Add(new("tool:" + stepId, tool is "subagent" or "subagent.completed" ? "subagent" : "tool",
                        "", 0, step, Number: tool == "assistant.reasoning" ? number : ++number));
                }
                if (artifactGroups.Remove(stepId, out var images))
                    result.Add(new("artifacts:step:" + stepId, "artifacts", "", 0, default, images));
            }
            TextUntil(content.Length);
            // Legacy anchored images without an original source offset wait
            // for their owner page; they must never move into the footer.
            var orphaned = artifacts.Where(artifact => S(artifact, "stepId").Length == 0).ToArray();
            if (orphaned.Length > 0) result.Add(new("artifacts", "artifacts", "", 0, default, orphaned));
            if (S(message, "error") is { Length: > 0 } error && error != content) Text("error", error, -1);
            if (IsResumableAssistantStatus(message)) result.Add(new("continuation", "continuation", "", 0, default));
            if (S(message, "status") is "streaming" or "pending") result.Add(new("thinkingIndicator", "thinking", "", 0, default));
        }
        result.Add(new("footer", "footer", "", 0, default));
        var activeTextKeys = result.Where(block => block.Kind == "text")
            .Select(block => block.Key[..block.Key.LastIndexOf(':')]).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in indices.Keys.Where(key => !activeTextKeys.Contains(key)).ToArray()) indices.Remove(stale);
        return result;
    }

    private static int? NativeArtifactOffset(JsonElement[] artifacts)
    {
        foreach (var artifact in artifacts)
        {
            var value = 0;
            if (artifact.TryGetProperty("contentOffset", out var offset) && offset.ValueKind == JsonValueKind.Number
                && offset.TryGetInt32(out value)) return value;
            if (artifact.TryGetProperty("metadata", out var metadata)
                && int.TryParse(S(metadata, "sourceContentOffset"), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value)) return value;
        }
        return null;
    }

    private NativeVirtualizingBlockPanel.Row VirtualRow(string messageId, JsonElement message, LogicalBlock block)
    {
        object version = block.Kind is "text" ? block.Text : block.Kind is "tool" or "subagent" ? block.Data : MessageVersion(message);
        var estimate = block.Kind switch { "text" => Math.Max(38, block.Text.Length / 85d * 26 + 12), "artifacts" => 360, _ => 44 };
        return new(block.Key, version, estimate,
            () => CreateVirtualBlock(messageId, message, block), view => UpdateVirtualBlock(view, messageId, message, block));
    }

    private FrameworkElement CreateVirtualBlock(string id, JsonElement message, LogicalBlock block)
    {
        FrameworkElement view;
        switch (block.Kind)
        {
            case "header":
                view = new StackPanel { Children = { new TextBlock { FontSize = 13,
                    Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextWrapping = TextWrapping.Wrap } } };
                break;
            case "text":
                var anchor = new NativeSourceAnchor(id, block.Start, block.Text);
                view = new ContentControl { Content = NativeVirtualizingTextView.CreateSourceBlock(block.Text, anchor),
                    Tag = anchor, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                break;
            case "artifacts": view = new NativeArtifactLinks(block.Artifacts ?? [], Guid.TryParse(id, out var guid) ? guid : null); break;
            case "subagent":
                var lifecycle = new SubagentLifecycleView(PlanetIdentities.GetOrAssign);
                lifecycle.SubagentRequested += async agent => { if (_subagents.TryGetValue(agent, out var child)) await ActivateSubagentTabAsync(child); };
                view = lifecycle;
                break;
            case "tool":
                var toolState = _virtualMessages[id];
                var expanded = toolState.ExpandedSteps.TryGetValue(block.Key, out var previousExpansion) ? previousExpansion
                    : _settings.Current.CodingToolStepsExpanded;
                var tool = new ToolStepView(expanded)
                {
                    DisclosureInvoked = OnToolDisclosureInteraction,
                    LoadDetails = () => LoadToolStepDetailsAsync(id, S(block.Data, "id")), ScrollOwner = ConversationScroll,
                    OpenResearch = () => OpenDeepResearchTabAsync(id, S(block.Data, "id")),
                    CanOpenResearch = () => FindDeepResearchView(id, S(block.Data, "id")) is not null,
                    ResearchIdentity = () => ResearchIdentity(id, S(block.Data, "id")),
                    PinSelection = () => _conversationSelection?.IsSelecting == true,
                    Project = projection => RunUiCallback("ToolStep.VirtualLargeReasoning", projection),
                };
                tool.ExpansionChanged += value => toolState.ExpandedSteps[block.Key] = value;
                tool.SubagentRequested += async agent => { if (_subagents.TryGetValue(agent, out var child)) await ActivateSubagentTabAsync(child); };
                view = tool;
                break;
            case "earlier":
                var earlier = new Button { Content = block.Text, HorizontalAlignment = HorizontalAlignment.Left };
                earlier.Click += async (_, _) => await LoadEarlierToolBlocksAsync(id);
                view = earlier;
                break;
            case "continuation":
                var continuation = new ContinuationToolStepView();
                continuation.ContinueButton.Click += async (_, _) => await ContinueAssistantMessageAsync(id);
                view = continuation;
                break;
            case "thinking":
                var thinkingState = _virtualMessages[id];
                var thinkingExpanded = thinkingState.ExpandedSteps.GetValueOrDefault(block.Key, _settings.Current.CodingToolStepsExpanded);
                var thinking = new ThinkingIndicatorView(thinkingExpanded)
                {
                    DisclosureInvoked = OnToolDisclosureInteraction,
                    IsMessageActive = true, ScrollOwner = ConversationScroll,
                    PinSelection = () => _conversationSelection?.IsSelecting == true,
                    Project = projection => RunUiCallback("Thinking.VirtualLargeReasoning", projection),
                };
                thinking.ExpansionChanged += value =>
                {
                    thinkingState.ExpandedSteps[block.Key] = value;
                    if (thinking.ReasoningId.Length > 0) thinkingState.ExpandedSteps["tool:" + thinking.ReasoningId] = value;
                };
                _thinkingIndicators[id] = thinking;
                view = thinking;
                break;
            default: view = MessageActionsFor(id, message); break;
        }
        UpdateVirtualBlock(view, id, message, block);
        return view;
    }

    private void UpdateVirtualBlock(FrameworkElement view, string id, JsonElement message, LogicalBlock block)
    {
        switch (view)
        {
            case ContentControl text when block.Kind == "text":
                var anchor = new NativeSourceAnchor(id, block.Start, block.Text);
                text.Tag = anchor;
                NativeVirtualizingTextView.UpdateSourceBlock(text, block.Text, anchor);
                break;
            case ToolStepView tool: tool.Update(block.Data, block.Number); break;
            case SubagentLifecycleView child: child.Update(block.Data, FindSubagentForStep(block.Data)); break;
            case NativeArtifactLinks artifacts: artifacts.UpdateArtifacts(block.Artifacts ?? []); break;
            case ContinuationToolStepView continuation: UpdateContinuationStep(id, message, continuation); break;
            case ThinkingIndicatorView thinking:
                var reasoning = MessageSteps(message).LastOrDefault(step => S(step, "tool") == "assistant.reasoning");
                var history = _messageBlocks.GetValueOrDefault(id)?.GetValueOrDefault("tool:" + S(reasoning, "id"));
                thinking.UpdateReasoning(S(reasoning, "id"), S(reasoning, "detail"), history);
                thinking.HasBlockingTool = MessageSteps(message).Any(step => S(step, "status") is "running" or "pending"
                    && S(step, "tool") is not ("assistant.reasoning" or "assistant.progress" or "assistant.narration"));
                break;
            case StackPanel header when block.Kind == "header": UpdateMessageHeader(header, message); break;
            default: if (block.Kind == "footer") UpdateMessageActions(id, message); break;
        }
    }

    private void ObserveVirtualThinking(string id, JsonElement message, JsonElement[] steps)
    {
        if (S(message, "status") is not ("streaming" or "pending"))
        { _thinkingIndicators.Remove(id); _thinkingStates.Remove(id); _modelPhases.Remove(id); return; }
        var reasoning = steps.LastOrDefault(step => S(step, "tool") == "assistant.reasoning");
        if (!_thinkingStates.TryGetValue(id, out var state)) _thinkingStates[id] = state = new();
        if (S(reasoning, "status") is "running" or "pending"
            && DateTimeOffset.TryParse(S(reasoning, "updatedAt"), out var updated))
            state.ObserveReasoning(id, S(reasoning, "id"), S(reasoning, "detail"), updated);
        state.ObserveContent(id, MessageContent(message), DateTimeOffset.UtcNow);
        RefreshThinkingIndicators();
    }

    private void RefreshVirtualConversationViewport()
    {
        foreach (var (id, state) in _virtualMessages)
            if (DisplayMessages.ContainsKey(id) && state.Panel.IsLoaded) state.Panel.QueueRefresh();
    }
}

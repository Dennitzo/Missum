using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, Dictionary<string, FrameworkElement>> _messageBlocks = new(StringComparer.Ordinal);

    private long _lastHeaderSecond = -1;

    private void UpdateRunDurations()
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_lastHeaderSecond == second) return;
        if (_conversationSelection?.IsSelecting == true) return;
        _lastHeaderSecond = second;
        EnforceInactiveProjectionBudget();
        foreach (var (id, blocks) in _messageBlocks)
            if (blocks.TryGetValue("header", out var header) && DisplayMessages.TryGetValue(id, out var message)
                && S(message, "status") is "streaming" or "pending")
                UpdateMessageHeader(header, message);
        RefreshContextDisplay();
        RefreshThinkingIndicators(refreshTokens: true);
        RefreshChatNotices();
        RefreshContinuationSteps();
    }

    private void UpdateMessageHeader(FrameworkElement header, JsonElement message)
    {
        var start = MessageCreatedAt(message);
        if (start == DateTimeOffset.MinValue) return;
        var active = S(message, "status") is "streaming" or "pending";
        var end = active ? DateTimeOffset.UtcNow : DateTimeOffset.TryParse(S(message, "updatedAt"), out var finished) ? finished : start;
        var elapsed = MessageActiveDuration(message, end);
        var duration = $"{(long)elapsed.TotalMinutes} Min. {elapsed.Seconds} Sek.";
        var text = active
            ? $"In Bearbeitung seit {duration}"
            : start.ToLocalTime().ToString("dd.MM.yyyy · HH:mm 'Uhr'", CultureInfo.CurrentCulture) + " · " + duration + " lang gearbeitet";
        if (active && (header.Tag is not double displayedTokens || displayedTokens != DisplayContextUsed)) header.Tag = DisplayContextUsed;
        var label = (TextBlock)((StackPanel)header).Children[0];
        if (label.Text != text) label.Text = text;
    }

    private void UpdateMessageBlocks(string messageId, JsonElement message, StackPanel panel)
    {
        // The message owns its footer, independently of mutable/recycled content.
        // Keep its visual parent and controls throughout streaming and virtualization.
        var messageActions = MessageActionsFor(messageId, message);
        if (!panel.Children.Contains(messageActions)) panel.Children.Add(messageActions);
        if (UpdateVirtualMessageBlocks(messageId, message, panel, messageActions)) return;
        if (!_messageBlocks.TryGetValue(messageId, out var blocks)) _messageBlocks[messageId] = blocks = new(StringComparer.Ordinal);
        var desired = new List<FrameworkElement>();
        var content = MessageContent(message);
        var assistant = S(message, "role") == "assistant";
        var visibleAnswer = new StringBuilder(content.TrimEnd());
        if (assistant)
        {
            if (!blocks.TryGetValue("header", out var header))
            {
                var row = new StackPanel { Spacing = 9 };
                row.Children.Add(new TextBlock { FontSize = 13, Foreground = ThemeBrush("MissumMutedTextBrush", 160), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
                blocks["header"] = header = row;
            }
            // Streaming deltas must not overwrite the once-per-second status header.
            if (((TextBlock)((StackPanel)header).Children[0]).Text.Length == 0 || S(message, "status") is not ("streaming" or "pending"))
                UpdateMessageHeader(header, message);
            desired.Add(header);
        }
        else
        {
            if (!blocks.TryGetValue("userText", out var userText)) blocks["userText"] = userText = new NativeStreamingMarkdown(content);
            else ((NativeStreamingMarkdown)userText).UpdateText(content);
            desired.Add(userText);
            content = "";
        }
        var liveStatus = S(message, "liveStatus");
        if (assistant && liveStatus.Length > 0)
        {
            if (!blocks.TryGetValue("liveStatus", out var statusView))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new ProgressRing { Width = 15, Height = 15, IsActive = true, Foreground = NativeIconPalette.BrushFor("navigation") });
                row.Children.Add(new TextBlock { FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160) });
                blocks["liveStatus"] = statusView = row;
            }
            ((TextBlock)((StackPanel)statusView).Children[1]).Text = liveStatus;
            desired.Add(statusView);
        }

        var offset = 0;
        var sequence = 0;
        var toolNumber = 0;
        var hasBlockingTool = false;
        void Text(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!blocks.TryGetValue(key, out var element)) blocks[key] = element = new NativeStreamingMarkdown(value);
            else ((NativeStreamingMarkdown)element).UpdateText(value);
            desired.Add(element);
        }
        var messageSteps = MessageSteps(message);
        var artifacts = VisibleNativeMessageArtifacts(Items(message, "artifacts"));
        var stepIds = messageSteps.Select(step => S(step, "id")).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        var stepArtifacts = artifacts.Where(artifact => stepIds.Contains(S(artifact, "stepId")))
            .GroupBy(artifact => S(artifact, "stepId"), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var renderedArtifactSteps = new HashSet<string>(StringComparer.Ordinal);
        void ArtifactLinks(string key, JsonElement[] items)
        {
            if (items.Length == 0) return;
            if (!blocks.TryGetValue(key, out var links))
                blocks[key] = links = new NativeArtifactLinks(items, Guid.TryParse(messageId, out var parsed) ? parsed : null);
            else ((NativeArtifactLinks)links).UpdateArtifacts(items);
            desired.Add(links);
        }
        void ArtifactsAfterStep(string id)
        {
            // The durable step, not the evolving answer length or file arrival
            // time, owns this position. Original recovery updates this same
            // container when a legacy thumbnail is replaced by its original.
            if (renderedArtifactSteps.Add(id) && stepArtifacts.TryGetValue(id, out var items))
                ArtifactLinks("artifacts:step:" + id, items);
        }
        var reasoningOwner = ActiveSubagent?.AgentId ?? "";
        var latestReasoning = messageSteps.LastOrDefault(step => S(step, "tool") == "assistant.reasoning"
            && NativeThinkingIndicatorState.IsOwnedStep(S(step, "agentId"), reasoningOwner));
        FrameworkElement? latestReasoningView = null;
        var subagentReceipts = messageSteps.Where(step => S(step, "tool") is "subagent" or "subagent.completed").ToArray();
        var positionedSteps = stepArtifacts.Count > 0 ? OrderNativeArtifactSteps(messageSteps) : messageSteps;
        var firstStep = MessageStepStart(messageId, positionedSteps);
        if (firstStep > 0)
        {
            if (!blocks.TryGetValue("earlierBlocks", out var earlier))
            {
                var button = new Button { HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += (_, _) => QueueEarlierMessageBlocks(messageId);
                blocks["earlierBlocks"] = earlier = button;
            }
            var label = $"Frühere Abschnitte laden · {firstStep:N0} Werkzeugschritte";
            if (!Equals(((Button)earlier).Content, label)) ((Button)earlier).Content = label;
            desired.Add(earlier);
            if (firstStep < positionedSteps.Length && TryNativeStepOffset(positionedSteps[firstStep], out var startOffset))
                offset = Math.Clamp(startOffset, 0, content.Length);
        }
        foreach (var step in positionedSteps.Skip(firstStep))
        {
            var next = TryNativeStepOffset(step, out var n) ? Math.Clamp(n, offset, content.Length) : offset;
            Text("text:" + offset, content[offset..next]); offset = next;
            var id = S(step, "id", "step:" + sequence++);
            var tool = S(step, "tool");
            if (IsDeepResearchReceipt(step)) { ArtifactsAfterStep(id); continue; }
            // The persisted child receipt owns its lifecycle row. Keep rejected
            // manager calls visible, and coalesce only calls for an accepted child.
            if (IsAcceptedSubagentManagementStep(step, subagentReceipts) || IsAcceptedDeepResearchStep(step, subagentReceipts))
            { ArtifactsAfterStep(id); continue; }
            if (tool is "subagent" or "subagent.completed")
            {
                var lifecycleKey = "tool:" + id;
                if (!blocks.TryGetValue(lifecycleKey, out var lifecycle) || lifecycle is not SubagentLifecycleView)
                {
                    var view = new SubagentLifecycleView(PlanetIdentities.GetOrAssign);
                    view.SubagentRequested += async agentId =>
                    {
                        if (_subagents.TryGetValue(agentId, out var child)) await ActivateSubagentTabAsync(child);
                    };
                    blocks[lifecycleKey] = lifecycle = view;
                }
                ((SubagentLifecycleView)lifecycle).Update(step, FindSubagentForStep(step));
                desired.Add(lifecycle);
                ArtifactsAfterStep(id);
                continue;
            }
            if (S(step, "status") is "running" or "pending" && NativeThinkingIndicatorState.IsOwnedStep(S(step, "agentId"), reasoningOwner)
                && tool is not ("assistant.reasoning" or "assistant.progress" or "assistant.narration" or "assistant.steering"))
                hasBlockingTool = true;
            var detail = S(step, "detail", S(step, "explanation"));
            if (tool == "assistant.steering")
            {
                if (string.IsNullOrWhiteSpace(detail)) { ArtifactsAfterStep(id); continue; }
                var steeringKey = "steering:" + id;
                if (!blocks.TryGetValue(steeringKey, out var steeringView))
                    blocks[steeringKey] = steeringView = CreateSteeringPromptView(messageId, message, step);
                UpdateSteeringPromptView((Border)steeringView, messageId, message, step);
                desired.Add(steeringView);
                ArtifactsAfterStep(id);
                continue;
            }
            if (tool == "assistant.narration")
            {
                Text("narration:" + id, detail);
                if (!string.IsNullOrWhiteSpace(detail)) visibleAnswer.Append('\0').Append(id).Append('\0').Append(detail.TrimEnd());
                ArtifactsAfterStep(id);
                continue;
            }
            // The preparation receipt stores an idempotent request before the
            // server accepts it. Only an accepted continuation is a visible step.
            if (tool == "assistant.continuation" && S(step, "status") != "completed") { ArtifactsAfterStep(id); continue; }
            var key = "tool:" + id;
            if (!blocks.TryGetValue(key, out var element))
            {
                var view = new ToolStepView(_settings.Current.CodingToolStepsExpanded)
                {
                    ScrollOwner = ConversationScroll, PinSelection = () => _conversationSelection?.IsSelecting == true,
                    Project = projection => RunUiCallback("ToolStep.LargeReasoning", projection),
                    DisclosureInvoked = OnToolDisclosureInteraction,
                };
                view.LoadDetails = () => LoadToolStepDetailsAsync(messageId, id);
                view.OpenResearch = () => OpenDeepResearchTabAsync(messageId, id);
                view.CanOpenResearch = () => FindDeepResearchView(messageId, id) is not null;
                view.ResearchIdentity = () => ResearchIdentity(messageId, id);
                view.SubagentRequested += async agentId =>
                {
                    if (_subagents.TryGetValue(agentId, out var child)) await ActivateSubagentTabAsync(child);
                };
                blocks[key] = element = view;
            }
            ((ToolStepView)element).Update(step, tool == "assistant.reasoning" ? toolNumber : ++toolNumber);
            if (tool == "assistant.reasoning") element.Visibility = Visibility.Visible;
            if (latestReasoning.ValueKind == JsonValueKind.Object && S(latestReasoning, "id") == id) latestReasoningView = element;
            desired.Add(element);
            ArtifactsAfterStep(id);
        }
        Text("text:" + offset, content[offset..]);
        if (assistant && latestReasoning.ValueKind == JsonValueKind.Object && S(latestReasoning, "status") is "running" or "pending"
            && DateTimeOffset.TryParse(S(latestReasoning, "updatedAt"), out var reasoningUpdatedAt))
        {
            if (!_thinkingStates.TryGetValue(messageId, out var reasoningState))
                _thinkingStates[messageId] = reasoningState = new NativeThinkingIndicatorState();
            reasoningState.ObserveReasoning(messageId, S(latestReasoning, "id"), S(latestReasoning, "detail"), reasoningUpdatedAt);
            if (!string.IsNullOrWhiteSpace(S(latestReasoning, "detail")))
                _modelPhases[messageId] = (_modelPhases.GetValueOrDefault(messageId) ?? new NativeModelPhasePresentation())
                    .ObserveReasoning(reasoningUpdatedAt);
        }
        if (assistant && _thinkingStates.TryGetValue(messageId, out var thinkingState)
            && thinkingState.ObserveContent(messageId, visibleAnswer.ToString(), DateTimeOffset.UtcNow))
            _modelPhases[messageId] = (_modelPhases.GetValueOrDefault(messageId) ?? new NativeModelPhasePresentation())
                .ObserveContent(DateTimeOffset.UtcNow);
        if (S(message, "error") is { Length: > 0 } error && error != content) Text("error", error);
        // Unassociated files keep their existing footer placement. In
        // particular, anchored images are never repeated in that collection.
        ArtifactLinks("artifacts", artifacts.Where(artifact => !stepArtifacts.ContainsKey(S(artifact, "stepId"))).ToArray());
        if (assistant && IsResumableAssistantStatus(message))
        {
            if (!blocks.TryGetValue("continuation", out var continuation))
            {
                var step = new ContinuationToolStepView();
                step.ContinueButton.Click += async (_, _) => await ContinueAssistantMessageAsync(messageId);
                blocks["continuation"] = continuation = step;
            }
            UpdateContinuationStep(messageId, message, (ContinuationToolStepView)continuation);
            desired.Add(continuation);
        }
        var streaming = assistant && S(message, "status") is "streaming" or "pending";
        if (streaming)
        {
            if (!blocks.TryGetValue("thinkingIndicator", out var thinking))
                blocks["thinkingIndicator"] = thinking = new ThinkingIndicatorView(_settings.Current.CodingToolStepsExpanded)
                {
                    ScrollOwner = ConversationScroll, PinSelection = () => _conversationSelection?.IsSelecting == true,
                    Project = projection => RunUiCallback("Thinking.LargeReasoning", projection),
                    DisclosureInvoked = OnToolDisclosureInteraction,
                };
            var indicator = (ThinkingIndicatorView)thinking;
            indicator.IsMessageActive = true;
            indicator.HasBlockingTool = hasBlockingTool || liveStatus.Length > 0;
            indicator.UpdateReasoning(S(latestReasoning, "id"), S(latestReasoning, "detail"), latestReasoningView,
                _settings.Current.CodingToolStepsExpanded);
            _thinkingIndicators[messageId] = indicator;
            desired.Add(indicator);
        }
        else { _thinkingIndicators.Remove(messageId); _thinkingStates.Remove(messageId); _modelPhases.Remove(messageId); }
        var desiredSet = desired.ToHashSet();
        foreach (var old in panel.Children.OfType<FrameworkElement>()
            .Where(child => !ReferenceEquals(child, messageActions) && !desiredSet.Contains(child)).ToArray()) panel.Children.Remove(old);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], desired[i])) continue;
            panel.Children.Remove(desired[i]); panel.Children.Insert(i, desired[i]);
        }
        foreach (var stale in blocks.Where(pair => !desiredSet.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
        {
            ReleaseSteeringPromptActions(messageId, blocks[stale]);
            blocks.Remove(stale);
        }
        RefreshThinkingIndicators();
    }

    private static JsonElement[] VisibleNativeMessageArtifacts(JsonElement[] artifacts)
    {
        var unique = artifacts.Where(artifact => Guid.TryParse(S(artifact, "id"), out var id) && id != Guid.Empty)
            .DistinctBy(artifact => Guid.Parse(S(artifact, "id"))).ToArray();
        var replaced = unique.Where(artifact => string.Equals(S(ResearchObject(artifact, "metadata"), "role"), "original", StringComparison.OrdinalIgnoreCase))
            .Select(artifact => S(ResearchObject(artifact, "metadata"), "replacesArtifactId"))
            .Where(id => Guid.TryParse(id, out _)).Select(Guid.Parse).ToHashSet();
        // A snapshot can briefly carry both the recovered original and its
        // legacy thumbnail. Their explicit relationship prevents a second card.
        return unique.Where(artifact => !replaced.Contains(Guid.Parse(S(artifact, "id")))
            || !string.Equals(S(ResearchObject(artifact, "metadata"), "role"), "thumbnail", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private static bool TryNativeStepOffset(JsonElement step, out int offset)
    {
        offset = 0;
        return step.ValueKind == JsonValueKind.Object && step.TryGetProperty("contentOffset", out var position)
            && position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out offset) && offset >= 0;
    }

    private static JsonElement[] OrderNativeArtifactSteps(JsonElement[] steps)
    {
        // Live receipts can be appended after newer persisted receipts. Sort
        // only positioned slots, so their offsets cannot be clamped forward by
        // an unrelated later receipt. Unpositioned slots and offset ties retain
        // their existing order; no chronology is invented for unknown offsets.
        var positioned = steps.Select((step, index) => (Step: step, Index: index,
            Offset: TryNativeStepOffset(step, out var offset) ? (int?)offset : null))
            .Where(item => item.Offset is not null).ToArray();
        if (positioned.Length < 2) return steps;
        var sorted = positioned.OrderBy(item => item.Offset).ThenBy(item => item.Index).ToArray();
        var result = steps.ToArray();
        for (var index = 0; index < positioned.Length; index++) result[positioned[index].Index] = sorted[index].Step;
        return result;
    }

    private sealed class ToolStepView : Grid
    {
        internal Func<Task<bool>>? LoadDetails { get; set; }
        internal ScrollViewer? ScrollOwner { get; set; }
        internal Func<bool>? PinSelection { get; set; }
        internal Action<Action>? Project { get; set; }
        internal Action? DisclosureInvoked { get; set; }
        private NativeVirtualizingTextView? _largeReasoning;
        private bool _loadingDetails;
        private bool _detailsInViewport;
        internal event Action<string>? SubagentRequested;
        internal Func<Task<bool>>? OpenResearch { get; set; }
        internal Func<bool>? CanOpenResearch { get; set; }
        internal Func<(string AgentId, int PlanetIndex, string Title)?>? ResearchIdentity { get; set; }
        internal Task? NavigationTask { get; private set; }
        internal event Action<bool>? ExpansionChanged;
        private readonly TextBlock _title = new() { FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _added = new() { FontSize = 13, Foreground = ThemeBrush("MissumSuccessBrush", 160) };
        private readonly TextBlock _removed = new() { FontSize = 13, Foreground = ThemeBrush("MissumDangerBrush", 160) };
        private readonly StackPanel _counts = new() { Orientation = Orientation.Horizontal, Spacing = 7, Visibility = Visibility.Collapsed };
        private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 10, Foreground = NativeIconPalette.BrushFor("navigation") };
        private readonly TextBlock _status = new();
        private readonly FontIcon _compactIcon = new() { Glyph = "\uE70F", FontSize = 14, Foreground = ToolGlyphBrush("tool", ""), VerticalAlignment = VerticalAlignment.Center };
        private NativeSubagentAvatar? _researchAvatar;
        private readonly Grid _headerRow = new() { ColumnSpacing = 8 };
        private JsonElement _step;
        private string _diff = "";
        private bool _detailsDirty;
        private readonly bool _initiallyExpanded;
        internal bool IsExpanded => _details.Visibility == Visibility.Visible;
        private bool _reasoning;
        private readonly StackPanel _details = new() { Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly Button _toggle;
        private bool _hasStep;
        private string _label = "";
        private string _filePath = "";
        private string _summary = "";
        private bool _fileMutation;
        private bool _running;
        private bool _hasCounts;

        public ToolStepView(bool initiallyExpanded = false)
        {
            _initiallyExpanded = initiallyExpanded;
            var row = _headerRow;
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(_compactIcon);
            Grid.SetColumn(_title, 1); row.Children.Add(_title);
            _counts.Children.Add(_added); _counts.Children.Add(_removed);
            Grid.SetColumn(_counts, 2); row.Children.Add(_counts);
            Grid.SetColumn(_chevron, 3); row.Children.Add(_chevron);
            _toggle = new Button { Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new(6, 5, 6, 5), BorderThickness = new(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            _toggle.Click += async (_, _) =>
            {
                if (IsResearchNavigation)
                {
                    NavigationTask = OpenResearch?.Invoke() ?? Task.FromResult(false);
                    await NavigationTask;
                }
                else { DisclosureInvoked?.Invoke(); SetExpanded(_details.Visibility != Visibility.Visible); }
            };
            var content = new StackPanel { Spacing = 10 };
            _details.Margin = new(24, 0, 0, 4);
            content.Children.Add(_toggle); content.Children.Add(_details);
            Children.Add(content);
            EffectiveViewportChanged += (sender, args) =>
            {
                var viewport = args.EffectiveViewport;
                _detailsInViewport = viewport.Width > 0 && viewport.Height > 0
                    && viewport.Right > 0 && viewport.Bottom > 0 && viewport.X < ActualWidth && viewport.Y < ActualHeight;
                if (_detailsInViewport && IsExpanded && IsTrue(_step, "detailsDeferred")) _ = LoadDetailsAsync();
            };
        }

        internal void SetExpanded(bool expanded, bool forceDetails = true)
        {
            if (IsResearchNavigation) { _details.Visibility = Visibility.Collapsed; UpdateHeader(); return; }
            var changed = IsExpanded != expanded;
            _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded && IsTrue(_step, "detailsDeferred")) _ = LoadDetailsAsync(force: forceDetails);
            else if (expanded) RenderDetails();
            else { _details.Children.Clear(); _largeReasoning = null; _detailsDirty = true; }
            UpdateHeader();
            if (changed) ExpansionChanged?.Invoke(expanded);
        }

        public void Update(JsonElement step, int number = 1)
        {
            if (_step.Equals(step)) return;
            var firstUpdate = !_hasStep;
            // Stable receipt identity is sufficient: a changed JsonElement must
            // be projected even if it has no timestamp or its text length stays
            // the same. Never duplicate a megabyte reasoning/output as raw JSON.
            _hasStep = true;
            _step = step.Clone();
            _detailsDirty = true;
            var tool = S(step, "tool");
            _fileMutation = tool is "coding.write" or "coding.edit" or "coding.undo";
            _label = tool switch {
                "subagent" or "subagent.spawn" => "Subagent", "subagent.wait" => "Subagent-Ergebnis",
                "coding.read" => "Datei lesen", "coding.write" or "coding.edit" or "coding.undo" => "Datei bearbeiten",
                "coding.command" => "Befehl ausgeführt", "coding.list" => "Dateien aufgelistet", "coding.search" => "Dateien durchsucht",
                "coding.gitDiff" => "Änderungen geprüft", "coding.updatePlan" => "Arbeitsplan aktualisiert", "assistant.reasoning" => "Denkprozess",
                "research.code.write" => "Python-Datei vorbereiten", "research.code.execute" => "Python-Analyse ausführen",
                "research.code.test" => "Berechnung prüfen", "research.code.benchmark" => "Berechnung vergleichen",
                "research.deliverables.verify" => "Forschungsergebnisse prüfen",
                "research.read" => "Forschungsstand lesen", "research.update" => "Forschungsstand ergänzen",
                "research.plan" => "Recherche planen", "research.synthesize" => "Rechercheergebnis aufarbeiten",
                "research.skeptic" => "Ergebnisse kritisch prüfen", "research.verify" => "Belege bewerten",
                "research.model" => "Recherche bearbeiten", "research.operation" => "Rechercheauftrag",
                "math.formalProof" => "Lean-Beweis prüfen", "assistant.progress" => "Fortschritt",
                "assistant.continuation" => S(step, "status") switch
                {
                    "completed" => "Lauf fortgesetzt", "failed" or "denied" => "Fortsetzen fehlgeschlagen",
                    "cancelled" or "interrupted" => "Fortsetzung abgebrochen", _ => "Fortsetzung wird vorbereitet",
                },
                "web.search" => "Websuche", "web.fetch" => "Webseite lesen", "web.deepResearch" => "Deep Research", _ => S(step, "label", tool) };
            _running = S(step, "status") is "running" or "pending";
            var iconKey = ToolStepIconKey(tool);
            _compactIcon.Glyph = ToolIconGlyph(iconKey);
            _compactIcon.Foreground = ToolGlyphBrush(iconKey, tool);
            _reasoning = tool == "assistant.reasoning";
            _status.Text = S(step, "status") switch { "completed" => "Abgeschlossen", "failed" => "Fehlgeschlagen", "denied" => "Abgelehnt", "cancelled" => "Abgebrochen", "interrupted" => "Unterbrochen", "steered" => "Umgelenkt", "pending" => "Wartet", _ => "In Bearbeitung" };
            if (IsResearchNavigation)
            {
                // This receipt opens the real research transcript. Its nested
                // operations use the same normal message/tool renderer there.
                _details.Visibility = Visibility.Collapsed;
                _filePath = _diff = _summary = ""; _hasCounts = false;
                UpdateHeader(); return;
            }
            if (firstUpdate) _details.Visibility = _initiallyExpanded ? Visibility.Visible : Visibility.Collapsed;
            if (IsTrue(step, "detailsDeferred"))
            {
                _filePath = _diff = "";
                _hasCounts = false;
                _summary = "";
                // Header metadata is intentionally independent of the lazy
                // code/output bodies, including receipts loaded from history.
                if (step.TryGetProperty("displayHeader", out var headerJson)
                    && headerJson.ValueKind == JsonValueKind.Object
                    && headerJson.Deserialize<ToolStepDisplayHeader>(JsonOptions) is { Version: AssistantToolStepHeaders.CurrentVersion } header)
                {
                    _filePath = header.FilePath ?? "";
                    _summary = header.Summary ?? "";
                    if (_fileMutation && header.AddedLines is { } added && header.RemovedLines is { } removed)
                    {
                        _hasCounts = true;
                        _added.Text = $"+{added:N0}";
                        _removed.Text = $"−{removed:N0}";
                    }
                }
                if (IsExpanded) _ = LoadDetailsAsync();
                UpdateHeader();
                return;
            }
            if (_reasoning)
            {
                _filePath = _diff = _summary = "";
                _hasCounts = false;
                if (_running) _status.Text = "Denkt nach";
                if (IsExpanded) RenderDetails();
                UpdateHeader();
                return;
            }
            var detail = S(step, "detail", S(step, "explanation"));
            var diffs = Regex.Matches(detail, @"```diff\s*\n(?<diff>[\s\S]*?)(?:```|\z)", RegexOptions.CultureInvariant);
            var output = ReadMetadata(S(step, "outputJson"));
            var diff = diffs.Count > 0 ? diffs[^1].Groups["diff"].Value : "";
            if (diff.Length == 0 && output.ValueKind == JsonValueKind.Object && output.TryGetProperty("diff", out var storedDiff))
                diff = storedDiff.ValueKind == JsonValueKind.String ? storedDiff.GetString() ?? "" : S(storedDiff, "stdout");
            var hasDiff = diff.Length > 0;
            _filePath = Regex.Match(detail, @"(?m)^Datei:\s*(.+)$", RegexOptions.CultureInvariant).Groups[1].Value.Trim();
            if (_filePath.Length == 0) _filePath = S(output, "path", S(output, "file"));
            if (_filePath.Length == 0) _filePath = S(ReadMetadata(S(step, "inputJson")), "path");
            if (_filePath.Length == 0 && hasDiff) _filePath = DiffPath(diff);
            _hasCounts = false;
            if (_fileMutation && hasDiff && !IsTrue(output, "diffTruncated") && !IsTrue(output, "isBinary")
                && UnifiedDiffCounts(diff) is { } counts)
            {
                _hasCounts = true;
                _added.Text = $"+{counts.Added:N0}";
                _removed.Text = $"−{counts.Removed:N0}";
            }
            _diff = diff;
            var input = ReadMetadata(S(step, "inputJson"));
            _summary = AssistantToolStepHeaders.Summary(tool, input, output, _filePath);
            if (_details.Visibility == Visibility.Visible) RenderDetails();
            // Updates never change the user's fold state or open a completed file operation automatically.
            UpdateHeader();
        }

        private async Task LoadDetailsAsync(bool force = false)
        {
            if (_loadingDetails || LoadDetails is null || !IsExpanded || !force && !_detailsInViewport) return;
            _loadingDetails = true;
            var requestedAt = S(_step, "updatedAt");
            var requestedStatus = S(_step, "status");
            if (S(_step, "tool") == "math.formalProof" && !_running) _leanSourceRequested = true;
            // A newer receipt may need a refresh while this detail is being
            // read. Retain its height and controls until replacement arrives.
            if (_details.Children.Count == 0)
                _details.Children.Add(new TextBlock { Text = "Werkzeugschritt wird geladen …", FontSize = 14,
                    Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextWrapping = TextWrapping.Wrap });
            try
            {
                if (!await LoadDetails() && IsExpanded)
                {
                    _details.Children.Clear();
                    _details.Children.Add(new TextBlock { Text = "Details konnten nicht geladen werden. Zum Wiederholen erneut aufklappen.", TextWrapping = TextWrapping.Wrap });
                }
            }
            finally
            {
                _loadingDetails = false;
                if (_leanSourceRequested && !IsTrue(_step, "detailsDeferred") && IsExpanded)
                { _detailsDirty = true; RenderDetails(); }
                // A result for A may be rejected because B streamed in while
                // the request was pending. Fetch B even if no further tick
                // arrives; unchanged failures must not create a retry loop.
                if (IsExpanded && IsTrue(_step, "detailsDeferred")
                    && (S(_step, "updatedAt") != requestedAt || S(_step, "status") != requestedStatus))
                    _ = LoadDetailsAsync(force);
            }
        }

        private void UpdateHeader()
        {
            var expanded = _details.Visibility == Visibility.Visible;
            _title.Text = _label + (_summary.Length > 0 ? " · " + _summary : "");
            var research = IsResearchNavigation ? ResearchIdentity?.Invoke() : null;
            if (_researchAvatar is not null) _researchAvatar.Visibility = research.HasValue ? Visibility.Visible : Visibility.Collapsed;
            _compactIcon.Visibility = research.HasValue ? Visibility.Collapsed : Visibility.Visible;
            if (research is { } child)
            {
                if (_researchAvatar is null)
                {
                    _researchAvatar = new(child.AgentId, 14, child.PlanetIndex);
                    _headerRow.Children.Add(_researchAvatar);
                }
                _researchAvatar.SetAgentId(child.AgentId, child.PlanetIndex);
                _title.Text = ResearchTaskLabel(child.Title);
            }
            if (_running) _title.Text += " …";
            if (S(_step, "status") is "failed" or "denied" or "cancelled" or "interrupted") _title.Text += " · " + _status.Text;
            _counts.Visibility = _fileMutation && _hasCounts ? Visibility.Visible : Visibility.Collapsed;
            _title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            _title.Foreground = ThemeBrush("MissumMutedTextBrush", 160);
            _chevron.Glyph = IsResearchNavigation ? "\uE8A7" : expanded ? "\uE70D" : "\uE76C";
            ToolTipService.SetToolTip(_toggle, IsResearchNavigation ? "Aufgabe und Werkzeugschritte im Subagent-Tab öffnen" : _title.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_toggle,
                _title.Text + (_counts.Visibility == Visibility.Visible ? " " + _added.Text + " " + _removed.Text : "")
                + (IsResearchNavigation ? ", Recherchearbeit öffnen" : expanded ? ", ausgeklappt" : ", eingeklappt"));
        }

        private bool IsResearchNavigation => S(_step, "tool") == "web.deepResearch"
            && OpenResearch is not null && CanOpenResearch?.Invoke() != false;

        internal void RefreshResearchNavigation()
        {
            if (!IsResearchNavigation) return;
            _details.Visibility = Visibility.Collapsed; _details.Children.Clear();
            UpdateHeader();
        }

        private bool _leanSourceRequested;

        private void RenderDetails()
        {
            if (IsTrue(_step, "detailsDeferred")) { _ = LoadDetailsAsync(); return; }
            if (!_detailsDirty) return;
            _detailsDirty = false;
            if (_reasoning || S(_step, "tool") == "assistant.continuation")
            {
                var text = S(_step, "detail", S(_step, "explanation"));
                if (text.Length > 8192 && ScrollOwner is { } scroll)
                {
                    if (_largeReasoning is null)
                    {
                        _largeReasoning = new NativeVirtualizingTextView(scroll, PinSelection, Project);
                        _details.Children.Clear(); _details.Children.Add(_largeReasoning);
                    }
                    _largeReasoning.UpdateText(text);
                    return;
                }
                _largeReasoning = null;
                if (_details.Children.FirstOrDefault() is NativeStreamingMarkdown existing) existing.UpdateText(text);
                else { _details.Children.Clear(); _details.Children.Add(new NativeStreamingMarkdown(text)); }
                return;
            }
            _details.Children.Clear();
            var input = ReadMetadata(S(_step, "inputJson"));
            var output = ReadMetadata(S(_step, "outputJson"));
            var tool = S(_step, "tool");
            if (tool == "math.formalProof" && !_leanSourceRequested && LoadDetails is not null
                && !_running && S(input, "source").Length == 0)
            {
                _leanSourceRequested = true;
                _ = LoadDetailsAsync(force: true);
                return;
            }
            _details.Children.Add(new NativeToolResultView(NativeToolResultFormatter.Receipt(
                tool, input, output, S(_step, "status"), _filePath, _diff, S(_step, "detail"))));
            if (tool is "subagent" or "subagent.spawn" or "subagent.wait")
            {
                var agentId = S(output, "agentId", S(input, "agentId", S(_step, "agentId")));
                if (agentId.Length > 0 && SubagentRequested is not null)
                {
                    var open = new Button { Content = "Subagent öffnen", HorizontalAlignment = HorizontalAlignment.Left };
                    open.Click += (_, _) => SubagentRequested?.Invoke(agentId);
                    _details.Children.Add(open);
                }
            }
        }

        internal static string ToolSummary(string tool, JsonElement input, JsonElement output)
            => AssistantToolStepHeaders.Summary(tool, input, output);

        internal static JsonElement ReadMetadata(string json)
        {
            if (json.Length == 0) return default;
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : default;
            }
            catch (JsonException) { return default; }
        }

        private static bool IsTrue(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty(property, out var flag) && flag.ValueKind == JsonValueKind.True;

        private static string DiffPath(string diff)
        {
            foreach (var prefix in new[] { "+++ ", "--- " })
                foreach (var line in diff.ReplaceLineEndings("\n").Split('\n'))
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var path = line[prefix.Length..].Split('\t')[0].Trim('"');
                        if (path == "/dev/null") continue;
                        return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                    }
            return "";
        }

        private static (long Added, long Removed)? UnifiedDiffCounts(string diff)
        {
            long added = 0, removed = 0, oldRemaining = 0, newRemaining = 0;
            var hasHunk = false;
            foreach (var line in diff.ReplaceLineEndings("\n").Split('\n'))
            {
                // A proposal consisting only of +/- lines has no Git hunk and is not a measured change.
                var hunk = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant);
                if (hunk.Success)
                {
                    if (oldRemaining != 0 || newRemaining != 0) return null;
                    if (!long.TryParse(hunk.Groups[2].Success ? hunk.Groups[2].Value : "1", NumberStyles.None, CultureInfo.InvariantCulture, out oldRemaining)
                        || !long.TryParse(hunk.Groups[4].Success ? hunk.Groups[4].Value : "1", NumberStyles.None, CultureInfo.InvariantCulture, out newRemaining)) return null;
                    hasHunk = true;
                    continue;
                }
                if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) return null;
                if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) continue;
                if (oldRemaining == 0 && newRemaining == 0) continue;
                if (line.StartsWith('+')) { if (newRemaining == 0) return null; added++; newRemaining--; }
                else if (line.StartsWith('-')) { if (oldRemaining == 0) return null; removed++; oldRemaining--; }
                else if (line.StartsWith(' '))
                {
                    if (oldRemaining == 0 || newRemaining == 0) return null;
                    oldRemaining--; newRemaining--;
                }
                else return null;
            }
            return hasHunk && oldRemaining == 0 && newRemaining == 0 ? (added, removed) : null;
        }
    }
}

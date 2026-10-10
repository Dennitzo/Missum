using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.App.Controls;
using Missum.App.Services;
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
        _lastHeaderSecond = second;
        foreach (var (id, blocks) in _messageBlocks)
            if (blocks.TryGetValue("header", out var header) && DisplayMessages.TryGetValue(id, out var message))
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
        if (!_messageBlocks.TryGetValue(messageId, out var blocks)) _messageBlocks[messageId] = blocks = new(StringComparer.Ordinal);
        var desired = new List<FrameworkElement>();
        var content = S(message, "content");
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
                row.Children.Add(new ProgressRing { Width = 15, Height = 15, IsActive = true, Foreground = ThemeBrush("MissumAccentBrush", 176) });
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
        var messageSteps = Items(message, "toolSteps");
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
        var subagentReceipts = messageSteps.Where(step => S(step, "tool") == "subagent").ToArray();
        var positionedSteps = stepArtifacts.Count > 0 ? OrderNativeArtifactSteps(messageSteps) : messageSteps;
        foreach (var step in positionedSteps)
        {
            var next = TryNativeStepOffset(step, out var n) ? Math.Clamp(n, offset, content.Length) : offset;
            Text("text:" + offset, content[offset..next]); offset = next;
            var id = S(step, "id", "step:" + sequence++);
            var tool = S(step, "tool");
            // The persisted child receipt owns its lifecycle row. Keep rejected
            // manager calls visible, and coalesce only calls for an accepted child.
            if (IsAcceptedSubagentManagementStep(step, subagentReceipts)) { ArtifactsAfterStep(id); continue; }
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
                var steeringKey = "steering:" + id;
                if (!blocks.TryGetValue(steeringKey, out var steeringView))
                    blocks[steeringKey] = steeringView = new Border { HorizontalAlignment = HorizontalAlignment.Right,
                        Padding = new(16, 12, 16, 12), CornerRadius = new(18), Background = ThemeBrush("MissumLayerStrongBrush", 45),
                        Child = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16 } };
                ((TextBlock)((Border)steeringView).Child).Text = detail;
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
                var view = new ToolStepView(tool != "assistant.reasoning" && _settings.Current.CodingToolStepsExpanded);
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
                blocks["thinkingIndicator"] = thinking = new ThinkingIndicatorView();
            var indicator = (ThinkingIndicatorView)thinking;
            indicator.IsMessageActive = true;
            indicator.HasBlockingTool = hasBlockingTool || liveStatus.Length > 0;
            indicator.UpdateReasoning(S(latestReasoning, "id"), S(latestReasoning, "detail"), latestReasoningView);
            _thinkingIndicators[messageId] = indicator;
            desired.Add(indicator);
        }
        else { _thinkingIndicators.Remove(messageId); _thinkingStates.Remove(messageId); _modelPhases.Remove(messageId); }
        if (MessageActionsFor(messageId, message) is { } messageActions) desired.Add(messageActions);
        foreach (var old in panel.Children.OfType<FrameworkElement>().Where(child => !desired.Contains(child)).ToArray()) panel.Children.Remove(old);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], desired[i])) continue;
            panel.Children.Remove(desired[i]); panel.Children.Insert(i, desired[i]);
        }
        foreach (var stale in blocks.Where(pair => !desired.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) blocks.Remove(stale);
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
        internal event Action<string>? SubagentRequested;
        private readonly TextBlock _title = new() { FontSize = 14, Foreground = ThemeBrush("MissumMutedTextBrush", 160), TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _added = new() { FontSize = 13, Foreground = ThemeBrush("MissumSuccessBrush", 160) };
        private readonly TextBlock _removed = new() { FontSize = 13, Foreground = ThemeBrush("MissumDangerBrush", 160) };
        private readonly StackPanel _counts = new() { Orientation = Orientation.Horizontal, Spacing = 7, Visibility = Visibility.Collapsed };
        private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 10, Foreground = ThemeBrush("MissumMutedTextBrush", 140) };
        private readonly TextBlock _status = new();
        private readonly FontIcon _compactIcon = new() { Glyph = "\uE70F", FontSize = 14, Foreground = new SolidColorBrush(ToolGlyphColor("tool", "")), VerticalAlignment = VerticalAlignment.Center };
        private JsonElement _step;
        private string _diff = "";
        private bool _detailsDirty;
        private readonly bool _initiallyExpanded;
        internal bool IsExpanded => _details.Visibility == Visibility.Visible;
        private bool _reasoning;
        private readonly StackPanel _details = new() { Spacing = 8, Visibility = Visibility.Collapsed };
        private readonly Button _toggle;
        private string? _signature;
        private string _label = "";
        private string _filePath = "";
        private string _summary = "";
        private bool _fileMutation;
        private bool _running;
        private bool _hasCounts;

        public ToolStepView(bool initiallyExpanded = false)
        {
            _initiallyExpanded = initiallyExpanded;
            var row = new Grid { ColumnSpacing = 8 };
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
            _toggle.Click += (_, _) => SetExpanded(_details.Visibility != Visibility.Visible);
            var content = new StackPanel { Spacing = 10 };
            _details.Margin = new(24, 0, 0, 4);
            content.Children.Add(_toggle); content.Children.Add(_details);
            Children.Add(content);
        }

        internal void SetExpanded(bool expanded)
        {
            _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded) RenderDetails();
            else { _details.Children.Clear(); _detailsDirty = true; }
            UpdateHeader();
        }

        public void Update(JsonElement step, int number = 1)
        {
            var signature = step.GetRawText();
            if (signature == _signature) return;
            var firstUpdate = _signature is null;
            _signature = signature;
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
                "math.formalProof" => "Lean-Beweis prüfen", "assistant.progress" => "Fortschritt",
                "assistant.continuation" => S(step, "status") switch
                {
                    "completed" => "Lauf fortgesetzt", "failed" or "denied" => "Fortsetzen fehlgeschlagen",
                    "cancelled" or "interrupted" => "Fortsetzung abgebrochen", _ => "Fortsetzung wird vorbereitet",
                },
                "web.search" => "Websuche", "web.fetch" => "Webseite lesen", _ => S(step, "label", tool) };
            _running = S(step, "status") is "running" or "pending";
            var iconKey = ToolStepIconKey(tool);
            _compactIcon.Glyph = ToolIconGlyph(iconKey);
            _compactIcon.Foreground = new SolidColorBrush(ToolGlyphColor(iconKey, tool));
            _reasoning = tool == "assistant.reasoning";
            _status.Text = S(step, "status") switch { "completed" => "Abgeschlossen", "failed" => "Fehlgeschlagen", "denied" => "Abgelehnt", "cancelled" => "Abgebrochen", "interrupted" => "Unterbrochen", "steered" => "Umgelenkt", "pending" => "Wartet", _ => "In Bearbeitung" };
            if (firstUpdate) _details.Visibility = _initiallyExpanded ? Visibility.Visible : Visibility.Collapsed;
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
            _summary = _reasoning ? "" : ToolSummary(tool, input, output);
            if (_reasoning && _running) _status.Text = "Denkt nach";
            if (_details.Visibility == Visibility.Visible) RenderDetails();
            // Updates never change the user's fold state or open a completed file operation automatically.
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            var expanded = _details.Visibility == Visibility.Visible;
            var name = Path.GetFileName(_filePath.Replace('\\', '/'));
            _title.Text = _fileMutation && !expanded && name.Length > 0 ? name : _label + (_summary.Length > 0 ? " · " + _summary : "");
            if (_running) _title.Text += " …";
            if (S(_step, "status") is "failed" or "denied" or "cancelled" or "interrupted") _title.Text += " · " + _status.Text;
            _counts.Visibility = _fileMutation && _hasCounts ? Visibility.Visible : Visibility.Collapsed;
            _title.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            _title.Foreground = ThemeBrush("MissumMutedTextBrush", 160);
            _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
            ToolTipService.SetToolTip(_toggle, _filePath.Length > 0 ? _filePath : _label + " · " + _summary);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_toggle,
                _title.Text + (_counts.Visibility == Visibility.Visible ? " " + _added.Text + " " + _removed.Text : "")
                + (expanded ? ", ausgeklappt" : ", eingeklappt"));
        }

        private void RenderDetails()
        {
            if (!_detailsDirty) return;
            _detailsDirty = false;
            if (_reasoning || S(_step, "tool") == "assistant.continuation")
            {
                var text = S(_step, "detail", S(_step, "explanation"));
                if (_details.Children.FirstOrDefault() is NativeStreamingMarkdown existing) existing.UpdateText(text);
                else { _details.Children.Clear(); _details.Children.Add(new NativeStreamingMarkdown(text)); }
                return;
            }
            _details.Children.Clear();
            var copy = NativeToolResultView.CopyButton("Daten kopieren", _step.GetRawText());
            copy.HorizontalAlignment = HorizontalAlignment.Right;
            var input = ReadMetadata(S(_step, "inputJson"));
            var output = ReadMetadata(S(_step, "outputJson"));
            if (S(_step, "tool") is "subagent" or "subagent.spawn" or "subagent.wait")
            {
                var task = S(input, "task", S(output, "title"));
                if (task.Length > 0) _details.Children.Add(new NativeStreamingMarkdown(task));
                var result = S(output, "result");
                if (result.Length > 0) _details.Children.Add(new NativeStreamingMarkdown(result));
                var agentId = S(output, "agentId", S(input, "agentId", S(_step, "agentId")));
                if (agentId.Length > 0 && SubagentRequested is not null)
                {
                    var open = new Button { Content = "Subagent öffnen", HorizontalAlignment = HorizontalAlignment.Left };
                    open.Click += (_, _) => SubagentRequested?.Invoke(agentId);
                    _details.Children.Add(open);
                }
                _details.Children.Add(copy);
                return;
            }
            var explanation = S(_step, "explanation");
            if (explanation.Length == 0 && _filePath.Length > 0)
                explanation = S(_step, "tool") == "coding.read" ? $"Ich lese „{_filePath}“." : _fileMutation ? $"Dateiänderungen für „{_filePath}“." : "";
            if (explanation.Length > 0) _details.Children.Add(new NativeStreamingMarkdown(explanation));
            if (S(_step, "tool") == "research.update" && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) continue;
                    var heading = S(data, "title", S(change, "id"));
                    var text = S(data, "contentMarkdown", S(data, "statement"));
                    if (text.Length > 0) _details.Children.Add(new NativeStreamingMarkdown("### " + heading + "\n\n" + text));
                }
            }
            else if (input.ValueKind == JsonValueKind.Object)
                _details.Children.Add(new NativeToolResultView(input, _filePath, true, _diff.Length > 0,
                    S(_step, "tool") switch { "math.formalProof" => "lean", "research.code.write" => "python", _ => null }));
            if (_diff.Length > 0)
            {
                var caption = S(_step, "tool") == "coding.gitDiff" ? "Git-Diff" : S(_step, "status") == "completed" ? "Angewendete Änderung" : "Vorbereitete Änderung";
                _details.Children.Add(NativeToolResultView.CodeCard(caption, _diff, "diff", _filePath));
            }
            if (output.ValueKind == JsonValueKind.Object)
                _details.Children.Add(new NativeToolResultView(output, _filePath, false, _diff.Length > 0));
            else if (_diff.Length == 0 && S(_step, "detail") is { Length: > 0 } detail)
                _details.Children.Add(new NativeStreamingMarkdown(detail));
            _details.Children.Add(copy);
        }

        internal static string ToolSummary(string tool, JsonElement input, JsonElement output)
        {
            if (tool == "research.update" && input.ValueKind == JsonValueKind.Object
                && input.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
            {
                var titles = changes.EnumerateArray().Select(change => change.TryGetProperty("data", out var data)
                    ? S(data, "title", S(change, "id")) : S(change, "id")).Where(title => title.Length > 0).ToArray();
                var summary = titles.Length == 1 ? titles[0] : titles.Length.ToString(CultureInfo.CurrentCulture) + " Forschungsobjekte";
                return summary.Length > 100 ? summary[..97] + "…" : summary;
            }
            if (tool == "research.read") return "Hypothesen, offene Prüfungen und Publikationsstand";
            if (tool is "subagent" or "subagent.spawn" or "subagent.wait")
            {
                var title = S(output, "title", S(input, "title", S(input, "task")));
                return title.Length > 80 ? title[..77] + "…" : title;
            }
            var path = S(output, "path", S(output, "file", S(input, "path")));
            var target = path.Length > 0 ? path.Replace('\\', '/') : S(input, "query", S(input, "url"));
            if (tool is "coding.command" or "research.code.execute" or "research.code.test" or "research.code.benchmark") target = S(input, "executable") + " " + (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.Array ? string.Join(" ", args.EnumerateArray().Select(x => x.ToString())) : "");
            var facts = new List<string>();
            if (target.Length > 0) facts.Add(target);
            if (tool == "coding.read" && output.ValueKind == JsonValueKind.Object && output.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                var lines = (content.GetString() ?? "").ReplaceLineEndings("\n");
                var count = lines.Length == 0 ? 0 : lines.Count(c => c == '\n') + (lines.EndsWith('\n') ? 0 : 1);
                var start = int.TryParse(S(output, "startLine"), out var first) ? first : 1;
                facts.Add($"{count:N0} Zeilen gelesen" + (count > 0 ? $" ({start:N0}–{start + count - 1:N0})" : ""));
            }
            foreach (var (key, label) in new[] { ("entries", "Einträge"), ("matches", "Treffer"), ("results", "Treffer"), ("sources", "Quellen") })
                if (output.ValueKind == JsonValueKind.Object && output.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array) facts.Add($"{array.GetArrayLength():N0} {label}");
            if (S(output, "exitCode") is { Length: > 0 } exit) facts.Add("Exitcode " + exit);
            if (S(output, "error", S(output, "errorCode")) is { Length: > 0 } error) facts.Add(error);
            if (IsTrue(output, "truncated")) facts.Add("Ausgabe gekürzt");
            return string.Join(" · ", facts);
        }

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

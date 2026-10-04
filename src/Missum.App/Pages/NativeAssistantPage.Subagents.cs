using System.Text.Json;
using Missum.App.Controls;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, NativeSubagentState> _subagents = new(StringComparer.Ordinal);
    private long _subagentObservationOrder;
    private readonly Dictionary<string, double> _conversationOffsets = new(StringComparer.Ordinal);
    private string? _activeSubagentId;
    private string? _parentComposerDraft;
    private string? _parentComposerPlaceholder;
    private string? _subagentOverlaySignature;
    private Button? _subagentSummaryButton;
    private StackPanel? _subagentSummaryIcons;
    private TextBlock? _subagentSummaryLabel;
    private TextBlock? _subagentSummaryFinished;
    private readonly Dictionary<int, NativeSubagentAvatar> _subagentSummaryAvatars = [];
    private SubagentPlanetIdentityStore? _subagentPlanetIdentities;
    private SubagentPlanetIdentityStore PlanetIdentities => _subagentPlanetIdentities ??= new(App.Current.DataDirectory);

    // Coordinator events always update _messages, the parent transcript. A tab is
    // a projection, so a child delta cannot replace or navigate its parent's run.
    private NativeSubagentState? ActiveSubagent => _activeSubagentId is { } id
        && _subagents.TryGetValue(id, out var child) && child.SessionId == _session ? child : null;
    private IReadOnlyDictionary<string, JsonElement> DisplayMessages => ActiveSubagent?.Messages ?? _messages;
    private bool DisplayRunning => ActiveSubagent is { } child ? child.IsRunning : _running;
    private double DisplayContextUsed => ActiveSubagent is { } child ? child.ContextUsed : _contextUsed;
    private double DisplayContextLimit => ActiveSubagent is { } child ? child.ContextLimit : _contextLimit;
    private string DisplayChatStatus => ActiveSubagent is { } child ? S(child.Snapshot, "runStatus") : ChatStatus;
    private string ConversationViewKey => _session + ":" + (_activeSubagentId ?? "parent");

    private void RefreshComposerModelDisplay()
    {
        var child = ActiveSubagent;
        var selectedModel = _settings.Current.SelectedModel;
        var modelId = child is not null ? S(child.Snapshot, "model", S(child.Snapshot, "modelId"))
            : !string.IsNullOrWhiteSpace(selectedModel) ? selectedModel : S(_snapshot, "reasoningModelId");
        ModelLabel.Text = string.IsNullOrWhiteSpace(modelId)
            ? child is null ? "Modell auswählen" : "Subagent-Modell"
            : modelId.Split('/').Last().Split('~')[0];
        // Only explicit child metadata may describe its reasoning. Parent options
        // can arrive asynchronously while this readonly child tab is visible.
        var childEffort = child is null ? "" : S(child.Snapshot, "reasoningEffort");
        ReasoningLabel.Text = child is not null ? childEffort.Length > 0 ? EffortLabel(childEffort) : ""
            : _reasoning?.Available == true ? EffortLabel(_reasoning.Selected) : "";
        ModelButton.IsEnabled = child is null;
        ToolTipService.SetToolTip(ModelButton, child is null ? "Modell und Reasoning auswählen"
            : "Modell des Subagenten: " + ModelLabel.Text);
    }

    private bool IsKnownConversationMessage(string id) => _messages.ContainsKey(id)
        || _subagents.Values.Any(child => child.SessionId == _session && child.Messages.ContainsKey(id));

    private bool IsConversationMessageRunning(string id) => _messages.TryGetValue(id, out var parent)
        ? _running && S(parent, "status") is "streaming" or "pending"
        : _subagents.Values.Any(child => child.SessionId == _session && child.IsRunning
            && child.Messages.TryGetValue(id, out var message) && S(message, "status") is "streaming" or "pending");

    private bool ApplySubagentEvent(string type, JsonElement data)
    {
        if (type != "subagent.snapshot") return false;
        var snapshot = data.TryGetProperty("subagent", out var nested) ? nested : data;
        ObserveSubagentSnapshot(snapshot);
        RenderSubagentOverlay();
        RenderSessionTabs();
        return true;
    }

    private void SyncSubagents(JsonElement snapshot, bool sessionChanged)
    {
        if (sessionChanged)
        {
            _activeSubagentId = null;
            _parentComposerDraft = _parentComposerPlaceholder = null;
        }
        var projectedChildren = Items(snapshot, "subagents").ToArray();
        PlanetIdentities.EnsureAssigned(projectedChildren.Select(child => S(child, "agentId", S(child, "runId")))
            .Where(id => id.Length > 0));
        foreach (var child in projectedChildren) ObserveSubagentSnapshot(child);
        if (snapshot.TryGetProperty("subagents", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            var knownIds = children.EnumerateArray().Select(child => S(child, "agentId", S(child, "runId"))).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _subagents.Values.Where(child => child.SessionId == _session && !knownIds.Contains(child.AgentId)).ToArray())
            {
                if (_activeSubagentId == stale.AgentId) ShowParentConversation();
                SessionTabsPanel.Children.Remove(stale.Container); _subagents.Remove(stale.AgentId);
            }
        }
        RenderSubagentOverlay();
        RefreshSubagentSources();
        UpdateComposerNavigationState();
        RefreshComposerModelDisplay();
    }

    private void ObserveSubagentSnapshot(JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object) return;
        var agentId = S(snapshot, "agentId", S(snapshot, "runId"));
        if (agentId.Length == 0) return;
        var owner = Guid.TryParse(S(snapshot, "sessionId"), out var session) ? session : _session;
        if (!_subagents.TryGetValue(agentId, out var child))
        {
            _subagents[agentId] = child = CreateSubagentTab(agentId, owner);
            child.ObservationOrder = ++_subagentObservationOrder;
        }
        child.Snapshot = snapshot.Clone();
        child.SessionId = owner;
        child.Title = SubagentChatState.TaskTitle(S(snapshot, "title", "Subagent"));
        child.Status = S(snapshot, "status", "running");
        child.IsRunning = child.Status is not ("completed" or "failed" or "denied" or "cancelled"
            or "interrupted" or "disabled" or "unavailable")
            && (S(snapshot, "isRunning") == "True"
                || child.Status is "running" or "pending" or "queued" or "waiting" or "waitingForClient");
        if (double.TryParse(S(snapshot, "contextUsed"), out var used)) child.ContextUsed = used;
        if (double.TryParse(S(snapshot, "contextLimit"), out var limit)) child.ContextLimit = limit;
        child.Messages.Clear();
        foreach (var message in Items(snapshot, "messages")) child.Messages[S(message, "id")] = message.Clone();
        // DTOs without a top-level creation time retain the assigned user
        // message's timestamp. Token and completion updates never change recency.
        var created = SubagentCreatedAt(snapshot);
        if (created is not null) child.CreatedAt = created;
        ObserveThinkingProgress(snapshot);
        RefreshSubagentLifecycleRows(child);
        RefreshSubagentSources();
        if (ReferenceEquals(ActiveSubagent, child))
        {
            RenderMessages();
            RefreshContextDisplay();
            RefreshChatNotices();
            RefreshComposerModelDisplay();
        }
    }

    private NativeSubagentState CreateSubagentTab(string agentId, Guid owner)
    {
        var planetIndex = PlanetIdentities.GetOrAssign(agentId);
        var label = new TextBlock { Text = "Subagent", FontSize = 13, MaxWidth = 184,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(SubagentIcon(agentId, 13, planetIndex)); row.Children.Add(label);
        var select = TabButton(); select.Content = row; select.Height = 30; select.Padding = new(10, 0, 5, 0);
        var close = TabButton(); close.Content = new FontIcon { Glyph = "\uE711", FontSize = 11 };
        close.Width = close.Height = 24; close.Margin = new(0, 0, 3, 0); close.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.Children.Add(select);
        Grid.SetColumn(close, 1); grid.Children.Add(close);
        var child = new NativeSubagentState(agentId, owner, new Border { Child = grid, MaxWidth = 285 }, select, close, label, planetIndex);
        select.Click += async (_, _) => await ActivateSubagentTabAsync(child);
        close.Click += (_, _) =>
        {
            child.TabOpen = false;
            if (_activeSubagentId == child.AgentId) ShowParentConversation();
            RenderSessionTabs();
        };
        return child;
    }

    private static NativeSubagentAvatar SubagentIcon(string agentId, double size, int? planetIndex = null) => new(agentId, size, planetIndex);

    private NativeSubagentState? FindSubagentForStep(JsonElement step)
    {
        var output = ToolStepView.ReadMetadata(S(step, "outputJson"));
        var input = ToolStepView.ReadMetadata(S(step, "inputJson"));
        var agentId = S(output, "agentId", S(input, "agentId", S(step, "agentId")));
        if (_subagents.TryGetValue(agentId, out var child) && child.SessionId == _session) return child;
        var runId = S(output, "runId", S(input, "runId"));
        return runId.Length == 0 ? null : _subagents.Values.FirstOrDefault(item => item.SessionId == _session && S(item.Snapshot, "runId") == runId);
    }

    private static bool IsAcceptedSubagentManagementStep(JsonElement step, IReadOnlyList<JsonElement> receipts)
    {
        if (S(step, "tool") is not ("subagent.spawn" or "subagent.wait")
            || S(step, "status") is "failed" or "denied" or "cancelled" or "interrupted") return false;
        var output = ToolStepView.ReadMetadata(S(step, "outputJson"));
        var input = ToolStepView.ReadMetadata(S(step, "inputJson"));
        if (S(output, "status") is "failed" or "denied" or "disabled" or "unavailable" or "rejected"
            || output.ValueKind == JsonValueKind.Object && output.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
            return false;
        var agentId = S(output, "agentId", S(input, "agentId", S(step, "agentId")));
        var runId = S(output, "runId", S(input, "runId"));
        foreach (var receipt in receipts)
        {
            var accepted = ToolStepView.ReadMetadata(S(receipt, "outputJson"));
            var acceptedAgent = S(accepted, "agentId", S(receipt, "agentId"));
            if (acceptedAgent.Length == 0 || S(accepted, "runId").Length == 0) continue;
            if (agentId.Length > 0 && agentId == acceptedAgent
                || runId.Length > 0 && runId == S(accepted, "runId")) return true;
        }
        return false;
    }

    private void RefreshSubagentLifecycleRows(NativeSubagentState child)
    {
        foreach (var view in _messageBlocks.Values.SelectMany(blocks => blocks.Values).OfType<SubagentLifecycleView>())
            if (view.AgentId == child.AgentId && child.SessionId == _session) view.UpdateState(child);
    }

    private sealed class SubagentLifecycleView : Button
    {
        private readonly Func<string, int> _planetIndexFor;
        internal event Func<string, Task>? SubagentRequested;
        internal string AgentId { get; private set; } = "";
        internal Task? NavigationTask { get; private set; }
        internal long InvocationCount { get; private set; }
        internal TextBlock TaskLabel { get; } = new() { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        internal TextBlock StateLabel { get; } = new() { FontSize = 14, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center };
        internal NativeSubagentAvatar Icon { get; } = SubagentIcon("", 14);
        private string _task = "";
        private bool _completion;
        private bool _appearanceChosen;
        private bool _legacyCompletion;

        internal SubagentLifecycleView(Func<string, int> planetIndexFor)
        {
            _planetIndexFor = planetIndexFor;
            var row = new Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(Icon);
            Grid.SetColumn(TaskLabel, 1); row.Children.Add(TaskLabel);
            Grid.SetColumn(StateLabel, 2); row.Children.Add(StateLabel);
            Content = row;
            HorizontalAlignment = HorizontalAlignment.Left;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            MinWidth = 0; MinHeight = Height = 30;
            Padding = new(6, 4, 6, 4); BorderThickness = new(0); CornerRadius = new(6);
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Resources["ButtonBackgroundPointerOver"] = Brush(53);
            Resources["ButtonBackgroundPressed"] = Brush(62);
            TaskLabel.Foreground = StateLabel.Foreground = ThemeBrush("MissumMutedTextBrush", 160);
            Click += async (_, _) =>
            {
                InvocationCount++;
                NavigationTask = SubagentRequested?.Invoke(AgentId) ?? Task.CompletedTask;
                await NavigationTask;
            };
        }

        internal void Update(JsonElement step, NativeSubagentState? child)
        {
            var output = ToolStepView.ReadMetadata(S(step, "outputJson"));
            var input = ToolStepView.ReadMetadata(S(step, "inputJson"));
            _completion = S(step, "tool") == SubagentChatState.CompletionTool;
            if (!_appearanceChosen)
            {
                var snapshot = child?.Snapshot ?? output;
                var hasDelivery = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("resultDelivered", out var delivered)
                    && delivered.ValueKind is JsonValueKind.True or JsonValueKind.False;
                _legacyCompletion = !_completion && !hasDelivery && (child?.Status ?? S(output, "status", S(step, "status"))) == "completed";
                _appearanceChosen = true;
            }
            AgentId = S(output, "agentId", S(input, "agentId", S(step, "agentId")));
            _task = S(input, "task", S(step, "detail", S(output, "title")));
            if (child is not null) { UpdateState(child); return; }
            UpdateLabels(S(output, "title", _task), canOpen: false);
        }

        internal void UpdateState(NativeSubagentState child)
        {
            AgentId = child.AgentId;
            var assigned = child.Messages.Values.FirstOrDefault(message => S(message, "role") == "user");
            _task = S(assigned, "content", _task);
            UpdateLabels(child.Title, canOpen: true);
        }

        private void UpdateLabels(string title, bool canOpen)
        {
            if (TaskLabel.Text.Length == 0) TaskLabel.Text = SubagentChatState.TaskTitle(title);
            StateLabel.Text = _completion || _legacyCompletion ? "hat die Arbeit beendet" : "hat die Arbeit begonnen";
            Icon.SetAgentId(AgentId, AgentId.Length == 0 ? null : _planetIndexFor(AgentId));
            IsEnabled = canOpen;
            var label = TaskLabel.Text + " " + StateLabel.Text;
            AutomationProperties.SetName(this, label);
            AutomationProperties.SetHelpText(this, "Subagent-Aufgabe und Werkzeugschritte öffnen");
            ToolTipService.SetToolTip(this, label + (_task.Length > 0 ? "\n\n" + _task : "") + "\n\nSubagent öffnen");
        }
    }

    private int RenderSubagentTabs(int index)
    {
        foreach (var child in _subagents.Values)
        {
            if (child.SessionId != _session || !child.TabOpen)
            { SessionTabsPanel.Children.Remove(child.Container); continue; }
            var active = _activeSubagentId == child.AgentId && BodyGrid.Visibility == Visibility.Visible;
            child.Label.Text = "Subagent · " + child.Title;
            ApplyTabAppearance(child.Container, child.Select, active);
            child.Close.Foreground = Brush(155);
            child.Select.IsEnabled = child.Close.IsEnabled = !_sessionTabNavigationBusy;
            AutomationProperties.SetName(child.Select, "Subagent-Tab: " + child.Title);
            AutomationProperties.SetHelpText(child.Select, active ? "Aktiver Subagent" : "Aufgabe und Werkzeugschritte öffnen");
            AutomationProperties.SetName(child.Close, "Subagent-Tab schließen: " + child.Title);
            ToolTipService.SetToolTip(child.Select, child.Title + "\n" + SubagentStatusLabel(child));
            ToolTipService.SetToolTip(child.Close, "Subagent-Tab schließen");
            if (index >= SessionTabsPanel.Children.Count || !ReferenceEquals(SessionTabsPanel.Children[index], child.Container))
            {
                SessionTabsPanel.Children.Remove(child.Container);
                SessionTabsPanel.Children.Insert(Math.Min(index, SessionTabsPanel.Children.Count), child.Container);
            }
            index++;
        }
        return index;
    }

    private void RenderSubagentOverlay()
    {
        var children = SessionSubagents();
        var signature = SubagentListSignature(children);
        if (_subagentOverlaySignature == signature) return;
        _subagentOverlaySignature = signature;
        SubagentsSection.Visibility = children.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (children.Length == 0)
        {
            SubagentsPanel.Children.Clear();
            _subagentSummaryIcons?.Children.Clear(); _subagentSummaryAvatars.Clear();
        }
        else
        {
            EnsureSubagentSummary();
            var working = children.Where(child => child.IsRunning).ToArray();
            var active = working.Length;
            var inactive = children.Length - active;
            var caption = active > 0 ? active + (active == 1 ? " arbeitet" : " arbeiten") : inactive + " fertig";
            var finished = active > 0 && inactive > 0 ? inactive + " fertig" : "";
            if (_subagentSummaryLabel!.Text != caption) _subagentSummaryLabel.Text = caption;
            _subagentSummaryLabel.Foreground = active > 0 ? ThemeBrush("MissumTextBrush", 230)
                : ThemeBrush("MissumMutedTextBrush", 160);
            if (_subagentSummaryFinished!.Text != finished) _subagentSummaryFinished.Text = finished;
            _subagentSummaryFinished.Visibility = finished.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            var representatives = (active > 0 ? working : children).Take(4).ToArray();
            for (var index = 0; index < representatives.Length; index++)
            {
                var child = representatives[index];
                var variant = child.PlanetIndex;
                if (!_subagentSummaryAvatars.TryGetValue(variant, out var avatar))
                    _subagentSummaryAvatars[variant] = avatar = SubagentIcon(child.AgentId, 14, variant);
                avatar.SetAgentId(child.AgentId, variant);
                if (index >= _subagentSummaryIcons!.Children.Count || !ReferenceEquals(_subagentSummaryIcons.Children[index], avatar))
                {
                    _subagentSummaryIcons.Children.Remove(avatar);
                    _subagentSummaryIcons.Children.Insert(index, avatar);
                }
            }
            while (_subagentSummaryIcons!.Children.Count > representatives.Length)
                _subagentSummaryIcons.Children.RemoveAt(_subagentSummaryIcons.Children.Count - 1);
            var visiblePlanets = representatives.Select(child => child.PlanetIndex).ToHashSet();
            foreach (var hidden in _subagentSummaryAvatars.Keys.Where(index => !visiblePlanets.Contains(index)).ToArray())
                _subagentSummaryAvatars.Remove(hidden);
            var label = caption + (finished.Length > 0 ? " · " + finished : "");
            AutomationProperties.SetName(_subagentSummaryButton!, label);
            var exceptions = children.Where(child => !child.IsRunning && child.Status != "completed")
                .GroupBy(SubagentStatusLabel).Select(group => group.Count() + " " + group.Key.ToLowerInvariant());
            ToolTipService.SetToolTip(_subagentSummaryButton!, label + "\nSubagentenübersicht öffnen"
                + string.Concat(exceptions.Select(detail => "\n" + detail)));
            if (SubagentsPanel.Children.Count != 1 || !ReferenceEquals(SubagentsPanel.Children[0], _subagentSummaryButton))
            {
                SubagentsPanel.Children.Clear(); SubagentsPanel.Children.Add(_subagentSummaryButton!);
            }
        }
        if (_activeSubagentOverviewSession == _session) RenderSubagentOverviewContent();
    }

    private void EnsureSubagentSummary()
    {
        if (_subagentSummaryButton is not null) return;
        _subagentSummaryIcons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center };
        _subagentSummaryLabel = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        _subagentSummaryFinished = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("MissumMutedTextBrush", 145), Opacity = .75, HorizontalAlignment = HorizontalAlignment.Right };
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(_subagentSummaryIcons);
        Grid.SetColumn(_subagentSummaryLabel, 1); row.Children.Add(_subagentSummaryLabel);
        Grid.SetColumn(_subagentSummaryFinished, 2); row.Children.Add(_subagentSummaryFinished);
        _subagentSummaryButton = SidebarButton(null, "Subagentenübersicht öffnen");
        _subagentSummaryButton.Content = row;
        _subagentSummaryButton.Padding = new(0, 6, 0, 6); _subagentSummaryButton.MinHeight = _subagentSummaryButton.Height = 32;
        _subagentSummaryButton.CornerRadius = new(4);
        _subagentSummaryButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AutomationProperties.SetHelpText(_subagentSummaryButton, "Alle Subagenten dieser Sitzung öffnen");
        _subagentSummaryButton.Click += (_, _) => OpenSubagentOverview();
    }

    private NativeSubagentState[] SessionSubagents() => _subagents.Values.Where(child => child.SessionId == _session)
        .OrderByDescending(child => child.CreatedAt ?? DateTimeOffset.MinValue)
        .ThenByDescending(child => child.ObservationOrder).ToArray();

    private string SubagentListSignature(IEnumerable<NativeSubagentState> children) => _session + string.Join("|",
        children.Select(child => child.AgentId + child.Title + child.Status + child.IsRunning + child.CreatedAt + child.ObservationOrder));

    private static DateTimeOffset? SubagentCreatedAt(JsonElement snapshot)
    {
        if (DateTimeOffset.TryParse(S(snapshot, "createdAt", S(snapshot, "startedAt")), out var time)) return time;
        var assigned = Items(snapshot, "messages").FirstOrDefault(message => S(message, "role") == "user");
        return DateTimeOffset.TryParse(S(assigned, "createdAt"), out time) ? time : null;
    }

    private static string SubagentStatusLabel(NativeSubagentState child) => child.Status.ToLowerInvariant() switch
    {
        "completed" => "fertig", "failed" or "denied" => "Fehlgeschlagen", "cancelled" => "Abgebrochen",
        "interrupted" => "Unterbrochen", "disabled" or "unavailable" => "Nicht verfügbar", _ => "arbeitet",
    };

    private void SaveConversationOffset() => _conversationOffsets[ConversationViewKey] = ConversationScroll.VerticalOffset;

    private async Task ActivateSubagentTabAsync(NativeSubagentState child)
    {
        if (_disposed || child.SessionId != _session || _sessionTabNavigationBusy
            || !_subagents.TryGetValue(child.AgentId, out var current) || !ReferenceEquals(current, child)) return;
        var owner = _session;
        if (_dictationSession == owner)
            await FinishDictationForNavigationAsync(owner, Composer.Text);
        if (_disposed || child.SessionId != _session || owner != _session) return;
        SaveConversationOffset();
        if (ActiveSubagent is null)
        { _parentComposerDraft = Composer.Text; _parentComposerPlaceholder = Composer.PlaceholderText; _draftTimer.Stop(); }
        ShowChatView();
        child.TabOpen = true; _activeSubagentId = child.AgentId;
        _rendering = true; Composer.Text = ""; Composer.PlaceholderText = "Aufgabe vom Hauptagenten"; _rendering = false;
        RefreshConversationTab();
    }

    private void ShowParentConversation()
    {
        if (_activeSubagentId is null) return;
        SaveConversationOffset(); _activeSubagentId = null;
        _rendering = true;
        Composer.Text = _parentComposerDraft ?? S(_snapshot, "draft");
        Composer.PlaceholderText = _parentComposerPlaceholder ?? (_mode == "coding" ? "Leg einfach los" : "Frag etwas");
        _rendering = false;
        _parentComposerDraft = _parentComposerPlaceholder = null;
        RefreshConversationTab();
    }

    private void RefreshConversationTab()
    {
        _conversationSelection?.Clear();
        // Cached native bubbles keep markdown cursors, disclosures and footer
        // owners; detach only their outer parent while changing the projection.
        MessagesPanel.Children.Clear();
        _sourcesSignature = null;
        UpdateComposerNavigationState();
        RefreshComposerModelDisplay();
        RenderMessagesNow(); RefreshContextDisplay(); RefreshChatNotices(); RenderSessionTabs();
        var key = ConversationViewKey;
        var offset = _conversationOffsets.GetValueOrDefault(key);
        ConversationScroll.ChangeView(null, offset, null, disableAnimation: true);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && key == ConversationViewKey)
            { ConversationScroll.ChangeView(null, offset, null, disableAnimation: true); SyncOuterScroll(); }
        });
    }

    private sealed class NativeSubagentState(string agentId, Guid sessionId, Border container, Button select, Button close, TextBlock label, int planetIndex)
    {
        internal string AgentId { get; } = agentId;
        internal int PlanetIndex { get; } = planetIndex;
        internal Guid SessionId { get; set; } = sessionId;
        internal string Title { get; set; } = "Subagent";
        internal string Status { get; set; } = "running";
        internal DateTimeOffset? CreatedAt { get; set; }
        internal long ObservationOrder { get; set; }
        internal bool IsRunning { get; set; } = true;
        internal bool TabOpen { get; set; } = true;
        internal double ContextUsed { get; set; }
        internal double ContextLimit { get; set; }
        internal JsonElement Snapshot { get; set; }
        internal Dictionary<string, JsonElement> Messages { get; } = new(StringComparer.Ordinal);
        internal Border Container { get; } = container;
        internal Button Select { get; } = select;
        internal Button Close { get; } = close;
        internal TextBlock Label { get; } = label;
    }
}

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly List<NativeToolTab> _toolTabs = [];
    private NativeToolTab? _activeToolTab;
    private bool IsConversationActiveView => _activeToolTab is null && BodyGrid.Visibility == Visibility.Visible;
    private readonly ContentControl _toolDetailsHost = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    private Task OpenToolTabAsync(string messageId, string stepId)
    {
        if (_disposed || _session == Guid.Empty || _sessionTabNavigationBusy) return Task.CompletedTask;
        var child = _subagents.Values.FirstOrDefault(item => item.SessionId == _session && item.Messages.ContainsKey(messageId));
        var owner = child?.Messages ?? _messages;
        if (!owner.TryGetValue(messageId, out var message)) return Task.CompletedTask;
        var step = MessageSteps(message).FirstOrDefault(item => S(item, "id") == stepId);
        if (step.ValueKind != JsonValueKind.Object) return Task.CompletedTask;
        if (S(step, "tool") == "coding.updatePlan") return OpenWorkingPlanTabAsync(child?.AgentId);
        var tab = _toolTabs.FirstOrDefault(item => item.SessionId == _session && item.MessageId == messageId
            && item.StepId == stepId && item.AgentId == child?.AgentId);
        if (tab is null)
        {
            tab = CreateToolTab(_session, messageId, stepId, child?.AgentId);
            _toolTabs.Add(tab);
        }
        SetToolTabStep(tab, step);
        ShowToolTab(tab);
        return Task.CompletedTask;
    }

    private NativeToolTab CreateToolTab(Guid sessionId, string messageId, string stepId, string? agentId)
    {
        var label = new TextBlock { FontSize = 13, MaxWidth = 184, TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center };
        var icon = CreateTabIcon("\uE713");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        content.Children.Add(icon); content.Children.Add(label);
        var select = TabButton(); select.Content = content; select.Height = 30;
        select.Padding = new Thickness(10, 0, 5, 0);
        var close = TabButton(); close.Content = CreateTabIcon("\uE711", 11);
        close.Width = close.Height = 24; close.Margin = new Thickness(0, 0, 3, 0);
        close.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid();
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.Children.Add(select); Grid.SetColumn(close, 1); row.Children.Add(close);
        var container = new Border { Child = row, MaxWidth = 284 };
        var details = new ToolStepView();
        var panel = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(details);
        var scroll = new ScrollViewer { Content = panel, Padding = new Thickness(28, 20, 28, 28),
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        details.ScrollOwner = scroll;
        var tab = new NativeToolTab(sessionId, messageId, stepId, agentId, container, select, close, label, icon, details, scroll);
        details.LoadDetails = () => LoadToolTabDetailsAsync(tab);
        select.Click += (_, _) => ShowToolTab(tab);
        close.Click += (_, _) => CloseToolTab(tab);
        return tab;
    }

    private void ShowToolTab(NativeToolTab tab)
    {
        if (_disposed || _sessionTabNavigationBusy || tab.SessionId != _session || !_toolTabs.Contains(tab)) return;
        SaveConversationOffset();
        ShowChatView();
        if (_toolDetailsHost.Parent is null)
        {
            var parent = (Panel)ResearchHost.Parent;
            parent.Children.Add(_toolDetailsHost);
            Grid.SetRow(_toolDetailsHost, Grid.GetRow(ResearchHost));
            Grid.SetColumn(_toolDetailsHost, Grid.GetColumn(ResearchHost));
        }
        _activeToolTab = tab;
        BodyGrid.Visibility = Visibility.Collapsed;
        _toolDetailsHost.Content = tab.Scroll;
        _toolDetailsHost.Visibility = Visibility.Visible;
        tab.Details.ShowDetailsOnly(tab.Step);
        UpdateToolTabTitle(tab);
        RenderSessionTabs();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed && ReferenceEquals(_activeToolTab, tab))
                tab.Scroll.ChangeView(null, tab.ScrollOffset, null, disableAnimation: true);
        });
    }

    private void HideToolView()
    {
        if (_activeToolTab is { } tab)
        {
            tab.ScrollOffset = tab.Scroll.VerticalOffset;
            // Only the visible tab owns detail controls. The saved receipt can
            // rebuild them on demand without rendering hidden code blocks.
            tab.Details.ShowHeaderOnly(tab.Step);
        }
        _activeToolTab = null;
        _toolDetailsHost.Visibility = Visibility.Collapsed;
    }

    private void CloseToolTab(NativeToolTab tab)
    {
        if (_disposed || _sessionTabNavigationBusy) return;
        if (ReferenceEquals(_activeToolTab, tab)) ShowChatView();
        _toolTabs.Remove(tab);
        SessionTabsPanel.Children.Remove(tab.Container);
        if (ReferenceEquals(_toolDetailsHost.Content, tab.Scroll)) _toolDetailsHost.Content = null;
        tab.Scroll.Content = null;
        RenderSessionTabs();
    }

    private void SyncToolTabsSession()
    {
        if (_activeToolTab is { } tab && tab.SessionId != _session) ShowChatView();
        foreach (var hidden in _toolTabs.Where(item => item.SessionId != _session))
            SessionTabsPanel.Children.Remove(hidden.Container);
    }

    private void ClearToolTabs(bool returnToChat = true)
    {
        if (returnToChat && _activeToolTab is not null) ShowChatView();
        else HideToolView();
        _toolDetailsHost.Content = null;
        foreach (var tab in _toolTabs)
        {
            SessionTabsPanel.Children.Remove(tab.Container);
            tab.Scroll.Content = null;
        }
        _toolTabs.Clear();
    }

    private int RenderToolTabs(int index)
    {
        foreach (var tab in _toolTabs)
        {
            if (tab.SessionId != _session) { SessionTabsPanel.Children.Remove(tab.Container); continue; }
            var active = ReferenceEquals(_activeToolTab, tab);
            ApplyTabAppearance(tab.Container, tab.Select, active);
            SetTabCloseForeground(tab.Close);
            SetTabEnabled(tab.Select, !_sessionTabNavigationBusy);
            SetTabEnabled(tab.Close, !_sessionTabNavigationBusy);
            SetTabText(tab.Label, tab.Title);
            SetTabName(tab.Select, "Werkzeug-Tab: " + tab.Title);
            SetTabName(tab.Close, "Werkzeug-Tab schließen: " + tab.Title);
            SetTabHelp(tab.Select, active ? "Aktuelle Werkzeugdetails" : "Werkzeugdetails öffnen");
            SetTabToolTip(tab.Select, tab.Title);
            SetTabToolTip(tab.Close, "Werkzeug-Tab schließen");
            PositionTab(tab.Container, index++);
        }
        return index;
    }

    private void RefreshOpenToolTabs()
    {
        if (_disposed) return;
        RefreshWorkingPlanTab();
        foreach (var tab in _toolTabs.Where(item => item.SessionId == _session))
        {
            var owner = tab.AgentId is { } agentId && _subagents.TryGetValue(agentId, out var child) ? child.Messages : _messages;
            if (!owner.TryGetValue(tab.MessageId, out var message)) continue;
            var step = MessageSteps(message).FirstOrDefault(item => S(item, "id") == tab.StepId);
            if (step.ValueKind == JsonValueKind.Object) SetToolTabStep(tab, step);
        }
    }

    // Details may arrive after the compact header was paged out of the chat.
    // The explicit tab owns its receipt independently of the chat's window.
    private void ObserveToolTabDetails(JsonElement data)
    {
        if (S(data, "sessionId") != _session.ToString() || !data.TryGetProperty("toolStep", out var step)) return;
        foreach (var tab in _toolTabs.Where(item => item.SessionId == _session && item.MessageId == S(data, "messageId")
            && item.StepId == S(step, "id"))) SetToolTabStep(tab, step);
    }

    private async Task<bool> LoadToolTabDetailsAsync(NativeToolTab tab)
    {
        if (_disposed || tab.SessionId != _session || !ReferenceEquals(_activeToolTab, tab)) return false;
        if (tab.AgentId is { } agentId && _subagents.TryGetValue(agentId, out var child)
            && !child.Messages.ContainsKey(tab.MessageId))
        {
            // A hidden child transcript may be evicted from the shared LRU.
            // The explicit tab still knows its owner; do not misroute its read
            // through the main conversation when the transcript is absent.
            var generation = _toolDetailGeneration;
            try { await _toolDetailGate.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) { return false; }
            try
            {
                if (_disposed || tab.SessionId != _session || generation != _toolDetailGeneration
                    || !ReferenceEquals(_activeToolTab, tab)) return false;
                return await CommandAsync("tool.details", new { sessionId = tab.SessionId, messageId = tab.MessageId,
                    stepId = tab.StepId, parentMessageId = S(child.Snapshot, "parentMessageId"), agentId });
            }
            finally { _toolDetailGate.Release(); }
        }
        return await LoadToolStepDetailsAsync(tab.MessageId, tab.StepId);
    }

    private void SetToolTabStep(NativeToolTab tab, JsonElement incoming)
    {
        if (tab.Step.Equals(incoming)) return;
        if (tab.Step.ValueKind == JsonValueKind.Object)
        {
            if (DateTimeOffset.TryParse(S(tab.Step, "updatedAt"), out var currentAt)
                && DateTimeOffset.TryParse(S(incoming, "updatedAt"), out var incomingAt) && incomingAt < currentAt) return;
            if (ToolTabDetailsDeferred(incoming) && !ToolTabDetailsDeferred(tab.Step)
                && S(incoming, "updatedAt").Length > 0 && S(incoming, "updatedAt") == S(tab.Step, "updatedAt")
                && S(incoming, "status") == S(tab.Step, "status")) return;
        }
        // JsonElement values from the UI projection are immutable and own their
        // document. Keep their identity so unchanged receipts are not reparsed
        // or copied on every streaming tick.
        tab.Step = incoming;
        if (ReferenceEquals(_activeToolTab, tab)) tab.Details.ShowDetailsOnly(tab.Step);
        else tab.Details.ShowHeaderOnly(tab.Step);
        UpdateToolTabTitle(tab);
    }

    private static void UpdateToolTabTitle(NativeToolTab tab)
    {
        var title = tab.Details.HeaderTitle;
        if (title.Length == 0) title = S(tab.Step, "tool", "Werkzeug");
        tab.Title = title;
        SetTabText(tab.Label, title);
        tab.Icon.Glyph = ToolIconGlyph(ToolStepIconKey(S(tab.Step, "tool")));
        SetTabToolTip(tab.Select, title);
    }

    private static bool ToolTabDetailsDeferred(JsonElement step) => step.ValueKind == JsonValueKind.Object
        && step.TryGetProperty("detailsDeferred", out var deferred) && deferred.ValueKind == JsonValueKind.True;

    private sealed class NativeToolTab(Guid sessionId, string messageId, string stepId, string? agentId,
        Border container, Button select, Button close, TextBlock label, FontIcon icon, ToolStepView details, ScrollViewer scroll)
    {
        internal Guid SessionId { get; } = sessionId;
        internal string MessageId { get; } = messageId;
        internal string StepId { get; } = stepId;
        internal string? AgentId { get; } = agentId;
        internal Border Container { get; } = container;
        internal Button Select { get; } = select;
        internal Button Close { get; } = close;
        internal TextBlock Label { get; } = label;
        internal FontIcon Icon { get; } = icon;
        internal ToolStepView Details { get; } = details;
        internal ScrollViewer Scroll { get; } = scroll;
        internal string Title { get; set; } = "Werkzeug";
        internal JsonElement Step { get; set; }
        internal double ScrollOffset { get; set; }
    }
}

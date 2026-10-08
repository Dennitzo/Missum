using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Missum.App.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly HashSet<Guid> _subagentOverviewTabs = [];
    private readonly Dictionary<Guid, double> _subagentOverviewOffsets = [];
    private Guid? _activeSubagentOverviewSession;
    private Guid? _subagentOverviewContentOwner;
    private string? _subagentOverviewContentSignature;
    private Button? _subagentOverviewTabButton;
    private Border? _subagentOverviewTabContainer;
    private readonly ContentControl _subagentOverviewHost = new()
    {
        Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
    };

    private void OpenSubagentOverview()
    {
        if (_disposed || _session == Guid.Empty || _sessionTabNavigationBusy) return;
        if (ActiveSubagent is not null) ShowParentConversation();
        else SaveConversationOffset();
        ShowChatView();
        BodyGrid.Visibility = Visibility.Collapsed;
        _subagentOverviewTabs.Add(_session);
        _activeSubagentOverviewSession = _session;
        if (_subagentOverviewHost.Parent is null)
        {
            ((Panel)ResearchHost.Parent).Children.Add(_subagentOverviewHost);
            Grid.SetRow(_subagentOverviewHost, Grid.GetRow(ResearchHost));
            Grid.SetColumn(_subagentOverviewHost, Grid.GetColumn(ResearchHost));
        }
        _subagentOverviewHost.Visibility = Visibility.Visible;
        RenderSubagentOverviewContent();
        RenderSessionTabs();
        var owner = _session;
        // Reveal only on explicit navigation. Background child snapshots keep
        // the user's position in the horizontal tab strip unchanged.
        DispatcherQueue.TryEnqueue(() => RunUiCallback("SubagentOverview.RevealTab", () =>
        {
            if (_disposed || _session != owner || _activeSubagentOverviewSession != owner
                || _subagentOverviewTabContainer is not { } tab || tab.Parent != SessionTabsPanel
                || SubagentOverviewTabScroll() is not { } scroll) return;
            scroll.UpdateLayout();
            if (tab.ActualWidth <= 0 || scroll.ViewportWidth <= 0) return;
            var left = tab.TransformToVisual(scroll).TransformPoint(new(0, 0)).X;
            var right = left + tab.ActualWidth;
            var offset = scroll.HorizontalOffset;
            if (left < 0) offset += left - 4;
            else if (right > scroll.ViewportWidth) offset += right - scroll.ViewportWidth + 4;
            else return;
            scroll.ChangeView(Math.Clamp(offset, 0, scroll.ScrollableWidth), null, null, disableAnimation: true);
        }));
    }

    private ScrollViewer? SubagentOverviewTabScroll()
    {
        for (DependencyObject? ancestor = SessionTabsPanel; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is ScrollViewer scroll) return scroll;
        return null;
    }

    private void HideSubagentOverview()
    {
        if (_activeSubagentOverviewSession is { } owner && _subagentOverviewHost.Content is ScrollViewer scroll)
            _subagentOverviewOffsets[owner] = scroll.VerticalOffset;
        _activeSubagentOverviewSession = null;
        _subagentOverviewHost.Visibility = Visibility.Collapsed;
    }

    private void RenderSubagentOverviewContent()
    {
        var children = SessionSubagents();
        var signature = SubagentListSignature(children);
        if (_subagentOverviewContentSignature == signature) return;
        var offset = _subagentOverviewContentOwner == _session && _subagentOverviewHost.Content is ScrollViewer previous
            ? previous.VerticalOffset : _subagentOverviewOffsets.GetValueOrDefault(_session);
        _subagentOverviewContentOwner = _session;
        _subagentOverviewContentSignature = signature;
        var body = new StackPanel { Spacing = 16, Padding = new Thickness(24) };
        body.Children.Add(new TextBlock { Text = "Subagenten", FontSize = 24 });
        if (children.Length == 0)
            body.Children.Add(new TextBlock { Text = "Noch keine Subagenten in dieser Sitzung", FontSize = 14,
                Foreground = ThemeBrush("MissumMutedTextBrush", 145), TextWrapping = TextWrapping.Wrap });
        foreach (var child in children)
        {
            var status = SubagentStatusLabel(child);
            var button = SidebarButton("\uE8D4", child.Title);
            button.Tag = child.AgentId;
            button.Padding = new(0, 6, 0, 6); button.MinHeight = 48; button.Height = double.NaN;
            var row = (Grid)button.Content;
            var oldIcon = row.Children.OfType<FontIcon>().Single();
            var avatar = SubagentIcon(child.AgentId, 16, child.PlanetIndex);
            Grid.SetColumn(avatar, Grid.GetColumn(oldIcon)); row.Children.Remove(oldIcon); row.Children.Add(avatar);
            var oldTitle = row.Children.OfType<TextBlock>().Single();
            var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = child.Title, FontSize = 16, Foreground = ThemeBrush("MissumTextBrush", 230), TextWrapping = TextWrapping.Wrap });
            labels.Children.Add(new TextBlock { Text = status, FontSize = 14,
                Foreground = ThemeBrush("MissumMutedTextBrush", 145), Opacity = .75 });
            Grid.SetColumn(labels, Grid.GetColumn(oldTitle)); row.Children.Remove(oldTitle); row.Children.Add(labels);
            AutomationProperties.SetName(button, child.Title + " · " + status);
            AutomationProperties.SetHelpText(button, "Subagent-Aufgabe und Werkzeugschritte öffnen");
            ToolTipService.SetToolTip(button, child.Title + "\n" + status);
            button.Click += async (_, _) => await ActivateSubagentTabAsync(child);
            body.Children.Add(button);
        }
        var content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        if (offset > 0) content.Loaded += (_, _) => RunUiCallback("SubagentOverview.RestoreScroll", () =>
        {
            if (_subagentOverviewContentOwner == _session)
                content.ChangeView(null, offset, null, disableAnimation: true);
        });
        _subagentOverviewHost.Content = content;
    }

    private int RenderSubagentOverviewTab(int index)
    {
        if (!_subagentOverviewTabs.Contains(_session))
        {
            if (_subagentOverviewTabContainer is not null) SessionTabsPanel.Children.Remove(_subagentOverviewTabContainer);
            return index;
        }
        if (_subagentOverviewTabContainer is null)
        {
            _subagentOverviewTabButton = TabButton();
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            content.Children.Add(SubagentIcon("subagent-overview", 13));
            content.Children.Add(new TextBlock { Text = "Subagenten", FontSize = 13 });
            _subagentOverviewTabButton.Content = content;
            _subagentOverviewTabButton.Height = 30; _subagentOverviewTabButton.Padding = new(10, 0, 5, 0);
            _subagentOverviewTabButton.Click += (_, _) => OpenSubagentOverview();
            AutomationProperties.SetName(_subagentOverviewTabButton, "Subagenten-Tab");
            var close = TabButton();
            close.Content = new FontIcon { Glyph = "\uE711", FontSize = 11 };
            close.Width = close.Height = 24; close.Margin = new(0, 0, 3, 0); close.VerticalAlignment = VerticalAlignment.Center;
            ToolTipService.SetToolTip(close, "Subagenten-Tab schließen");
            AutomationProperties.SetName(close, "Subagenten-Tab schließen");
            close.Click += (_, _) =>
            {
                if (_disposed || _sessionTabNavigationBusy) return;
                _subagentOverviewTabs.Remove(_session);
                if (_activeSubagentOverviewSession == _session) ShowChatView();
                RenderSessionTabs();
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(_subagentOverviewTabButton); Grid.SetColumn(close, 1); row.Children.Add(close);
            _subagentOverviewTabContainer = new Border { Child = row };
        }
        var select = _subagentOverviewTabButton!;
        ApplyTabAppearance(_subagentOverviewTabContainer, select, _activeSubagentOverviewSession == _session);
        SetTabEnabled(select, !_sessionTabNavigationBusy);
        SetTabHelp(select,
            _activeSubagentOverviewSession == _session ? "Aktive Subagentenübersicht" : "Alle Subagenten dieser Sitzung öffnen");
        SetTabToolTip(select, "Alle Subagenten dieser Sitzung");
        PositionTab(_subagentOverviewTabContainer, index);
        return index + 1;
    }
}

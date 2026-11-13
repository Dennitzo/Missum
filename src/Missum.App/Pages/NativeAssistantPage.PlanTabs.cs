using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Missum.App.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly List<PlanModeTab> _planModeTabs = [];
    private PlanModeTab? _activePlanModeTab;
    private readonly ContentControl _planModeTabHost = new() { Visibility = Visibility.Collapsed,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };

    private Button PlanSummaryCard(Guid session, string messageId, string title, string fullPlan)
    {
        title = string.IsNullOrWhiteSpace(title) ? "Plan" : title.Trim();
        var text = new StackPanel { Spacing = 8 };
        text.Children.Add(new TextBlock { Text = "Plan", FontSize = 12, Foreground = ThemeBrush("MissumMutedTextBrush", 180) });
        var heading = PlanLabel(title, bold: true); heading.FontSize = 18;
        text.Children.Add(heading);
        var (section, preview) = PlanPreviewParts(fullPlan, title);
        if (section.Length > 0)
        {
            var subheading = PlanLabel(section, bold: true);
            subheading.FontSize = 15;
            text.Children.Add(subheading);
        }
        if (preview.Length > 0)
            text.Children.Add(new TextBlock { Text = preview, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("MissumTextBrush", 220) });
        var open = new Button { Content = new Border { Child = text, Padding = new Thickness(16),
                BorderBrush = ThemeBrush("MissumStrokeBrush", 50), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Background = ThemeBrush("MissumLayerBrush", 40) },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0), Background = TransparentBrush() };
        AutomationProperties.SetName(open, "Vollständigen Plan öffnen: " + title);
        ToolTipService.SetToolTip(open, "Vollständigen Plan im Tab öffnen");
        open.Click += (_, _) => OpenPlanModeTab(session, messageId, title, fullPlan);
        return open;
    }

    internal static string PlanPreview(string fullPlan, string title) => PlanPreviewParts(fullPlan, title).Preview;

    private static (string Heading, string Preview) PlanPreviewParts(string fullPlan, string title)
    {
        // Present the first section and its opening paragraph, keeping the
        // complete markdown in the separate tab rather than flattening it.
        var heading = "";
        var lines = new List<string>();
        var inFence = false;
        foreach (var raw in fullPlan.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (lines.Count > 0) break;
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            if (line.Length == 0) { if (lines.Count > 0) break; continue; }
            if (line.StartsWith('#'))
            {
                if (lines.Count > 0) break;
                var candidate = Regex.Replace(line, @"^#{1,6}\s*", "");
                if (!string.Equals(candidate, title, StringComparison.OrdinalIgnoreCase)) heading = candidate;
                continue;
            }
            if (string.Equals(line, title, StringComparison.OrdinalIgnoreCase) && lines.Count == 0) continue;
            lines.Add(line.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal));
            if (lines.Sum(item => item.Length) >= 260) break;
        }
        var preview = string.Join(" ", lines);
        return (heading, preview.Length <= 260 ? preview : preview[..257].TrimEnd() + "…");
    }

    private void OpenPlanModeTab(Guid session, string messageId, string title, string fullPlan)
    {
        if (_disposed || _sessionTabNavigationBusy || session != _session) return;
        var tab = _planModeTabs.FirstOrDefault(item => item.SessionId == session && item.MessageId == messageId);
        if (tab is null)
        {
            var label = new TextBlock { Text = title, FontSize = 13, MaxWidth = 184,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
            row.Children.Add(CreateWorkingPlanTabIcon()); row.Children.Add(label);
            var select = TabButton(); select.Content = row; select.Height = 30; select.Padding = new Thickness(10, 0, 5, 0);
            var close = TabButton(); close.Content = CreateTabIcon("\uE711", 11); close.Width = close.Height = 24;
            close.Margin = new Thickness(0, 0, 3, 0); close.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(select); Grid.SetColumn(close, 1); grid.Children.Add(close);
            var container = new Border { Child = grid, MaxWidth = 284 };
            var scroll = new ScrollViewer { Padding = new Thickness(28, 20, 28, 28),
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new NativeStreamingMarkdown(fullPlan) };
            tab = new PlanModeTab(session, messageId, title, container, select, close, scroll);
            var captured = tab;
            select.Click += (_, _) => ShowPlanModeTab(captured);
            close.Click += (_, _) => ClosePlanModeTab(captured);
            _planModeTabs.Add(tab);
        }
        ShowPlanModeTab(tab);
    }

    private void ShowPlanModeTab(PlanModeTab tab)
    {
        if (_disposed || _sessionTabNavigationBusy || tab.SessionId != _session || !_planModeTabs.Contains(tab)) return;
        SaveConversationOffset();
        ShowChatView();
        if (_planModeTabHost.Parent is null)
        {
            var parent = (Panel)ResearchHost.Parent;
            parent.Children.Add(_planModeTabHost);
            Grid.SetRow(_planModeTabHost, Grid.GetRow(ResearchHost));
            Grid.SetColumn(_planModeTabHost, Grid.GetColumn(ResearchHost));
        }
        _activePlanModeTab = tab;
        BodyGrid.Visibility = Visibility.Collapsed;
        _planModeTabHost.Content = tab.Scroll;
        _planModeTabHost.Visibility = Visibility.Visible;
        RenderSessionTabs();
    }

    private void HidePlanModeTab()
    {
        _activePlanModeTab = null;
        _planModeTabHost.Visibility = Visibility.Collapsed;
        _planModeTabHost.Content = null;
    }

    private void ClosePlanModeTab(PlanModeTab tab)
    {
        if (_disposed || _sessionTabNavigationBusy) return;
        if (ReferenceEquals(_activePlanModeTab, tab)) ShowChatView();
        _planModeTabs.Remove(tab);
        SessionTabsPanel.Children.Remove(tab.Container);
        RenderSessionTabs();
    }

    private void SyncPlanModeTabsSession()
    {
        if (_activePlanModeTab is { } active && active.SessionId != _session) ShowChatView();
        foreach (var tab in _planModeTabs.Where(item => item.SessionId != _session))
            SessionTabsPanel.Children.Remove(tab.Container);
    }

    private void ClearPlanModeTabs(bool returnToChat = true)
    {
        if (returnToChat && _activePlanModeTab is not null) ShowChatView();
        else HidePlanModeTab();
        foreach (var tab in _planModeTabs) SessionTabsPanel.Children.Remove(tab.Container);
        _planModeTabs.Clear();
    }

    private int RenderPlanModeTabs(int index)
    {
        foreach (var tab in _planModeTabs)
        {
            if (tab.SessionId != _session) { SessionTabsPanel.Children.Remove(tab.Container); continue; }
            var active = ReferenceEquals(tab, _activePlanModeTab);
            ApplyTabAppearance(tab.Container, tab.Select, active);
            SetTabCloseForeground(tab.Close);
            SetTabEnabled(tab.Select, !_sessionTabNavigationBusy); SetTabEnabled(tab.Close, !_sessionTabNavigationBusy);
            SetTabName(tab.Select, "Plan-Tab: " + tab.Title);
            SetTabName(tab.Close, "Plan-Tab schließen: " + tab.Title);
            SetTabHelp(tab.Select, active ? "Aktiver Plan" : "Vollständigen Plan öffnen");
            SetTabToolTip(tab.Select, tab.Title); SetTabToolTip(tab.Close, "Plan-Tab schließen");
            PositionTab(tab.Container, index++);
        }
        return index;
    }

    private sealed record PlanModeTab(Guid SessionId, string MessageId, string Title, Border Container,
        Button Select, Button Close, ScrollViewer Scroll);
}

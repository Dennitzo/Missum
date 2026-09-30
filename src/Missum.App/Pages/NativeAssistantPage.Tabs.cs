using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly List<SessionTabState> _sessionTabs = [];
    private bool _sessionTabNavigationBusy;

    // Tabs describe views inside the current session. New session views can be
    // added per mode without turning other chats into tabs.
    private static readonly Dictionary<string, SessionViewKind[]> SessionViewLayouts =
        new Dictionary<string, SessionViewKind[]>(StringComparer.Ordinal)
        {
            ["general"] = [SessionViewKind.Chat],
            ["coding"] = [SessionViewKind.Chat],
            ["claudescience"] = [SessionViewKind.Chat, SessionViewKind.Research, SessionViewKind.Simulation],
        };

    /// <summary>Call after assigning the active session and its complete coordinator snapshot.</summary>
    private void SyncSessionTabs()
    {
        if (_disposed) return;
        SyncReviewSession();
        var snapshotMode = S(_snapshot, "chatMode", _mode);
        var knownSessions = new Dictionary<Guid, string>();
        foreach (var session in Items(_snapshot, "sessions"))
        {
            if (!Guid.TryParse(S(session, "id"), out var id)) continue;
            knownSessions[id] = S(session, "title", "Neue Sitzung");
        }

        // A tab bar belongs to one session. The sidebar remains the place where
        // users switch between sessions inside their fixed project.
        foreach (var removed in _sessionTabs.Where(tab => tab.Id != _session || tab.Mode != snapshotMode).ToArray())
        {
            SessionTabsPanel.Children.Remove(removed.Container);
            _sessionTabs.Remove(removed);
        }

        foreach (var tab in _sessionTabs)
        {
            if (knownSessions.TryGetValue(tab.Id, out var title)) tab.Title = NormalizeTabTitle(title);
        }
        if (_session != Guid.Empty && _sessionTabs.All(tab => tab.Id != _session))
            _sessionTabs.Add(CreateSessionTab(_session, NormalizeTabTitle(knownSessions.GetValueOrDefault(_session)), snapshotMode));

        RenderSessionTabs();
    }

    private SessionTabState CreateSessionTab(Guid sessionId, string title, string mode)
    {
        var label = new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 184,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = "\uE8F2", FontSize = 13 });
        content.Children.Add(label);
        var select = TabButton();
        select.Content = content;
        select.Padding = new Thickness(10, 0, 5, 0);
        select.HorizontalContentAlignment = HorizontalAlignment.Left;
        select.MinWidth = 72;
        select.Height = 30;
        var border = new Border
        {
            Height = 32,
            MaxWidth = 258,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = select,
        };
        var tab = new SessionTabState(sessionId, title, mode, border, label, select);
        select.Click += async (_, _) => await ActivateSessionTabAsync(tab.Id);
        return tab;
    }

    private void RenderSessionTabs()
    {
        SessionTabsPanel.Spacing = 6;
        var layout = SessionViewLayouts.TryGetValue(_mode, out var configured)
            ? configured
            : SessionViewLayouts["general"];
        if (!layout.Contains(SessionViewKind.Research) && _researchTabButton is not null)
            SessionTabsPanel.Children.Remove(_researchTabButton);
        if (!layout.Contains(SessionViewKind.Simulation) && _simulationTabButton is not null)
            SessionTabsPanel.Children.Remove(_simulationTabButton);

        var position = 0;
        foreach (var view in layout)
        {
            if (view == SessionViewKind.Simulation)
            {
                if (_session != Guid.Empty) RenderSimulationTab(position++);
                continue;
            }
            if (view == SessionViewKind.Research)
            {
                if (_session != Guid.Empty)
                {
                    RenderResearchTab(position);
                    position++;
                }
                continue;
            }

            var tab = _sessionTabs.FirstOrDefault(item => item.Id == _session);
            if (tab is null) continue;
            var active = tab.Id == _session && _activeReviewRunId is null && _activeResearchSessionId is null;
            tab.Label.Text = tab.Title;
            tab.Container.Background = active ? ThemeBrush("MissumAccentSubtleBrush", 42) : Brush(29);
            tab.Container.BorderBrush = active ? ThemeBrush("MissumAccentBrush", 72) : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            tab.Select.Foreground = Brush(active ? (byte)242 : (byte)170);
            tab.Select.IsEnabled = !_sessionTabNavigationBusy;
            ToolTipService.SetToolTip(tab.Select, tab.Title);
            AutomationProperties.SetName(tab.Select, $"Chat-Tab: {tab.Title}");
            AutomationProperties.SetHelpText(tab.Select, active ? "Aktiver Chat" : "Chat öffnen");
            if (position >= SessionTabsPanel.Children.Count || !ReferenceEquals(SessionTabsPanel.Children[position], tab.Container))
            {
                SessionTabsPanel.Children.Remove(tab.Container);
                SessionTabsPanel.Children.Insert(position, tab.Container);
            }
            position++;
        }
        RenderReviewTabs(position);
    }

    private async Task ActivateSessionTabAsync(Guid sessionId)
    {
        if (_sessionTabNavigationBusy || _disposed) return;
        ShowChatView();
        if (sessionId == _session) { RenderSessionTabs(); return; }
        _sessionTabNavigationBusy = true;
        RenderSessionTabs();
        try
        {
            await NavigateAsync("session.open", new { sessionId });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { ShowError(exception.Message); }
        finally
        {
            _sessionTabNavigationBusy = false;
            if (!_disposed) SyncSessionTabs();
        }
    }

    private static Button TabButton()
    {
        var button = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            CornerRadius = new CornerRadius(6),
        };
        button.Resources["ButtonBackgroundPointerOver"] = Brush(53);
        button.Resources["ButtonBackgroundPressed"] = Brush(62);
        return button;
    }

    private static string NormalizeTabTitle(string? title) => string.IsNullOrWhiteSpace(title) ? "Neue Sitzung" : title;

    private enum SessionViewKind
    {
        Chat,
        Research,
        Simulation,
    }

    private sealed class SessionTabState(Guid id, string title, string mode, Border container, TextBlock label, Button select)
    {
        public Guid Id { get; } = id;
        public string Title { get; set; } = title;
        public string Mode { get; } = mode;
        public Border Container { get; } = container;
        public TextBlock Label { get; } = label;
        public Button Select { get; } = select;
    }
}

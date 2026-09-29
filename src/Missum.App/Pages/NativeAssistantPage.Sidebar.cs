using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<Guid, List<ProgressRing>> _sidebarActivityRings = [];
    private readonly HashSet<Guid> _sidebarReportedBusy = [];

    private void RenderSidebar()
    {
        ProjectsPanel.Children.Clear();
        _sidebarActivityRings.Clear();
        _sidebarReportedBusy.Clear();
        var sessions = Items(_snapshot, "sessions");
        var byId = new Dictionary<Guid, JsonElement>();
        foreach (var session in sessions)
        {
            if (!Guid.TryParse(S(session, "id"), out var id)) continue;
            byId[id] = session;
        }

        var search = SearchBox.Text.Trim();
        foreach (var group in Items(_snapshot, "sessionGroups"))
        {
            var name = S(group, "name", "Projekt");
            var path = S(group, "workspacePath");
            var members = new List<JsonElement>();
            var seen = new HashSet<Guid>();
            // The coordinator supplies GUID strings, ordered like its session list.
            foreach (var value in Items(group, "sessionIds"))
            {
                if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id)
                    && seen.Add(id) && byId.TryGetValue(id, out var member)) members.Add(member);
            }
            var nameMatches = name.Contains(search, StringComparison.CurrentCultureIgnoreCase);
            var visibleMembers = nameMatches ? members : members.Where(session =>
                S(session, "title", "Neue Sitzung").Contains(search, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (search.Length > 0 && !nameMatches && visibleMembers.Count == 0) continue;

            var project = new StackPanel { Spacing = 1 };
            var projectHeader = new Grid();
            projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var expanded = !string.Equals(S(group, "isCollapsed"), "True", StringComparison.OrdinalIgnoreCase);
            var folderButton = SidebarButton("\uE8B7", name);
            ((TextBlock)((Grid)folderButton.Content).Children[1]).Foreground = Brush(180);
            ((FontIcon)((Grid)folderButton.Content).Children[0]).Foreground = Brush(180);
            var projectChildren = new StackPanel { Visibility = expanded || search.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
            ToolTipService.SetToolTip(folderButton, path.Length == 0 ? name : path);
            folderButton.Click += async (_, _) =>
            {
                try
                {
                    expanded = !expanded;
                    projectChildren.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                    await CommandAsync("session.groupCollapse", new { groupId = S(group, "id"), collapsed = !expanded });
                    await RefreshForExternalActivationAsync();
                }
                catch (Exception exception) { ShowError(exception.Message); }
            };
            projectHeader.Children.Add(folderButton);
            projectHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            var options = ProjectOptionsButton(path, name, members.Select(member => S(member, "id")).ToArray(), S(group, "id"));
            Grid.SetColumn(options, 1); projectHeader.Children.Add(options);
            var newChatLabel = "Neue Sitzung in " + name;
            var projectMode = S(group, "chatMode", _mode);
            var newChatButton = new Button
            {
                Content = new FontIcon { Glyph = "\uE70F", FontSize = 15, Foreground = Brush(235) },
                Style = (Style)Resources["RoundIconButtonStyle"],
                Width = 32, Height = 32, MinWidth = 32, MinHeight = 32,
                Padding = new Thickness(0), Margin = new Thickness(0),
                CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right, Opacity = 1,
                IsEnabled = path.Length > 0,
            };
            AutomationProperties.SetName(newChatButton, newChatLabel);
            ToolTipService.SetToolTip(newChatButton, newChatLabel);
            newChatButton.Click += async (_, _) =>
            {
                newChatButton.IsEnabled = false;
                try
                {
                    await NavigateAsync("session.projectCreate", new { workspacePath = path, chatMode = projectMode });
                }
                catch (Exception exception) { ShowError(exception.Message); }
                finally { newChatButton.IsEnabled = path.Length > 0; }
            };
            Grid.SetColumn(newChatButton, 2); projectHeader.Children.Add(newChatButton);
            project.Children.Add(projectHeader);
            if (visibleMembers.Count == 0)
            {
                projectChildren.Children.Add(new TextBlock
                {
                    Text = "Keine Chats", FontSize = 13, Foreground = Brush(105),
                    Margin = new Thickness(32, 3, 8, 9),
                });
            }
            else
            {
                var children = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 9) };
                foreach (var member in visibleMembers) children.Children.Add(CreateSidebarSession(member, projectChild: true));
                projectChildren.Children.Add(children);
            }
            project.Children.Add(projectChildren);
            ProjectsPanel.Children.Add(project);
        }
        if (ProjectsPanel.Children.Count == 0 && Items(_snapshot, "sessionGroups").Length == 0)
            ProjectsPanel.Children.Add(new TextBlock { Text = "Projekt hinzufügen", Foreground = Brush(110), FontSize = 13, Margin = new Thickness(10, 4, 0, 8) });

        UpdateSidebarActivity(_snapshot);
    }

    private Button CreateSidebarSession(JsonElement session, bool projectChild = false)
    {
        var id = Guid.Parse(S(session, "id"));
        var title = S(session, "title", "Neue Sitzung");
        var button = SidebarButton(null, title);
        if (projectChild) button.Padding = new Thickness(36, 4, 8, 4);
        if (id == _session) button.Background = ThemeBrush("MissumAccentSubtleBrush", 46);
        var row = (Grid)button.Content;
        var indicatorColumn = row.ColumnDefinitions.Count;
        // Reserve a narrow indicator column, so starting a run cannot move the title.
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        var ring = new ProgressRing
        {
            Width = 14, Height = 14, MinWidth = 14, MinHeight = 14,
            Foreground = ThemeBrush("MissumAccentBrush", 185), IsActive = false, Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
        };
        AutomationProperties.SetName(ring, "Antwort wird erstellt");
        Grid.SetColumn(ring, indicatorColumn); row.Children.Add(ring);
        if (!_sidebarActivityRings.TryGetValue(id, out var rings)) _sidebarActivityRings[id] = rings = [];
        rings.Add(ring);
        button.Click += async (_, _) =>
        {
            try { await NavigateAsync("session.open", new { sessionId = id }); }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Umbenennen" };
        rename.Click += async (_, _) =>
        {
            try
            {
                var input = new TextBox { Text = title, MaxLength = 160 };
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Chat umbenennen", Content = input,
                    PrimaryButtonText = "Speichern", CloseButtonText = "Abbrechen" };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await CommandAsync("session.rename", new { sessionId = id, title = input.Text });
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        var delete = new MenuFlyoutItem { Text = "Löschen" };
        delete.Click += async (_, _) =>
        {
            try
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Chat löschen?", Content = title,
                    PrimaryButtonText = "Löschen", CloseButtonText = "Abbrechen" };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await NavigateAsync("session.delete", new { sessionId = id });
            }
            catch (Exception exception) { ShowError(exception.Message); }
        };
        menu.Items.Add(rename); menu.Items.Add(delete); button.ContextFlyout = menu;
        return button;
    }

    /// <summary>Updates the existing rings; optionally consumes a snapshot or queue.changed payload.</summary>
    private void UpdateSidebarActivity(JsonElement update = default)
    {
        if (update.ValueKind == JsonValueKind.Object)
        {
            if (update.TryGetProperty("runQueue", out var queue) && queue.ValueKind == JsonValueKind.Object)
            {
                _sidebarReportedBusy.Clear();
                if (queue.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Object
                    && S(active, "state") is "running" or "cancelling"
                    && Guid.TryParse(S(active, "sessionId"), out var activeId)) _sidebarReportedBusy.Add(activeId);
            }
            if (S(update, "isAiBusy") == "True" && Guid.TryParse(S(update, "activeRunSessionId"), out var runningId))
                _sidebarReportedBusy.Add(runningId);
        }
        foreach (var (id, rings) in _sidebarActivityRings)
        {
            var busy = id == _session ? _running : _sidebarReportedBusy.Contains(id) && !_finishedSessions.Contains(id);
            foreach (var ring in rings)
            {
                ring.IsActive = busy;
                ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    // Also used by the native attachments panel; keep its two-argument contract.
    private static Button SidebarButton(string? glyph, string label)
    {
        var row = new Grid { ColumnSpacing = glyph is null ? 0 : 10 };
        if (glyph is not null)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        }
        var textColumn = row.ColumnDefinitions.Count;
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, textColumn); row.Children.Add(text);
        var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 4, 8, 4),
            MinWidth = 0, MinHeight = 31, Height = 31, CornerRadius = new CornerRadius(8) };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

}

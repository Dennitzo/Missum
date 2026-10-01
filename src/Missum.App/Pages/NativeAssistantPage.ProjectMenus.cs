using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Missum.App.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task OpenProjectFolderAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Der Projektordner ist nicht verfügbar.");
            if (!await Windows.System.Launcher.LaunchFolderPathAsync(path)) ShowError("Der Projektordner konnte nicht geöffnet werden.");
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private MenuFlyout ProjectMenu(string path, string name, string[]? sessions = null, string? groupId = null)
    {
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Im Explorer öffnen", Icon = new FontIcon { Glyph = "\uE8B7", Foreground = NativeIconPalette.BrushFor("folder") }, IsEnabled = Directory.Exists(path) };
        open.Click += async (_, _) => await OpenProjectFolderAsync(path);
        menu.Items.Add(open);
        if (sessions is not null && groupId is not null)
        {
            var delete = new MenuFlyoutItem { Text = "Projekt löschen", Icon = new FontIcon { Glyph = "\uE74D", Foreground = NativeIconPalette.BrushFor("danger") } };
            delete.Click += async (_, _) =>
            {
                try
                {
                    var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "Projekt löschen?",
                        Content = $"„{name}“ und seine {sessions.Length} Sitzungen werden aus Missum gelöscht. Der Ordner und seine Dateien bleiben erhalten.",
                        PrimaryButtonText = "Projekt löschen", CloseButtonText = "Abbrechen", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                    foreach (var session in sessions)
                        if (!await CommandAsync("session.delete", new { sessionId = session })) return;
                    await CommandAsync("session.groupDeleteEmpty", new { groupId });
                    await RefreshForExternalActivationAsync();
                }
                catch (Exception exception) { ShowError(exception.Message); }
            };
            menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(delete);
        }
        return menu;
    }

    private Button ProjectOptionsButton(string path, string name, string[] sessions, string groupId)
    {
        var button = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 17, Foreground = NativeIconPalette.BrushFor("settings") },
            Style = (Style)Resources["RoundIconButtonStyle"],
            Width = 32, Height = 32, MinWidth = 32, MinHeight = 32, Padding = new(0), Opacity = 1,
            VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Projektoptionen für " + name);
        ToolTipService.SetToolTip(button, "Projektoptionen");
        button.Flyout = ProjectMenu(path, name, sessions, groupId);
        return button;
    }

    private async void OnInspectorWorkspaceClick(object sender, RoutedEventArgs e) =>
        await OpenProjectFolderAsync(S(_snapshot, "workspacePath"));
}

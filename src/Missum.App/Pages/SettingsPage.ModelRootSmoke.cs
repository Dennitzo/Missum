using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class SettingsPage
{
    private static readonly string[] NativeModelRootSmokeThemeNames = ["dark", "light"];

    internal static async Task VerifyNativeModelRootSmokeAsync(Panel host)
    {
        var isolatedRoot = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY"))
            || string.IsNullOrWhiteSpace(isolatedRoot)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || !string.Equals(Path.GetFullPath(isolatedRoot), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Modellordnerprüfung benötigt ein isoliertes Profil ohne Modellstart.");
        var app = App.Current;
        var settings = app.GetService<SettingsCoordinator>();
        var saved = settings.Current;
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var page = new SettingsPage();
        page.Loaded -= page.OnLoaded;
        page.Background = (Brush)app.Resources["MissumWindowBrush"];
        Grid.SetRowSpan(page, 100); Grid.SetColumnSpan(page, 100); Canvas.SetZIndex(page, 1000);
        const string chosen = @"\\nas-model-path-smoke.invalid\Melow's Umbrel\Huggingface";
        try
        {
            await page.ViewModel.InitializeAsync();
            page.SynchronizeControls();
            host.Children.Add(page); host.UpdateLayout();
            page.NativeModelRootCard.StartBringIntoView();
            await Task.Delay(80); host.UpdateLayout();
            if (page.NativeModelRootCard.ActualWidth < 300 || page.NativeModelRootCard.ActualHeight < 100
                || page.SelectNativeModelRootButton.Content?.ToString() != "Ordner auswählen"
                || page.SelectNativeModelRootButton.ActualWidth < 100)
                throw new InvalidOperationException("Die native Modellordnerkarte besitzt keine nutzbare Ordnerauswahl.");
            page.NativeModelRootBox.Text = chosen;
            await Task.Yield();
            if (page.ViewModel.NativeModelRoot != chosen || !page.ViewModel.HasUnsavedChanges)
                throw new InvalidOperationException("Der native Modellpfad wird nicht an den Einstellungsentwurf gebunden.");
            using var closedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            closedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            page.ViewModel.MissumAiServerUrl = "http://127.0.0.1:" + ((IPEndPoint)closedPort.LocalEndPoint!).Port;
            page.OnSave(page, new RoutedEventArgs());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!page.StatusBar.IsOpen && DateTimeOffset.UtcNow < deadline) await Task.Delay(40);
            if (page.StatusBar.Severity != InfoBarSeverity.Success || settings.Current.NativeModelRoot != chosen)
                throw new InvalidOperationException("Der echte Speichern-Handler übernimmt den Modellordner nicht: " + page.StatusBar.Message);
            var reloaded = await app.GetService<ISettingsStore>().LoadAsync();
            if (reloaded.NativeModelRoot != chosen)
                throw new InvalidOperationException("Der Modellordner bleibt nach erneutem Laden nicht gespeichert.");
            page.NativeModelRootBox.Text = @"C:\Unstored Models";
            await page.ViewModel.InitializeAsync();
            host.UpdateLayout();
            if (page.NativeModelRootBox.Text != chosen || page.ViewModel.HasUnsavedChanges)
                throw new InvalidOperationException("Die Modellordnerkarte lädt den gespeicherten Stand nicht erneut.");
            foreach (var theme in StatusSmokeThemes)
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Light ? "#ECE7F2" : "#181818");
                app.ApplyAccentColor("#A970FF");
                host.UpdateLayout(); await Task.Delay(80); host.UpdateLayout();
                await SaveStatusSmokePreviewAsync(page.NativeModelRootCard,
                    Path.Combine(app.DataDirectory, "native-model-root-" + theme.ToString().ToLowerInvariant() + ".png"));
            }
            await File.WriteAllTextAsync(Path.Combine(app.DataDirectory, "native-model-root-settings-validation.json"),
                JsonSerializer.Serialize(new { passed = true, renderer = "WinUI3", nativeCard = true, folderButton = true,
                    editablePathBinding = true, actualSaveHandler = true, persistedReload = true, themes = NativeModelRootSmokeThemeNames,
                    path = chosen, modelRequests = 0, folderDialogOpened = false, userDataModified = false }));
        }
        finally
        {
            host.Children.Remove(page);
            await settings.UpdateAsync(current => current with { NativeModelRoot = saved.NativeModelRoot,
                MissumAiServerUrl = saved.MissumAiServerUrl });
            await page.ViewModel.InitializeAsync();
            app.ApplyTheme(saved.Theme); app.ApplyAccentColor(originalAccent); app.ApplyBackgroundColor(originalBackground);
            host.UpdateLayout();
        }
    }
}

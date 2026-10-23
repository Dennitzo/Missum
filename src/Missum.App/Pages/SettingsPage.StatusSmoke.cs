using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Missum.App.Pages;

public sealed partial class SettingsPage
{
    private static readonly AppTheme[] StatusSmokeThemes = [AppTheme.Dark, AppTheme.Light];
    private static readonly string[] StatusSmokeAccents = ["#A970FF", "#4DA3FF", "#FCEE0A"];

    internal static async Task VerifyStatusAccentSmokeAsync(Panel host)
    {
        var isolatedRoot = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY"))
            || string.IsNullOrWhiteSpace(isolatedRoot)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || !string.Equals(Path.GetFullPath(isolatedRoot), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die Einstellungsprüfung benötigt eine isolierte Datenbank ohne Modellstart.");

        var app = App.Current;
        var settings = app.GetService<SettingsCoordinator>();
        var saved = settings.Current;
        var originalTheme = (host.XamlRoot?.Content as FrameworkElement)?.RequestedTheme switch
        {
            ElementTheme.Light => AppTheme.Light,
            ElementTheme.Dark => AppTheme.Dark,
            _ => AppTheme.System,
        };
        var originalAccent = app.AccentColor;
        var originalBackground = app.BackgroundColor;
        var page = new SettingsPage();
        // Initialize the actual editor without its normal gateway/catalog fetch.
        page.Loaded -= page.OnLoaded;
        page.Background = (Brush)app.Resources["MissumWindowBrush"];
        Grid.SetRowSpan(page, 100);
        Grid.SetColumnSpan(page, 100);
        Canvas.SetZIndex(page, 1000);
        var variants = new List<object>();
        try
        {
            await page.ViewModel.InitializeAsync();
            page.SynchronizeControls();
            host.Children.Add(page);
            host.UpdateLayout();

            // A bound, non-listening socket fails the normal connection probe
            // locally. No gateway, model, or user profile is contacted.
            using var closedPort = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            closedPort.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)closedPort.LocalEndPoint!;
            page.ViewModel.MissumAiServerUrl = "http://127.0.0.1:" + endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            page.StatusBar.IsOpen = false;
            page.OnSave(page, new RoutedEventArgs());
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!page.StatusBar.IsOpen && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(40);
            if (page.StatusBar is not { IsOpen: true, Severity: InfoBarSeverity.Success, Message: "Einstellungen gespeichert." }
                || settings.Current.MissumAiServerUrl != page.ViewModel.MissumAiServerUrl)
                throw new InvalidOperationException("Der echte Speichern-Handler zeigte keine gespeicherte Erfolgsmeldung: " + page.StatusBar.Message);

            foreach (var theme in StatusSmokeThemes)
            {
                app.ApplyTheme(theme);
                app.ApplyBackgroundColor(theme == AppTheme.Light ? "#ECE7F2" : "#181818");
                foreach (var accent in StatusSmokeAccents)
                {
                    app.ApplyAccentColor(accent);
                    page.StatusBar.ApplyTemplate();
                    host.UpdateLayout();
                    await Task.Delay(80);
                    host.UpdateLayout();
                    var descendants = StatusSmokeDescendants(page.StatusBar).ToArray();
                    var background = descendants.OfType<Border>().Single(item => item.Name == "ContentRoot");
                    var iconBackground = descendants.OfType<TextBlock>().Single(item => item.Name == "IconBackground");
                    var iconForeground = descendants.OfType<TextBlock>().Single(item => item.Name == "StandardIcon");
                    var message = descendants.OfType<TextBlock>().Single(item => item.Name == "Message");
                    VerifyBrush(background.Background, "MissumAccentSubtleBrush");
                    VerifyBrush(iconBackground.Foreground, "MissumAccentBrush");
                    VerifyBrush(iconForeground.Foreground, "MissumAccentTextBrush");
                    VerifyBrush(message.Foreground, "MissumTextBrush");
                    if (page.ActualTheme != (theme == AppTheme.Light ? ElementTheme.Light : ElementTheme.Dark)
                        || page.StatusBar.ActualHeight < 32 || page.StatusBar.ActualWidth < 100)
                        throw new InvalidOperationException("Die Erfolgsmeldung wurde nicht im erwarteten nativen Theme dargestellt.");
                    var fileName = "native-settings-save-" + theme.ToString().ToLowerInvariant() + "-" + accent[1..] + ".png";
                    await SaveStatusSmokePreviewAsync(page.StatusBar, Path.Combine(app.DataDirectory, fileName));
                    variants.Add(new { theme = theme.ToString(), accent, background = BrushValue(background.Background),
                        icon = BrushValue(iconBackground.Foreground), iconForeground = BrushValue(iconForeground.Foreground),
                        text = BrushValue(message.Foreground), fileName });
                }
            }
            await File.WriteAllTextAsync(Path.Combine(app.DataDirectory, "native-settings-save-validation.json"),
                JsonSerializer.Serialize(new { passed = true, renderer = "WinUI3", actualSaveHandler = true,
                    persistedInIsolatedProfile = true, liveThemeAndAccentChanges = true, variants, modelRequests = 0 }));
        }
        finally
        {
            host.Children.Remove(page);
            await settings.UpdateAsync(current => current with
            {
                MissumAiServerUrl = saved.MissumAiServerUrl,
                Theme = saved.Theme,
                AccentColor = saved.AccentColor,
                BackgroundColor = saved.BackgroundColor,
            });
            await page.ViewModel.InitializeAsync();
            app.ApplyTheme(originalTheme);
            app.ApplyBackgroundColor(originalBackground);
            app.ApplyAccentColor(originalAccent);
            host.UpdateLayout();
        }

        void VerifyBrush(Brush actual, string key)
        {
            if (actual is not SolidColorBrush paint || app.Resources[key] is not SolidColorBrush expected
                || paint.Color != expected.Color || Math.Abs(paint.Opacity - expected.Opacity) > .001)
                throw new InvalidOperationException("Die native Erfolgsmeldung verwendet nicht " + key + ".");
        }
    }

    private static string BrushValue(Brush brush) => brush is SolidColorBrush paint
        ? $"#{paint.Color.A:X2}{paint.Color.R:X2}{paint.Color.G:X2}{paint.Color.B:X2}@{paint.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "not-solid";

    private static IEnumerable<DependencyObject> StatusSmokeDescendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in StatusSmokeDescendants(child)) yield return descendant;
        }
    }

    private static async Task SaveStatusSmokePreviewAsync(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth < 100 || bitmap.PixelHeight < 32)
            throw new InvalidOperationException("Die Erfolgsmeldung besitzt keine sichtbare Darstellung.");
        var buffer = await bitmap.GetPixelsAsync();
        var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }
}

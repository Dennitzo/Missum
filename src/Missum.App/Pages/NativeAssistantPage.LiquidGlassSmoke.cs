using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static readonly JsonSerializerOptions LiquidGlassReportOptions = new() { WriteIndented = true };

    /// <summary>
    /// Visual Liquid Glass smoke for the isolated profile. The acrylic backdrop is
    /// composed by DWM and is invisible to RenderTargetBitmap, so every variant is
    /// announced as <c>glass-N.json</c> (with the window rectangle) in the directory
    /// named by MISSUM_SMOKE_UI_LIQUIDGLASS; an external capture of the real screen
    /// answers with <c>glass-N.done</c>. Liquid Glass applies after a restart, so the
    /// smoke flips and saves the real settings switch at the end and verifies that the
    /// running session keeps its look; the next launch on the same profile shows the
    /// other state (MISSUM_SMOKE_UI_LIQUIDGLASS_EXPECT = on/off).
    /// </summary>
    private async Task VerifyLiquidGlassSmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var app = App.Current;
        var window = app.MainWindow ?? throw new InvalidOperationException("Hauptfenster fehlt.");
        var expectGlass = Environment.GetEnvironmentVariable("MISSUM_SMOKE_UI_LIQUIDGLASS_EXPECT") == "on";
        if (app.IsLiquidGlassEnabled != expectGlass)
            throw new InvalidOperationException($"Liquid Glass beim Start {app.IsLiquidGlassEnabled}, erwartet {expectGlass}.");
        window.AppWindow.Move(new Windows.Graphics.PointInt32(40, 40));
        window.AppWindow.Resize(SmokeWindowSize(1360, 860));
        window.Activate();
        var prefix = expectGlass ? "on" : "off";
        var checks = new List<object>();
        var step = 0;
        var samples = AddLiquidGlassSampleMessages();
        try
        {
            (string Name, AppTheme Theme, string Accent, string Background)[] variants =
            [
                ("dark-default", AppTheme.Dark, AppSettings.DefaultAccentColor, AppSettings.DefaultBackgroundColor),
                ("light-default", AppTheme.Light, AppSettings.DefaultAccentColor, AppSettings.DefaultBackgroundColor),
                ("dark-accent", AppTheme.Dark, "#A970FF", "#261D45"),
                ("light-accent", AppTheme.Light, "#4C8DFF", "#4C8DFF"),
                ("dark-cyberpunk", AppTheme.Dark, AppSettings.CyberpunkAccentColor, "#261D45"),
            ];
            foreach (var variant in variants)
            {
                app.ApplyTheme(variant.Theme);
                app.ApplyAccentColor(variant.Accent);
                app.ApplyBackgroundColor(variant.Background);
                // Palette refreshes re-show the empty-session welcome text over the sample messages.
                WelcomePanel.Visibility = Visibility.Collapsed;
                checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-{variant.Name}", expectGlass));
            }

            // The "+" tool menu stays opaque for readability, also with Liquid Glass.
            app.ApplyTheme(AppTheme.Dark);
            app.ApplyAccentColor("#A970FF");
            app.ApplyBackgroundColor("#261D45");
            var toolsMenu = ShowComposerTools();
            await Task.Delay(500);
            var menuBrush = (SolidColorBrush)app.Resources["MissumMenuBrush"];
            if (menuBrush.Color.A != 0xFF)
                throw new InvalidOperationException("Das Werkzeugmenü ist nicht deckend: " + menuBrush.Color);
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-tools-menu", expectGlass));
            // A long tool description scrolls through while its row is hovered or keyboard-focused.
            var browserUse = FindSmokeButton(toolsMenu.Content as DependencyObject, "Browser-Use")
                ?? throw new InvalidOperationException("Werkzeug Browser-Use fehlt im Menü.");
            browserUse.Focus(FocusState.Keyboard);
            await Task.Delay(2200);
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-tools-menu-marquee", expectGlass));
            toolsMenu.Hide();
            await Task.Delay(300);

            // The mode dropdown is a menu too and stays opaque with Liquid Glass.
            OnModeMenu(ModeButton, new RoutedEventArgs());
            await Task.Delay(500);
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-mode-menu", expectGlass));
            // A long mode description scrolls through while its entry is hovered or keyboard-focused.
            var meetings = VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)
                .Select(popup => FindSmokeButton(popup.Child, "Claude Meetings")).FirstOrDefault(button => button is not null)
                ?? throw new InvalidOperationException("Modus-Eintrag Claude Meetings fehlt.");
            meetings.Focus(FocusState.Keyboard);
            await Task.Delay(1800);
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-mode-menu-marquee", expectGlass));
            foreach (var popup in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot)) popup.IsOpen = false;
            await Task.Delay(300);

            // The prompt timeline preview is the bordered card alone, without a tooltip box around it.
            var previewOwner = (FrameworkElement)samples[0];
            var preview = CreatePromptPreviewToolTip(new("1) Setze die Forschungsarbeit in `Publikation.md` ab §19.17 fort. Arbeite sauber weiter.",
                "Ich setze die Arbeit ab Abschnitt 19.17 fort und prüfe danach die Quellen und Querverweise."));
            ToolTipService.SetToolTip(previewOwner, preview);
            preview.IsOpen = true;
            await Task.Delay(400);
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-timeline-preview", expectGlass));
            preview.IsOpen = false;
            ToolTipService.SetToolTip(previewOwner, null);
            await Task.Delay(300);

            // The real settings switch only edits the saved preference: the running
            // session keeps its look until Missum is started again.
            app.ApplyTheme(AppTheme.Dark);
            app.ApplyAccentColor("#A970FF");
            app.ApplyBackgroundColor("#261D45");
            await window.NavigateForSmokeAsync("settings");
            var settingsPage = await WaitForSettingsPageAsync(window);
            settingsPage.BringLiquidGlassSwitchIntoView();
            settingsPage.LiquidGlassSwitchIsOn = !expectGlass;
            await settingsPage.ViewModel.SavePreferencesAsync();
            if (app.GetService<SettingsCoordinator>().Current.IsLiquidGlassEnabled != !expectGlass)
                throw new InvalidOperationException("Der Liquid-Glass-Schalter wurde nicht gespeichert.");
            if (app.IsLiquidGlassEnabled != expectGlass)
                throw new InvalidOperationException("Liquid Glass wurde ohne Neustart umgeschaltet.");
            checks.Add(await CaptureLiquidGlassVariantAsync(directory, ++step, $"{prefix}-settings-switched", expectGlass));
            await window.OpenAssistantSessionAsync(refreshSession: false);
        }
        finally
        {
            foreach (var sample in samples) MessagesPanel.Children.Remove(sample);
            WelcomePanel.Visibility = MessagesPanel.Children.Count == 0 ? Visibility.Visible : WelcomePanel.Visibility;
            await File.WriteAllTextAsync(Path.Combine(directory, "glass-report.json"),
                JsonSerializer.Serialize(new { steps = step, expectGlass, checks }, LiquidGlassReportOptions));
        }
    }

    private static Button? FindSmokeButton(DependencyObject? root, string automationName)
    {
        if (root is null) return null;
        if (root is Button button && Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == automationName) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindSmokeButton(VisualTreeHelper.GetChild(root, index), automationName) is { } found) return found;
        return null;
    }

    private static async Task<object> CaptureLiquidGlassVariantAsync(string directory, int step, string name, bool glass)
    {
        var app = App.Current;
        var window = app.MainWindow!;
        await Task.Delay(700);
        var windowBrush = (SolidColorBrush)app.Resources["MissumWindowBrush"];
        var layerBrush = (SolidColorBrush)app.Resources["MissumLayerBrush"];
        var button = app.Resources.TryGetValue("ButtonBackground", out var buttonValue) ? buttonValue?.GetType().Name : null;
        var page = window.CurrentPage as NativeAssistantPage;
        var composer = page?.ComposerSurface.Background?.GetType().Name;
        var sidebar = page?.Sidebar.Background?.GetType().Name;
        var shown = window.IsLiquidGlassShown;
        var backdrop = window.SystemBackdrop?.GetType().Name;
        var check = new
        {
            step, name, glass, shown, backdrop, composer, sidebar, button,
            windowAlpha = windowBrush.Color.A, layerAlpha = layerBrush.Color.A,
            accent = app.AccentColor, background = app.BackgroundColor,
        };
        // The floating inspector pane must fit inside its column (no clipped right edge).
        var inspectorFits = page is null || page.Inspector.Visibility != Visibility.Visible
            || page.Inspector.ActualWidth + page.Inspector.Margin.Left + page.Inspector.Margin.Right <= page.InspectorColumn.ActualWidth + 0.5;
        if (!inspectorFits)
            throw new InvalidOperationException($"Rechte Seitenleiste ragt aus ihrer Spalte: {page!.Inspector.ActualWidth} + Rand > {page.InspectorColumn.ActualWidth}");
        var pageOk = page is null || (glass
            ? composer == nameof(AcrylicBrush) && sidebar == nameof(AcrylicBrush)
            : composer == nameof(SolidColorBrush) && sidebar == nameof(SolidColorBrush));
        var expectedOk = pageOk && backdrop == nameof(MicaBackdrop) && (glass
            ? shown && windowBrush.Color.A == 0 && layerBrush.Color.A is > 0 and < 0xE6 && button == nameof(AcrylicBrush)
            : !shown && windowBrush.Color.A == 0xFF && layerBrush.Color.A >= 0xE6 && button != nameof(AcrylicBrush));
        if (!expectedOk)
            throw new InvalidOperationException("Liquid-Glass-Zustand falsch: " + JsonSerializer.Serialize(check));

        var position = window.AppWindow.Position;
        var size = window.AppWindow.Size;
        var marker = Path.Combine(directory, $"glass-{step}.json");
        var done = Path.Combine(directory, $"glass-{step}.done");
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            step, name, x = position.X, y = position.Y, width = size.Width, height = size.Height,
        }));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (!File.Exists(done))
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Keine Bildschirmaufnahme für {name} erhalten.");
            await Task.Delay(100);
        }
        return check;
    }

    /// <summary>Visual-only sample conversation so the composer glass has content scrolling beneath it.</summary>
    private List<UIElement> AddLiquidGlassSampleMessages()
    {
        var added = new List<UIElement>();
        WelcomePanel.Visibility = Visibility.Collapsed;
        string[] paragraphs =
        [
            "Kannst du mir die wichtigsten Unterschiede zwischen Mica, Acrylic und Liquid Glass erklären?",
            "Mica färbt das Fenster leicht mit dem Desktop-Hintergrund ein und bleibt dabei ruhig. Acrylic ist ein milchiges Glas, das Inhalte dahinter weichzeichnet. Liquid Glass ergänzt das um Glanzkanten, Licht und Tiefe.",
            "Wie wirkt die Akzentfarbe im Glas?",
            "Die Akzentfarbe leuchtet hinter dem Eingabefeld und spiegelt sich in der Glaskante beim Senden-Knopf. Der Hintergrund tönt alle Glasflächen.",
            "Und bleibt das Werkzeugmenü lesbar?",
            "Ja. Menüs mit vielen Einträgen bleiben deckend, damit jeder Eintrag gut lesbar ist. Die Glasflächen gelten für Leisten, Schaltflächen und das Eingabefeld.",
            "Zeig mir bitte noch ein Beispiel mit etwas mehr Text, damit man sieht, wie die Nachrichten unter dem schwebenden Eingabefeld weiterlaufen und durch das Glas weichgezeichnet werden.",
            "Gern. Scrollt der Verlauf nach oben, gleiten die Zeilen unter das Eingabefeld. Dort erscheinen sie verschwommen und farbig getönt, wie hinter einer echten Glasscheibe. So hebt sich das Eingabefeld deutlich vom Hintergrund ab.",
        ];
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var user = index % 2 == 0;
            var text = new TextBlock { Text = paragraphs[index], TextWrapping = TextWrapping.Wrap, FontSize = 15 };
            UIElement element = user
                ? new Border
                {
                    Child = text, HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = 560, Padding = new Thickness(14, 10, 14, 10),
                    CornerRadius = new CornerRadius(18), Background = ThemeBrush("MissumLayerStrongBrush", 44),
                }
                : text;
            MessagesPanel.Children.Add(element);
            added.Add(element);
        }
        ConversationScroll.UpdateLayout();
        // Leave the last answer partly beneath the floating composer, so its glass frosts real text.
        ConversationScroll.ChangeView(null, Math.Max(0, ConversationScroll.ScrollableHeight - ComposerDock.ActualHeight * 0.8), null, disableAnimation: true);
        return added;
    }

    private static async Task<SettingsPage> WaitForSettingsPageAsync(MainWindow window)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (window.CurrentPage is SettingsPage { IsLoaded: true } page)
            {
                await Task.Delay(500);
                return page;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Die Einstellungsseite wurde nicht geladen.");
    }
}

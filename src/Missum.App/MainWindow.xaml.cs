using Missum.App.Pages;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Globalization;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;
using Microsoft.Extensions.Logging;

namespace Missum.App;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Type> _routes = new(StringComparer.Ordinal)
    {
        ["assistant"] = typeof(NativeAssistantPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };
    private readonly SettingsCoordinator _settings;
    private readonly ILogger<MainWindow> _logger;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly AppWindow _appWindow;
    private readonly nint _windowHandle;
    private NativeAssistantPage? _assistantPage;
    private bool _restored;
    private bool _isClosing;
    private bool _closePreparationStarted;
    private bool _allowClose;
    private WindowPlacement _lastNormalPlacement = new();
    private WindowDisplayState _lastNonMinimizedState = WindowDisplayState.Normal;

    public MainWindow(
        ShellViewModel viewModel,
        SettingsCoordinator settings,
        ILogger<MainWindow> logger)
    {
        ViewModel = viewModel;
        _settings = settings;
        _logger = logger;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        var iconPath = ApplicationAssets.ResolvePath("Assets", "AppLogo.ico");
        if (File.Exists(iconPath))
        {
            _appWindow.SetIcon(iconPath);
        }
        ConfigureTitleBar();
        App.Current.ThemeChanged += OnAppThemeChanged;
        AppTitleBar.SizeChanged += (_, _) =>
        {
            var scale = Content.XamlRoot?.RasterizationScale ?? 1;
            var input = Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(windowId);
            input.SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough,
                [new RectInt32(0, 0, (int)(420 * scale), (int)(44 * scale))]);
        };

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(450);
        _saveTimer.Tick += OnSaveTimerTick;
        _appWindow.Changed += OnAppWindowChanged;
        _appWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
    }

    public ShellViewModel ViewModel { get; }

    internal Func<Task>? BeforeCloseAsync { get; set; }

    public async Task SaveStateAsync()
    {
        if (!_restored)
        {
            return;
        }

        var state = _isClosing ? _lastNormalPlacement.State : GetPresenterState();
        var placement = !_isClosing && IsPresenterRestored()
            ? CaptureNormalPlacement()
            : _lastNormalPlacement with { State = state };
        var selectedRoute = GetSelectedRoute();
        await _settings.UpdateAsync(current => current with
        {
            Window = placement,
            LastRoute = selectedRoute,
        });
    }

    public void BringToForeground()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
        {
            presenter.Restore();
        }

        Activate();
    }

    internal async Task OpenAssistantSessionAsync(bool refreshSession)
    {
        await NavigateToAsync("assistant");

        if (refreshSession && ContentFrame.Content is NativeAssistantPage assistantPage)
        {
            await assistantPage.RefreshForExternalActivationAsync();
        }
    }

    private async void OnMenuAssistant(object sender, RoutedEventArgs e) =>
        await OpenAssistantSessionAsync(refreshSession: false);

    private async void OnMenuSettings(object sender, RoutedEventArgs e) => await NavigateToAsync("settings");
    private void OnMenuClose(object sender, RoutedEventArgs e) => Close();
    private async void OnMenuCompose(object sender, RoutedEventArgs e) { await NavigateToAsync("assistant"); _assistantPage?.FocusComposer(); }
    private void OnMenuSidebar(object sender, RoutedEventArgs e) => _assistantPage?.OnToggleSidebar(sender, e);
    private void OnMenuInspector(object sender, RoutedEventArgs e) => _assistantPage?.OnToggleInspector(sender, e);
    private async void OnMenuAbout(object sender, RoutedEventArgs e)
    {
        await new ContentDialog { XamlRoot = Content.XamlRoot, Title = "Missum", Content = "Native WinUI 3 · Lokaler AI-Arbeitsbereich\nChat und Codex mit der Werkzeug- und Docker-Anbindung aus Missum.", CloseButtonText = "Schließen" }.ShowAsync();
    }
    private void ConfigureTitleBar()
    {
        var titleBar = _appWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppTitleBar.ActualThemeChanged += OnTitleBarActualThemeChanged;
        UpdateTitleBarColors();
    }

    private void OnTitleBarActualThemeChanged(FrameworkElement sender, object args) =>
        UpdateTitleBarColors();

    private void OnAppThemeChanged(object? sender, EventArgs args) => UpdateTitleBarColors();

    private void UpdateTitleBarColors()
    {
        var titleBar = _appWindow.TitleBar;
        if (App.Current.IsHighContrastActive)
        {
            var palette = App.Current.ResolveHighContrastPalette();
            titleBar.ForegroundColor = palette.Foreground;
            titleBar.InactiveForegroundColor = palette.Foreground;
            titleBar.ButtonForegroundColor = palette.Foreground;
            titleBar.ButtonInactiveForegroundColor = palette.Foreground;
            titleBar.ButtonHoverForegroundColor = palette.AccentForeground;
            titleBar.ButtonPressedForegroundColor = palette.AccentForeground;
            titleBar.ButtonHoverBackgroundColor = palette.Accent;
            titleBar.ButtonPressedBackgroundColor = palette.Accent;
            return;
        }

        var isLight = AppTitleBar.ActualTheme == ElementTheme.Light;
        var foreground = isLight
            ? Windows.UI.Color.FromArgb(255, 36, 29, 47)
            : Windows.UI.Color.FromArgb(255, 248, 244, 255);
        var inactiveForeground = isLight
            ? Windows.UI.Color.FromArgb(180, 36, 29, 47)
            : Windows.UI.Color.FromArgb(180, 248, 244, 255);
        var hoverBackground = isLight
            ? Windows.UI.Color.FromArgb(24, 36, 29, 47)
            : Windows.UI.Color.FromArgb(28, 255, 255, 255);
        var pressedBackground = isLight
            ? Windows.UI.Color.FromArgb(42, 36, 29, 47)
            : Windows.UI.Color.FromArgb(48, 255, 255, 255);

        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactiveForeground;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactiveForeground;
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedForegroundColor = foreground;
        titleBar.ButtonHoverBackgroundColor = hoverBackground;
        titleBar.ButtonPressedBackgroundColor = pressedBackground;
    }

    private async void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (_restored)
        {
            return;
        }

        _restored = true;
        var settings = _settings.Current;
        try
        {
            RestoreWindow(settings.Window);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.WindowRestoreFailed(_logger, exception);
        }

        await NavigateToAsync(settings.LastRoute);
        await Task.Yield();
        ScheduleSave();
    }

    private void RestoreWindow(WindowPlacement placement)
    {
        var requestedX = ToInt(placement.X, 120);
        var requestedY = ToInt(placement.Y, 80);
        var displayArea = FindSavedDisplay(placement.MonitorId)
            ?? DisplayArea.GetFromPoint(
                new PointInt32(requestedX, requestedY),
                DisplayAreaFallback.Primary);
        if (displayArea is null)
        {
            return;
        }

        var work = displayArea.WorkArea;
        _appWindow.Move(new PointInt32(
            Math.Clamp(requestedX, work.X, work.X + Math.Max(0, work.Width - 1)),
            Math.Clamp(requestedY, work.Y, work.Y + Math.Max(0, work.Height - 1))));

        var currentDpi = Math.Max(96, GetDpi());
        var savedDpi = double.IsFinite(placement.SavedDpi) && placement.SavedDpi >= 48
            ? placement.SavedDpi
            : 96;
        var scale = currentDpi / savedDpi;
        var requestedWidth = ScaleToInt(placement.Width, scale, 1280);
        var requestedHeight = ScaleToInt(placement.Height, scale, 820);
        var minimumWidth = Math.Min(work.Width, ScaleToInt(720, currentDpi / 96, 720));
        var minimumHeight = Math.Min(work.Height, ScaleToInt(540, currentDpi / 96, 540));
        var width = Math.Clamp(requestedWidth, minimumWidth, Math.Max(1, work.Width));
        var height = Math.Clamp(requestedHeight, minimumHeight, Math.Max(1, work.Height));
        var x = Math.Clamp(requestedX, work.X, work.X + work.Width - width);
        var y = Math.Clamp(requestedY, work.Y, work.Y + work.Height - height);
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
        _lastNormalPlacement = new WindowPlacement(
            x,
            y,
            width,
            height,
            GetMonitorId(displayArea),
            currentDpi,
            WindowDisplayState.Normal);
        _lastNonMinimizedState = placement.State;

        if (placement.State == WindowDisplayState.Maximized
            && _appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    private async Task NavigateToAsync(string requestedRoute)
    {
        var route = ShellViewModel.ResolveNavigationRoute(requestedRoute);
        if (ContentFrame.CurrentSourcePageType != _routes[route])
        {
            if (ContentFrame.Content is NativeAssistantPage assistantPage)
                await assistantPage.FlushDraftAsync();
            ContentFrame.Navigate(_routes[route]);
        }
        await _settings.UpdateAsync(current => current with { LastRoute = route });
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is NativeAssistantPage assistantPage)
        {
            _assistantPage = assistantPage;
        }

        ViewModel.ActivePageTitle = e.SourcePageType.Name switch
        {
            nameof(NativeAssistantPage) => "AI Assistent",
            nameof(LogsPage) => "Logs",
            nameof(SettingsPage) => "Einstellungen",
            _ => "Missum",
        };
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        e.Handled = true;
        var pageType = e.SourcePageType?.FullName ?? "unknown";
        AppLog.NavigationFailed(_logger, e.Exception, pageType);
        ViewModel.ActivePageTitle = "Seite nicht verfügbar";

        var content = new StackPanel
        {
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
        };
        content.Children.Add(new FontIcon { Glyph = "\uEA39", FontSize = 32, Foreground = Missum.App.Controls.NativeIconPalette.BrushFor("danger") });
        content.Children.Add(new TextBlock
        {
            Text = "Die Seite konnte nicht geladen werden.",
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Prüfe die lokalen Missum-Komponenten und die Anwendungsprotokolle.",
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        ContentFrame.Content = content;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_restored || _isClosing)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Restored
            && (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange))
        {
            _lastNormalPlacement = CaptureNormalPlacement();
            _lastNonMinimizedState = WindowDisplayState.Normal;
        }
        else if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
        {
            _lastNonMinimizedState = WindowDisplayState.Maximized;
        }

        if (args.DidPositionChange || args.DidSizeChange || args.DidPresenterChange)
        {
            ScheduleSave();
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
        {
            return;
        }

        args.Cancel = true;
        if (_closePreparationStarted)
        {
            return;
        }

        _closePreparationStarted = true;
        if (_assistantPage is { } assistantPage)
        {
            await assistantPage.FlushDraftAsync();
            await assistantPage.CloseSessionToolsAsync();
        }

        try
        {
            await SaveStateAsync();
        }
        catch (Exception exception)
        {
            AppLog.WindowStateSaveFailed(_logger, exception);
        }

        try
        {
            DisposeNativeAssistantPage();
            if (BeforeCloseAsync is not null)
            {
                await BeforeCloseAsync();
            }
        }
        catch (Exception exception)
        {
            AppLog.ShutdownCleanupFailed(_logger, exception);
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void ScheduleSave()
    {
        if (!_restored || _isClosing)
        {
            return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        await SaveStateAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            if (IsPresenterRestored())
            {
                _lastNormalPlacement = CaptureNormalPlacement();
                _lastNonMinimizedState = WindowDisplayState.Normal;
            }
            else if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
            {
                _lastNonMinimizedState = WindowDisplayState.Maximized;
            }

            _lastNormalPlacement = _lastNormalPlacement with { State = _lastNonMinimizedState };
        }
        catch (InvalidOperationException)
        {
            // The last AppWindow.Changed snapshot remains valid during teardown.
        }

        _isClosing = true;
        DisposeNativeAssistantPage();
        _saveTimer.Stop();
        _saveTimer.Tick -= OnSaveTimerTick;
        AppTitleBar.ActualThemeChanged -= OnTitleBarActualThemeChanged;
        App.Current.ThemeChanged -= OnAppThemeChanged;
        _appWindow.Changed -= OnAppWindowChanged;
        _appWindow.Closing -= OnAppWindowClosing;
    }

    private void DisposeNativeAssistantPage()
    {
        var assistantPage = _assistantPage;
        _assistantPage = null;
        try
        {
            assistantPage?.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppLog.ShutdownCleanupFailed(_logger, exception);
        }
    }

    private WindowPlacement CaptureNormalPlacement()
    {
        var displayArea = DisplayArea.GetFromWindowId(
            _appWindow.Id,
            DisplayAreaFallback.Primary);
        return new WindowPlacement(
            _appWindow.Position.X,
            _appWindow.Position.Y,
            _appWindow.Size.Width,
            _appWindow.Size.Height,
            displayArea is null ? null : GetMonitorId(displayArea),
            GetDpi(),
            WindowDisplayState.Normal);
    }

    private WindowDisplayState GetPresenterState()
    {
        return _appWindow.Presenter is OverlappedPresenter presenter
            ? presenter.State switch
            {
                OverlappedPresenterState.Maximized => WindowDisplayState.Maximized,
                OverlappedPresenterState.Restored => WindowDisplayState.Normal,
                _ => _lastNonMinimizedState,
            }
            : _lastNonMinimizedState;
    }

    private bool IsPresenterRestored() =>
        _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored };

    private string GetSelectedRoute()
    {
        if (ContentFrame.CurrentSourcePageType == typeof(SettingsPage)) return "settings";
        if (ContentFrame.CurrentSourcePageType == typeof(LogsPage)) return "logs";
        return "assistant";
    }

    private double GetDpi()
    {
        var nativeDpi = GetDpiForWindow(_windowHandle);
        if (nativeDpi >= 48)
        {
            return nativeDpi;
        }

        var scale = ContentFrame.XamlRoot?.RasterizationScale ?? 1;
        return double.IsFinite(scale) && scale > 0 ? scale * 96 : 96;
    }

    private static DisplayArea? FindSavedDisplay(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId))
        {
            return null;
        }

        var displays = DisplayArea.FindAll();
        for (var index = 0; index < displays.Count; index++)
        {
            var area = displays[index];
            if (string.Equals(GetMonitorId(area), monitorId, StringComparison.OrdinalIgnoreCase))
            {
                return area;
            }
        }

        return null;
    }

    private static string GetMonitorId(DisplayArea area) => area.DisplayId.Value.ToString("X16", CultureInfo.InvariantCulture);

    private static int ScaleToInt(double value, double scale, int fallback)
    {
        if (!double.IsFinite(value) || value <= 0 || !double.IsFinite(scale) || scale <= 0)
        {
            return fallback;
        }

        return (int)Math.Clamp(
            Math.Round(value * scale, MidpointRounding.AwayFromZero),
            1,
            int.MaxValue);
    }

    private static int ToInt(double value, int fallback)
    {
        return double.IsFinite(value)
            ? (int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue)
            : fallback;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint windowHandle);
}




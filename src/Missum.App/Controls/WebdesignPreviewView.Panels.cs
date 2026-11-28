using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Missum.App.Services.Webdesign;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

public sealed partial class WebdesignPreviewView
{
    private readonly Dictionary<string, ToggleButton> _deviceButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToggleButton> _modeButtons = new(StringComparer.Ordinal);
    private readonly TextBox _address = new() { MinWidth = 100, MaxWidth = 420, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _back = IconButton("", "Zurück");
    private readonly Button _forward = IconButton("", "Vor");
    private readonly Button _reload = IconButton("", "Neu laden");
    private readonly Button _rotate = IconButton("", "Hoch- oder Querformat");
    private readonly Button _serverChip = new() { Height = 30, Padding = new Thickness(10, 0, 10, 0) };
    private readonly TextBlock _serverChipText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _serverDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _compareToggle = new() { Height = 30, Padding = new Thickness(10, 0, 10, 0), IsEnabled = false };
    private readonly StackPanel _tunePanel = new() { Spacing = 4, Visibility = Visibility.Collapsed };
    // Hosting details open inside the tool pill: a Flyout anchored in the pill above the
    // WebView2 is rejected by WinUI (E_INVALIDARG), so no popup is used here.
    private readonly Border _hostingPanel = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(8, 6, 8, 6) };
    private readonly TextBlock _toast = new() { FontSize = 12, Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _compare = new() { Visibility = Visibility.Collapsed };
    private readonly Image _compareBefore = new() { Stretch = Stretch.Uniform };
    private readonly Image _compareAfter = new() { Stretch = Stretch.Uniform };
    private readonly Slider _compareSlider = new() { Minimum = 0, Maximum = 100, Value = 50, Width = 360 };
    private readonly Grid _compareBeforeHost = new();
    private string _overlayMode = "none";
    private JsonElement? _tuneTarget;
    private readonly Dictionary<string, string> _tuneOriginal = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tuneValues = new(StringComparer.Ordinal);
    private byte[]? _beforePng;
    private byte[]? _afterPng;
    private string? _beforeUrl;
    private HashSet<int> _doneNumbers = [];
    private DispatcherTimer? _toastTimer;

    private static Button IconButton(string glyph, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Width = 32, Height = 30, Padding = new Thickness(0),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private readonly List<TextBlock> _toolbarLabels = [];

    /// <summary>Labels give way to icons when the tab is narrow (inspector open, small window).</summary>
    private void UpdateToolbarLabels()
    {
        var wide = _stage.ActualWidth == 0 || _stage.ActualWidth >= 940;
        var visible = wide ? Visibility.Visible : Visibility.Collapsed;
        foreach (var label in _toolbarLabels) if (label.Visibility != visible) label.Visibility = visible;
        _address.Width = wide ? 300 : 160;
    }

    /// <summary>
    /// Toggles look exactly like the buttons of the Claude Meetings tool pill: transparent, the
    /// Missum hover and pressed surfaces, the same corner radius as their hover state. A checked
    /// tool keeps the pressed surface instead of the WinUI accent fill.
    /// </summary>
    internal static ToggleButton PillToggle(ToggleButton toggle)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var hover = NativeThemeBrushes.Resource("MissumHoverBrush", 60);
        var pressed = NativeThemeBrushes.Resource("MissumPressedBrush", 80);
        var text = NativeThemeBrushes.Resource("MissumTextBrush", 240);
        foreach (var (key, brush) in new (string, Brush)[]
        {
            ("ToggleButtonBackground", transparent), ("ToggleButtonBackgroundPointerOver", hover), ("ToggleButtonBackgroundPressed", pressed),
            ("ToggleButtonBackgroundDisabled", transparent),
            ("ToggleButtonBackgroundChecked", pressed), ("ToggleButtonBackgroundCheckedPointerOver", pressed), ("ToggleButtonBackgroundCheckedPressed", hover),
            ("ToggleButtonBackgroundCheckedDisabled", transparent),
            ("ToggleButtonBorderBrush", transparent), ("ToggleButtonBorderBrushPointerOver", transparent), ("ToggleButtonBorderBrushPressed", transparent),
            ("ToggleButtonBorderBrushChecked", transparent), ("ToggleButtonBorderBrushCheckedPointerOver", transparent), ("ToggleButtonBorderBrushCheckedPressed", transparent),
            ("ToggleButtonBorderBrushDisabled", transparent), ("ToggleButtonBorderBrushCheckedDisabled", transparent),
            ("ToggleButtonForeground", text), ("ToggleButtonForegroundPointerOver", text), ("ToggleButtonForegroundPressed", text),
            ("ToggleButtonForegroundChecked", text), ("ToggleButtonForegroundCheckedPointerOver", text), ("ToggleButtonForegroundCheckedPressed", text),
        })
            toggle.Resources[key] = brush;
        toggle.BorderThickness = new Thickness(0);
        toggle.CornerRadius = new CornerRadius(4);
        return toggle;
    }

    /// <summary>A plain button with the same transparent pill look as <see cref="PillToggle"/>.</summary>
    internal static Button PillButton(Button button)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var hover = NativeThemeBrushes.Resource("MissumHoverBrush", 60);
        var pressed = NativeThemeBrushes.Resource("MissumPressedBrush", 80);
        var text = NativeThemeBrushes.Resource("MissumTextBrush", 240);
        foreach (var (key, brush) in new (string, Brush)[]
        {
            ("ButtonBackground", transparent), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", pressed),
            ("ButtonBackgroundDisabled", transparent),
            ("ButtonBorderBrush", transparent), ("ButtonBorderBrushPointerOver", transparent), ("ButtonBorderBrushPressed", transparent),
            ("ButtonBorderBrushDisabled", transparent),
            ("ButtonForeground", text), ("ButtonForegroundPointerOver", text), ("ButtonForegroundPressed", text),
        })
            button.Resources[key] = brush;
        button.Background = transparent;
        button.BorderThickness = new Thickness(0);
        return button;
    }

    private ToggleButton ToolToggle(string glyph, string text, string tip)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        var caption = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _toolbarLabels.Add(caption);
        content.Children.Add(caption);
        var button = PillToggle(new ToggleButton { Content = content, Height = 30, Padding = new Thickness(10, 0, 11, 0) });
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, tip);
        return button;
    }

    // The tools float over the page in a pill styled like the Codex prompt window: draggable by
    // its grip, collapsible to a small handle, translucent so the page stays visible behind it.
    private readonly Canvas _overlayLayer = new();
    private readonly Border _toolOverlay = new();
    private readonly StackPanel _overlayBody = new() { Spacing = 6 };
    private readonly StackPanel _overlayMini = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _miniDot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _collapseButton = IconButton("", "Werkzeuge einklappen");
    private bool _overlayCollapsed;
    private bool _overlayHovered;
    private (double X, double Y)? _overlayAnchor;
    private Windows.Foundation.Point? _dragStart;
    private (double Left, double Top) _dragOrigin;

    private void BuildToolbar()
    {
        // Row 1 only: the page uses the whole height, the pill floats above it.
        SetRow(_overlayLayer, 1);
        Canvas.SetZIndex(_overlayLayer, 10);
        _overlayLayer.SizeChanged += (_, _) => PlaceOverlay();
        Children.Add(_overlayLayer);

        _toolOverlay.CornerRadius = new CornerRadius(26);
        _toolOverlay.BorderThickness = new Thickness(1);
        _toolOverlay.Padding = new Thickness(6, 8, 10, 8);
        _toolOverlay.Translation = new System.Numerics.Vector3(0, 0, 32);
        _toolOverlay.Shadow = new ThemeShadow();
        _toolOverlay.SizeChanged += (_, _) => PlaceOverlay();
        PointerHover.Track(_toolOverlay, value => { _overlayHovered = value; UpdateOverlaySurface(); });
        AutomationProperties.SetName(_toolOverlay, "Werkzeuge der Webseite");
        ActualThemeChanged += (_, _) => UpdateOverlaySurface();
        UpdateOverlaySurface();

        var layout = new Grid { ColumnSpacing = 6 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var grip = new Border
        {
            Width = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new FontIcon { Glyph = "", FontSize = 14, Foreground = NativeThemeBrushes.MutedText, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(grip, "Werkzeugleiste verschieben");
        ToolTipService.SetToolTip(grip, "Ziehen zum Verschieben · Doppelklick setzt die Position zurück");
        grip.PointerEntered += (_, _) => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.SizeAll);
        grip.PointerExited += (_, _) => { if (_dragStart is null) ProtectedCursor = null; };
        grip.PointerPressed += (_, args) =>
        {
            _dragStart = args.GetCurrentPoint(_overlayLayer).Position;
            _dragOrigin = (Canvas.GetLeft(_toolOverlay), Canvas.GetTop(_toolOverlay));
            grip.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (_dragStart is not { } start) return;
            var point = args.GetCurrentPoint(_overlayLayer).Position;
            MoveOverlay(_dragOrigin.Left + point.X - start.X, _dragOrigin.Top + point.Y - start.Y);
            args.Handled = true;
        };
        void EndDrag()
        {
            _dragStart = null;
            ProtectedCursor = null;
        }
        grip.PointerReleased += (_, args) => { grip.ReleasePointerCapture(args.Pointer); EndDrag(); };
        grip.PointerCaptureLost += (_, _) => EndDrag();
        grip.DoubleTapped += (_, _) => { _overlayAnchor = null; PlaceOverlay(); };
        layout.Children.Add(grip);

        // Navigation, path and server (kept wired for state updates, not shown). Shown: devices and the marking tools.
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _back.Click += (_, _) => { if (_core?.CanGoBack == true) _core.GoBack(); };
        _forward.Click += (_, _) => { if (_core?.CanGoForward == true) _core.GoForward(); };
        _reload.Click += async (_, _) => await ReloadKeepingScrollAsync();
        _address.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter || _state is not { Status: "ready" } state) return;
            args.Handled = true;
            var path = _address.Text.Trim().TrimStart('/');
            await NavigateCoreAsync(state.LocalUrl.TrimEnd('/') + "/" + path);
        };
        AutomationProperties.SetName(_address, "Seitenpfad");
        ToolTipService.SetToolTip(_address, "Pfad der angezeigten Seite. Eingabe mit Enter öffnet eine andere Seite des Projekts.");
        _address.CornerRadius = new CornerRadius(15);
        first.Children.Add(_back);
        first.Children.Add(_forward);
        first.Children.Add(_reload);
        first.Children.Add(_address);
        var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chip.Children.Add(_serverDot);
        chip.Children.Add(_serverChipText);
        _serverChip.Content = chip;
        _serverChip.CornerRadius = new CornerRadius(15);
        AutomationProperties.SetName(_serverChip, "Server und Hosting");
        _serverChip.Click += (_, _) => ShowHostingFlyout();
        // The overlay shows only the tool row; navigation and path are not part of it. The server chip
        // sits in the tool row, so hosting (LAN, QR code, restart, hosting without Missum) stays reachable.
        _ = first;

        var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (id, device) in Devices)
        {
            var toggle = PillToggle(new ToggleButton
            {
                Content = new FontIcon { Glyph = device.Glyph, FontSize = 13 }, Width = 32, Height = 30, Padding = new Thickness(0),
            });
            AutomationProperties.SetName(toggle, device.Label);
            ToolTipService.SetToolTip(toggle, id == "free" ? "Frei · volle Breite des Tabs" : $"{device.Label} · {device.Width}×{device.Height}");
            toggle.Click += async (_, _) => { PushUndo(); await SetDeviceAsync(id); };
            _deviceButtons[id] = toggle;
            second.Children.Add(toggle);
        }
        _rotate.Click += async (_, _) => { PushUndo(); await SetDeviceAsync(_device, !_landscape); };
        _rotate.CornerRadius = new CornerRadius(15);
        second.Children.Add(_rotate);
        second.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = NativeThemeBrushes.Resource("MissumStrokeBrush", 70) });
        foreach (var (id, glyph, text, tip) in new[]
        {
            ("mark", "", "Markieren", "Elemente anklicken und als nummerierte Referenz an Missum geben (Umschalt: mehrere)"),
            ("region", "", "Rahmen", "Einen Bereich aufziehen und beschreiben, was sich dort ändern soll"),
            ("tune", "", "Feintuning", "Element wählen und Farbe, Schrift, Abstände live ausprobieren"),
        })
        {
            var toggle = ToolToggle(glyph, text, tip);
            toggle.Click += async (_, _) => await SetOverlayModeAsync(toggle.IsChecked == true ? id : "none");
            _modeButtons[id] = toggle;
            second.Children.Add(toggle);
        }
        _compareToggle.Content = Label("", "Vorher/Nachher");
        PillToggle(_compareToggle);
        _compareToggle.Padding = new Thickness(10, 0, 11, 0);
        ToolTipService.SetToolTip(_compareToggle, "Stand vor und nach dem letzten Lauf vergleichen");
        _compareToggle.Click += (_, _) => ShowCompare(_compareToggle.IsChecked == true);
        second.Children.Add(_compareToggle);
        _undoButton.CornerRadius = new CornerRadius(15);
        _undoButton.IsEnabled = false;
        _undoButton.Click += async (_, _) => await UndoAsync();
        _undoButton.KeyboardAccelerators.Add(new Microsoft.UI.Xaml.Input.KeyboardAccelerator
        {
            Key = Windows.System.VirtualKey.Z, Modifiers = Windows.System.VirtualKeyModifiers.Control,
        });
        second.Children.Add(_undoButton);
        second.Children.Add(new Border { Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = NativeThemeBrushes.Resource("MissumStrokeBrush", 70) });
        second.Children.Add(PillButton(_serverChip));
        _overlayBody.Children.Add(second);
        // Third row, only while an element is being fine-tuned.
        _overlayBody.Children.Add(_tunePanel);
        _overlayBody.Children.Add(_hostingPanel);
        SetColumn(_overlayBody, 1);
        layout.Children.Add(_overlayBody);

        _overlayMini.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        _overlayMini.Children.Add(new TextBlock { Text = "Werkzeuge", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _overlayMini.Children.Add(_miniDot);
        SetColumn(_overlayMini, 1);
        layout.Children.Add(_overlayMini);

        _collapseButton.CornerRadius = new CornerRadius(15);
        _collapseButton.VerticalAlignment = VerticalAlignment.Top;
        _collapseButton.Click += (_, _) => SetOverlayCollapsed(!_overlayCollapsed);
        SetColumn(_collapseButton, 2);
        layout.Children.Add(_collapseButton);

        _toolOverlay.Child = layout;
        _overlayLayer.Children.Add(_toolOverlay);
        SizeChanged += (_, _) => UpdateToolbarLabels();
        UpdateDeviceButtons();
        UpdateServerChip();
    }

    private void SetOverlayCollapsed(bool collapsed)
    {
        _overlayCollapsed = collapsed;
        _overlayBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _overlayMini.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        _collapseButton.Content = new FontIcon { Glyph = collapsed ? "" : "", FontSize = 14 };
        var name = collapsed ? "Werkzeuge ausklappen" : "Werkzeuge einklappen";
        AutomationProperties.SetName(_collapseButton, name);
        ToolTipService.SetToolTip(_collapseButton, name);
    }

    internal bool OverlayCollapsed => _overlayCollapsed;
    internal (double Left, double Top) OverlayPosition => (Canvas.GetLeft(_toolOverlay), Canvas.GetTop(_toolOverlay));
    internal void CollapseOverlayForSmoke(bool collapsed) => SetOverlayCollapsed(collapsed);
    internal void MoveOverlayForSmoke(double left, double top) => MoveOverlay(left, top);

    /// <summary>Translucent like a pane of glass over the page; a little more solid while in use.</summary>
    private void UpdateOverlaySurface()
    {
        var input = NativeThemeBrushes.Resource("MissumInputBrush", 40).Color;
        var alpha = (byte)(_overlayHovered ? 0xE6 : 0xB8);
        _toolOverlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, input.R, input.G, input.B));
        _toolOverlay.BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 70);
    }

    private void MoveOverlay(double left, double top)
    {
        var free = (Width: Math.Max(0, _overlayLayer.ActualWidth - _toolOverlay.ActualWidth), Height: Math.Max(0, _overlayLayer.ActualHeight - _toolOverlay.ActualHeight));
        left = Math.Clamp(left, 0, free.Width);
        top = Math.Clamp(top, 0, free.Height);
        Canvas.SetLeft(_toolOverlay, left);
        Canvas.SetTop(_toolOverlay, top);
        // Remembered relative to the free space, so a resized tab keeps the pill where the user put it.
        _overlayAnchor = (free.Width <= 0 ? 0.5 : left / free.Width, free.Height <= 0 ? 1 : top / free.Height);
    }

    private void PlaceOverlay()
    {
        if (_overlayLayer.ActualWidth <= 0 || _toolOverlay.ActualWidth <= 0) return;
        var free = (Width: Math.Max(0, _overlayLayer.ActualWidth - _toolOverlay.ActualWidth), Height: Math.Max(0, _overlayLayer.ActualHeight - _toolOverlay.ActualHeight));
        var (x, y) = _overlayAnchor ?? (0.5, 1);
        Canvas.SetLeft(_toolOverlay, Math.Round(free.Width * x));
        // Default: centred at the bottom with a margin, like the prompt window.
        Canvas.SetTop(_toolOverlay, _overlayAnchor is null ? Math.Max(0, free.Height - 18) : Math.Round(free.Height * y));
    }

    private StackPanel Label(string glyph, string text)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        var caption = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        _toolbarLabels.RemoveAll(static item => item.Parent is null);
        _toolbarLabels.Add(caption);
        caption.Visibility = _stage.ActualWidth is > 0 and < 940 ? Visibility.Collapsed : Visibility.Visible;
        content.Children.Add(caption);
        return content;
    }

    private void UpdateDeviceButtons()
    {
        foreach (var (id, button) in _deviceButtons) button.IsChecked = id == _device;
        _rotate.IsEnabled = _device != "free";
    }

    private void UpdateAddress()
    {
        if (_currentUrl is null || !Uri.TryCreate(_currentUrl, UriKind.Absolute, out var uri)) { _address.Text = ""; return; }
        if (_address.FocusState == FocusState.Unfocused) _address.Text = Uri.UnescapeDataString(uri.PathAndQuery);
    }

    private void UpdateNavigationButtons()
    {
        _back.IsEnabled = _core?.CanGoBack == true;
        _forward.IsEnabled = _core?.CanGoForward == true;
    }

    private void UpdateServerChip()
    {
        var (color, text) = _state?.Status switch
        {
            "ready" => (NativeThemeBrushes.Resource("MissumSuccessBrush", Microsoft.UI.Colors.SeaGreen), $"Port {_state.Port.ToString(CultureInfo.InvariantCulture)}" + (_state.LanUrl is null ? "" : " · LAN")),
            "starting" or "restarting" => (NativeThemeBrushes.Resource("MissumWarningBrush", Microsoft.UI.Colors.Orange), "Startet …"),
            "failed" => (NativeThemeBrushes.Resource("MissumDangerBrush", Microsoft.UI.Colors.IndianRed), "Fehler"),
            _ => (NativeThemeBrushes.MutedText, "Gestoppt"),
        };
        _serverDot.Fill = color;
        _miniDot.Fill = color;
        _serverChipText.Text = text;
        ToolTipService.SetToolTip(_serverChip, _state is { Status: "ready" } ready
            ? $"{ready.LocalUrl}{(ready.LanUrl is null ? "" : "\n" + ready.LanUrl)}\nHosting-Optionen öffnen"
            : _state?.Message ?? "Server des Projekts");
    }

    // ---------------------------------------------------------------------------------------
    // Hosting flyout
    // ---------------------------------------------------------------------------------------

    private void ShowHostingFlyout()
    {
        var panel = new StackPanel { Spacing = 10, Width = 360, Padding = new Thickness(4) };
        panel.Children.Add(new TextBlock { Text = "Lokales Hosting", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var state = _state;
        if (state is not { Status: "ready" })
        {
            panel.Children.Add(new TextBlock { Text = state?.Message ?? "Der Server läuft nicht.", TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            panel.Children.Add(UrlRow("Auf diesem PC", state.LocalUrl));
            panel.Children.Add(new TextBlock
            {
                Text = state.Kind == "command"
                    ? $"Projekt-Server: {state.Command}"
                    : $"Eingebauter Server · Ordner „{state.Root}“ · Startseite {state.StartPage}",
                FontSize = 12, Foreground = NativeThemeBrushes.MutedText, TextWrapping = TextWrapping.Wrap,
            });
            var lan = new ToggleSwitch { Header = "Im lokalen Netzwerk freigeben (Smartphone, Tablet)", IsOn = state.Lan };
            lan.Toggled += async (_, _) =>
            {
                if (_workspace is not { } workspace || lan.IsOn == state.Lan) return;
                lan.IsEnabled = false;
                try { await _serve.ConfigureAsync(workspace, config => config with { Lan = lan.IsOn }); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { ShowToast(exception.Message); }
            };
            panel.Children.Add(lan);
            if (state.LanUrl is { } lanUrl)
            {
                panel.Children.Add(UrlRow("Im Netzwerk", lanUrl));
                var qr = new Image { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center };
                AutomationProperties.SetName(qr, "QR-Code der Netzwerkadresse");
                _ = LoadQrAsync(qr, lanUrl);
                panel.Children.Add(qr);
                panel.Children.Add(new TextBlock
                {
                    Text = "Mit der Smartphone-Kamera scannen; beide Geräte müssen im selben Netzwerk sein. Fragt Windows nach der Firewall, erlaube den Zugriff nur für private Netzwerke.",
                    FontSize = 12, Foreground = NativeThemeBrushes.MutedText, TextWrapping = TextWrapping.Wrap,
                });
            }
            else if (state.Lan)
                panel.Children.Add(new TextBlock { Text = state.Message ?? "Kein Netzwerk gefunden.", TextWrapping = TextWrapping.Wrap });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var restart = new Button { Content = "Neu starten" };
        restart.Click += async (_, _) => { if (_workspace is { } workspace) await RestartServerAsync(workspace); };
        var external = new Button { Content = "Im Browser öffnen", IsEnabled = _currentUrl is not null };
        external.Click += (_, _) => { if (_currentUrl is { } url) _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url)); };
        var files = new Button { Content = "Ohne Missum hosten …" };
        ToolTipService.SetToolTip(files, "Missum legt Startskript und Anleitung im Projekt an");
        files.Click += (_, _) => ComposeRequested?.Invoke(
            "Erstelle für dieses Webdesign-Projekt ein Startskript start-server.ps1 (bzw. ein serve-Skript in package.json, falls vorhanden) und eine HOSTING.md: lokal starten ohne Missum, freien Port wählen, im Netzwerk freigeben und Hinweise zur Veröffentlichung auf einem Webspace.");
        buttons.Children.Add(restart);
        buttons.Children.Add(external);
        panel.Children.Add(buttons);
        // Second row: the panel is 360 px wide, four buttons in one row would be clipped.
        var more = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        more.Children.Add(files);
        panel.Children.Add(more);
        // The chip sits in the floating tool pill (usually at the bottom): let the flyout pick its side.
        // A collapsed tool pill leaves the chip without size; WinUI then rejects it as anchor.
        var close = new Button { Content = "Schließen" };
        close.Click += (_, _) => _hostingPanel.Visibility = Visibility.Collapsed;
        more.Children.Add(close);
        _hostingPanel.Child = panel;
        _hostingPanel.Visibility = Visibility.Visible;
        // A collapsed pill would hide the panel: expand it while hosting is shown.
        SetOverlayCollapsed(false);
    }

    private static StackPanel UrlRow(string label, string url)
    {
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = NativeThemeBrushes.MutedText });
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        line.Children.Add(new TextBlock { Text = url, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") });
        var copy = IconButton("", "Adresse kopieren");
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(url);
            Clipboard.SetContent(package);
        };
        line.Children.Add(copy);
        row.Children.Add(line);
        return row;
    }

    private static async Task LoadQrAsync(Image target, string url) =>
        target.Source = await BitmapAsync(WebdesignQrCode.Encode(url).ToPng(scale: 6));

    // ---------------------------------------------------------------------------------------
    // Overlay messages and modes
    // ---------------------------------------------------------------------------------------

    internal async Task SetOverlayModeAsync(string mode)
    {
        // Leaving fine-tuning drops its row and any change that was not handed over.
        if (_overlayMode == "tune" && mode != "tune" && _tuneTarget is not null)
        {
            await ResetTunePreviewAsync();
            _tuneTarget = null;
            _tuneValues.Clear();
            ClearUndo();
            RenderTunePanel();
        }
        _overlayMode = mode;
        foreach (var (id, button) in _modeButtons) button.IsChecked = id == mode;
        if (mode != "none" && _compareToggle.IsChecked == true) { _compareToggle.IsChecked = false; ShowCompare(false); }
        if (_core is not null)
            await _core.ExecuteScriptAsync($"window.__missum && window.__missum.setMode({JsonSerializer.Serialize(mode)})");
        if (mode != "none")
            ShowToast(mode switch
            {
                "mark" => "Klicke ein Element an und beschreibe die Änderung; Missum setzt sie direkt um. Esc beendet.",
                "pin" => "Klicke auf ein Element und schreibe deinen Kommentar.",
                "region" => "Ziehe einen Rahmen auf und beschreibe die Änderung.",
                _ => "Klicke ein Element an, um es fein abzustimmen.",
            });
    }

    /// <summary>Last overlay messages and outcome, for the smoke report.</summary>
    internal List<string> MessageDiagnostics { get; } = [];

    private void Diagnose(string line)
    {
        MessageDiagnostics.Add(line);
        if (MessageDiagnostics.Count > 40) MessageDiagnostics.RemoveAt(0);
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (!IsServerUrl(args.Source) || _workspace is not { } workspace) { Diagnose("ignored source " + args.Source); return; }
            using var document = JsonDocument.Parse(args.TryGetWebMessageAsString());
            var message = document.RootElement;
            if (!message.TryGetProperty("nonce", out var nonce) || nonce.GetString() != _nonce) { Diagnose("wrong nonce"); return; }
            var type = message.GetProperty("type").GetString();
            Diagnose("message " + type);
            switch (type)
            {
                case "mark":
                    await AddAnnotationAsync(workspace, message, "mark", Text(message, "comment"));
                    break;
                case "pin":
                    await AddAnnotationAsync(workspace, message, "pin", Text(message, "comment"));
                    break;
                case "region":
                    await AddAnnotationAsync(workspace, message, "region", Text(message, "comment"));
                    break;
                case "tune-select":
                    SelectTuneTarget(message.Clone());
                    break;
                case "tune-change":
                    ApplyTuneGesture(message);
                    break;
                case "badge":
                    if (message.TryGetProperty("number", out var number) && number.TryGetInt32(out var badge)
                        && WebdesignAnnotationStore.Shared.List(workspace).FirstOrDefault(item => item.Number == badge) is { } wish)
                        ShowToast($"#{badge} · {(wish.Status == "done" ? "erledigt" : "in Arbeit")} · {WebdesignAnnotationStore.Describe(wish)}");
                    break;
                case "mode":
                    await SetOverlayModeAsync("none");
                    break;
                case "ready":
                    await RenderAnnotationsAsync();
                    break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnose("error " + exception.GetType().Name + ": " + exception.Message + " @ " + exception.StackTrace?.Split('\n').FirstOrDefault(line => line.Contains("Missum", StringComparison.Ordinal))?.Trim());
            ShowToast("Die Markierung konnte nicht übernommen werden: " + exception.Message);
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static WebdesignBox? Box(JsonElement element, string name = "box") =>
        element.TryGetProperty(name, out var box) && box.ValueKind == JsonValueKind.Object
            ? new WebdesignBox(box.GetProperty("x").GetDouble(), box.GetProperty("y").GetDouble(), box.GetProperty("width").GetDouble(), box.GetProperty("height").GetDouble())
            : null;

    private async Task<WebdesignAnnotation> AddAnnotationAsync(string workspace, JsonElement message, string kind, string? comment,
        IReadOnlyList<WebdesignStyleChange>? styles = null)
    {
        var element = kind == "region" && message.TryGetProperty("element", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : message;
        var viewport = message.TryGetProperty("viewport", out var view) ? (int)view.GetProperty("width").GetDouble() : 0;
        var classes = element.TryGetProperty("classes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(static item => item.GetString() ?? "").Where(static item => item.Length > 0).ToArray() : [];
        var root = _state?.Root ?? ".";
        var hint = await Task.Run(() => WebdesignSourceLocator.Find(workspace, root, Text(element, "id"), classes, Text(element, "text"), Text(element, "tag")));
        var annotation = WebdesignAnnotationStore.Shared.Add(workspace, new WebdesignAnnotation
        {
            Kind = kind,
            Page = Text(message, "page") ?? "/",
            Device = _device,
            ViewportWidth = viewport,
            Selector = Text(element, "selector"),
            Tag = Text(element, "tag"),
            Text = Text(element, "text"),
            Box = Box(message),
            Comment = comment,
            Styles = styles,
            SourceHint = hint,
        });
        var title = $"#{annotation.Number} {KindLabel(kind)}";
        ShowToast($"{title} geht an Missum und wird direkt umgesetzt.");
        if (Box(message, "view") is { } area && AttachmentRequested is { } attach)
        {
            try
            {
                if (await CropViewportAsync(area.X, area.Y, area.Width, area.Height) is { } png)
                {
                    var label = string.Concat((annotation.Text ?? annotation.Tag ?? kind).Take(32).Select(static c => char.IsLetterOrDigit(c) || c == ' ' ? c : '-')).Trim();
                    await attach($"#{annotation.Number} {KindLabel(kind)} {label}.png".Replace("  ", " ", StringComparison.Ordinal), png);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { ShowToast($"{title}: Bildausschnitt nicht verfügbar ({exception.Message})."); }
        }
        SubmitRequested?.Invoke(DirectRequest(annotation));
        return annotation;
    }

    /// <summary>The prompt that hands one wish to Missum; the run also sees all open wishes.</summary>
    internal static string DirectRequest(WebdesignAnnotation wish) =>
        $"Setze meinen Änderungswunsch #{wish.Number.ToString(CultureInfo.InvariantCulture)} direkt um und melde ihn danach mit webdesign.preview resolve als erledigt: "
        + WebdesignAnnotationStore.Describe(wish);

    private static string KindLabel(string kind) => kind switch
    {
        "pin" => "Kommentar",
        "region" => "Bereich",
        "tune" => "Feintuning",
        _ => "Element",
    };

    private void OnAnnotationsChanged(string workspace) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || !string.Equals(workspace, _workspace, StringComparison.OrdinalIgnoreCase)) return;
        RenderAnnotations();
    });

    private void RenderAnnotations()
    {
        _ = RenderAnnotationsAsync();
    }

    private async Task RenderAnnotationsAsync()
    {
        if (_core is null || _workspace is not { } workspace) return;
        var items = WebdesignAnnotationStore.Shared.List(workspace);
        var payload = JsonSerializer.Serialize(items.Select(static item => new
        {
            number = item.Number, kind = item.Kind, status = item.Status, page = item.Page, selector = item.Selector,
            box = item.Box is { } box ? new { x = box.X, y = box.Y, width = box.Width, height = box.Height } : null,
            comment = item.Comment, text = item.Text,
        }));
        try
        {
            await _core.ExecuteScriptAsync($"window.__missum && window.__missum.render({payload})");
            // Freshly finished wishes blink once on the page.
            var done = items.Where(static item => item.Status == "done").Select(static item => item.Number).ToHashSet();
            foreach (var number in done.Except(_doneNumbers))
                await _core.ExecuteScriptAsync($"window.__missum && window.__missum.flash({number.ToString(CultureInfo.InvariantCulture)})");
            _doneNumbers = done;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    // ---------------------------------------------------------------------------------------
    // Side panel: wishes and fine-tuning
    // ---------------------------------------------------------------------------------------

    private void BuildSidePanel()
    {
        // No side panel: wishes go straight to Missum, fine-tuning sits in the floating overlay.
        _toast.Foreground = NativeThemeBrushes.ReadableAccent;
        var toastHost = new Border
        {
            Child = _toast, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 0, 0),
            Background = NativeThemeBrushes.Resource("MissumLayerStrongBrush", 40), BorderThickness = new Thickness(1),
            BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 70), IsHitTestVisible = false,
        };
        toastHost.SetBinding(VisibilityProperty, new Microsoft.UI.Xaml.Data.Binding { Source = _toast, Path = new PropertyPath("Visibility") });
        SetRow(toastHost, 1);
        Canvas.SetZIndex(toastHost, 11);
        Children.Add(toastHost);
    }

    private void ShowToast(string text)
    {
        _toast.Text = text;
        _toast.Visibility = Visibility.Visible;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Stop();
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Start();
    }

    private void HideToast(object? sender, object e)
    {
        _toastTimer?.Stop();
        _toast.Visibility = Visibility.Collapsed;
    }

    /// <summary>A wish to implement right away; the host sends it as a prompt (or steers a running one).</summary>
    public event Action<string>? SubmitRequested;

    // Fine-tuning: the chosen element gets preview styles in the page; nothing is written until
    // the user hands the exact differences to Missum as a tune wish.

    private void SelectTuneTarget(JsonElement element)
    {
        ClearUndo();
        _ = ResetTunePreviewAsync();
        _tuneTarget = element;
        _tuneOriginal.Clear();
        _tuneValues.Clear();
        if (element.TryGetProperty("styles", out var styles))
            foreach (var property in styles.EnumerateObject()) _tuneOriginal[property.Name] = property.Value.GetString() ?? "";
        if (!_tuneOriginal.ContainsKey("transform")) _tuneOriginal["transform"] = "none";
        RenderTunePanel();
    }

    private readonly TextBlock _tuneReadout = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
    private static readonly string[] TuneSwatches = ["#FFFFFF", "#1A1A2E", "#7C5CFF", "#3AD0C5", "#2EA043", "#FFB020", "#E5484D", "#F4F2FB"];

    /// <summary>
    /// The fine-tuning row of the overlay. Geometry is changed with handles directly on the page
    /// (move, scale, rotate, round corners); the row shows the result and offers colour swatches.
    /// </summary>
    private void RenderTunePanel()
    {
        if (_tuneTarget is null) { _tunePanel.Visibility = Visibility.Collapsed; return; }
        _tunePanel.Visibility = Visibility.Visible;
        // The row is built once and only refreshed: re-parenting the colour wheel on every new
        // element failed with "Keine installierten Komponenten gefunden".
        if (_tuneRowBuilt) { UpdateTuneReadout(); SelectColorTarget(_colorTarget); return; }
        _tuneRowBuilt = true;
        // The overlay grows in height for fine-tuning: geometry on the left, colour wheel on the right.
        var row = new Grid { ColumnSpacing = 18 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        UpdateTuneReadout();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(actions);
        row.Children.Add(left);
        var colours = BuildColorSection();
        SetColumn(colours, 1);
        row.Children.Add(colours);
        var apply = new Button { Content = "Umsetzen", CornerRadius = new CornerRadius(15),
            Style = Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null };
        _tuneApplyButton = apply;
        UpdateTuneReadout();
        apply.Click += async (_, _) => await CommitTuneAsync();
        var reset = new Button { Content = "Zurücksetzen", CornerRadius = new CornerRadius(15) };
        reset.Click += async (_, _) => { PushUndo(); await ResetTunePreviewAsync(); _tuneValues.Clear(); UpdateTuneReadout(); };
        var close = IconButton("\uE711", "Feintuning schließen");
        close.CornerRadius = new CornerRadius(15);
        close.Click += async (_, _) =>
        {
            await ResetTunePreviewAsync();
            if (_core is not null) await _core.ExecuteScriptAsync("window.__missum && window.__missum.tuneEnd()");
            _tuneTarget = null; _tuneValues.Clear(); RenderTunePanel();
        };
        actions.Children.Add(apply);
        actions.Children.Add(reset);
        actions.Children.Add(close);
        _tunePanel.Children.Add(new ScrollViewer
        {
            Content = row, MaxWidth = 860, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 0, 0, 4),
        });
    }

    private bool _tuneRowBuilt;
    private const int WheelSize = 132;
    private string _colorTarget = "color";
    private double _wheelBrightness = 1;
    private readonly Image _wheelImage = new() { Width = WheelSize, Height = WheelSize };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _wheelMarker = new()
    {
        Width = 14, Height = 14, StrokeThickness = 2, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false,
    };
    private readonly Slider _brightness = new()
    {
        Orientation = Orientation.Vertical, Height = WheelSize, Minimum = 0, Maximum = 100, Value = 100, IsDirectionReversed = false,
    };
    private readonly TextBox _hexBox = new() { Width = 104, PlaceholderText = "#1A1A2E", CornerRadius = new CornerRadius(15) };
    private readonly Border _colorPreview = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
    private readonly ToggleButton _textTarget = new() { Content = "Text", CornerRadius = new CornerRadius(15), IsChecked = true };
    private readonly ToggleButton _fillTarget = new() { Content = "Fläche", CornerRadius = new CornerRadius(15) };
    private bool _colorSectionBuilt;
    private bool _wheelDragging;
    private bool _colorApplying;
    private string? _pendingColor;

    /// <summary>Colour wheel (hue around, white in the middle), brightness and hex input.</summary>
    private StackPanel BuildColorSection()
    {
        if (!_colorSectionBuilt)
        {
            _colorSectionBuilt = true;
            var wheelHost = new Grid { Width = WheelSize, Height = WheelSize };
            var canvas = new Canvas { Width = WheelSize, Height = WheelSize, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            wheelHost.Children.Add(_wheelImage);
            canvas.Children.Add(_wheelMarker);
            wheelHost.Children.Add(canvas);
            AutomationProperties.SetName(canvas, "Farbrad");
            canvas.PointerPressed += (_, args) => { _wheelDragging = canvas.CapturePointer(args.Pointer); PickFromWheel(args.GetCurrentPoint(canvas).Position); };
            canvas.PointerMoved += (_, args) => { if (_wheelDragging) PickFromWheel(args.GetCurrentPoint(canvas).Position); };
            canvas.PointerReleased += (_, args) => { _wheelDragging = false; canvas.ReleasePointerCapture(args.Pointer); };
            canvas.PointerCaptureLost += (_, _) => _wheelDragging = false;
            _wheelHostElement = wheelHost;
            _brightness.ValueChanged += (_, _) =>
            {
                _wheelBrightness = _brightness.Value / 100;
                RenderWheel();
                // Showing another element's colour moves the slider too; that must not paint anything.
                if (_syncingBrightness) return;
                if (WebdesignColorWheel.ParseHex(_hexBox.Text) is { } current)
                {
                    // Keep hue and saturation, change only the brightness.
                    var spot = WebdesignColorWheel.Locate(current.R, current.G, current.B, WheelSize);
                    if (WebdesignColorWheel.ColorAt(spot.X, spot.Y, WheelSize, _wheelBrightness) is { } darker) ApplyColor(WebdesignColorWheel.Hex(darker));
                }
            };
            AutomationProperties.SetName(_brightness, "Helligkeit");
            ToolTipService.SetToolTip(_brightness, "Helligkeit");
            AutomationProperties.SetName(_hexBox, "Farbe als Hex-Wert");
            _hexBox.KeyDown += (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) ApplyHexInput(); };
            _hexBox.LostFocus += (_, _) => ApplyHexInput();
            _colorPreview.BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 90);
            _textTarget.Click += (_, _) => SelectColorTarget("color");
            _fillTarget.Click += (_, _) => SelectColorTarget("backgroundColor");
            RenderWheel();
        }
        var section = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var targets = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var input = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        input.Children.Add(_colorPreview);
        input.Children.Add(_hexBox);
        targets.Children.Add(new TextBlock { Text = "Farbe für", FontSize = 11, Foreground = NativeThemeBrushes.MutedText });
        targets.Children.Add(_textTarget);
        targets.Children.Add(_fillTarget);
        targets.Children.Add(input);
        section.Children.Add(_wheelHostElement!);
        section.Children.Add(_brightness);
        section.Children.Add(targets);
        SelectColorTarget(_colorTarget);
        return section;
    }

    private FrameworkElement? _wheelHostElement;

    private WriteableBitmap? _wheelBitmap;
    private bool _syncingBrightness;

    /// <summary>One bitmap for the wheel, rewritten in place; a new imaging object per change failed
    /// with "Keine installierten Komponenten gefunden" when switching elements.</summary>
    private void RenderWheel()
    {
        try
        {
            var pixels = WebdesignColorWheel.Bgra(WheelSize, _wheelBrightness);
            _wheelBitmap ??= new WriteableBitmap(WheelSize, WheelSize);
            using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(_wheelBitmap.PixelBuffer))
                stream.Write(pixels, 0, pixels.Length);
            _wheelBitmap.Invalidate();
            if (!ReferenceEquals(_wheelImage.Source, _wheelBitmap)) _wheelImage.Source = _wheelBitmap;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Diagnose("wheel: " + exception.Message); }
    }

    private void SelectColorTargetView() => SelectColorTarget(_colorTarget);

    private void SelectColorTarget(string property)
    {
        _colorTarget = property;
        _textTarget.IsChecked = property == "color";
        _fillTarget.IsChecked = property == "backgroundColor";
        var current = _tuneValues.GetValueOrDefault(property, ToHex(_tuneOriginal.GetValueOrDefault(property, "")));
        ShowColor(current);
    }

    /// <summary>Shows a colour in field, preview and marker without applying it.</summary>
    private void ShowColor(string value)
    {
        if (WebdesignColorWheel.ParseHex(value) is not { } color)
        {
            _hexBox.Text = value.Length > 0 && value != "#00000000" ? value : "";
            _colorPreview.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _wheelMarker.Visibility = Visibility.Collapsed;
            return;
        }
        _hexBox.Text = WebdesignColorWheel.Hex(color);
        _colorPreview.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
        var spot = WebdesignColorWheel.Locate(color.R, color.G, color.B, WheelSize);
        Canvas.SetLeft(_wheelMarker, spot.X - _wheelMarker.Width / 2);
        Canvas.SetTop(_wheelMarker, spot.Y - _wheelMarker.Height / 2);
        _wheelMarker.Visibility = Visibility.Visible;
        if (Math.Abs(spot.Brightness - _wheelBrightness) > 0.01 && !_wheelDragging)
        {
            _wheelBrightness = spot.Brightness;
            _syncingBrightness = true;
            try { _brightness.Value = Math.Round(spot.Brightness * 100); }
            finally { _syncingBrightness = false; }
        }
    }

    private void PickFromWheel(Windows.Foundation.Point point)
    {
        var x = Math.Clamp(point.X, 0, WheelSize);
        var y = Math.Clamp(point.Y, 0, WheelSize);
        var radius = WheelSize / 2d;
        var dx = x - radius;
        var dy = y - radius;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius) { x = radius + dx / distance * radius; y = radius + dy / distance * radius; }
        if (WebdesignColorWheel.ColorAt(x, y, WheelSize, _wheelBrightness) is { } color) ApplyColor(WebdesignColorWheel.Hex(color));
    }

    private void ApplyHexInput()
    {
        if (WebdesignColorWheel.ParseHex(_hexBox.Text) is { } color) ApplyColor(WebdesignColorWheel.Hex(color));
        else if (_hexBox.Text.Trim().Length > 0) ShowToast("Bitte eine Farbe wie #1A1A2E eingeben.");
    }

    /// <summary>Live preview while dragging; only the newest colour is sent when the page is busy.</summary>
    private void ApplyColor(string hex)
    {
        PushUndo(coalesce: true);
        ShowColor(hex);
        _pendingColor = hex;
        if (_colorApplying) return;
        _ = PumpColorAsync();
    }

    private async Task PumpColorAsync()
    {
        _colorApplying = true;
        try
        {
            while (_pendingColor is { } next)
            {
                _pendingColor = null;
                await SetTuneValueAsync(_colorTarget, next);
                UpdateTuneReadout();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { ShowToast(exception.Message); }
        finally { _colorApplying = false; }
    }

    internal void PickColorForSmoke(string property, double x, double y)
    {
        SelectColorTarget(property);
        PickFromWheel(new Windows.Foundation.Point(x, y));
    }

    /// <summary>A gesture on the page ended: take over geometry as style differences.</summary>
    private void ApplyTuneGesture(JsonElement message)
    {
        if (_tuneTarget is null) return;
        if (!_restoringUndo) PushUndo();
        int Value(string name) => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : 0;
        var (dx, dy, rotate) = (Value("dx"), Value("dy"), Value("rotate"));
        if (dx != 0 || dy != 0 || rotate != 0)
            _tuneValues["transform"] = string.Create(CultureInfo.InvariantCulture, $"translate({dx}px, {dy}px) rotate({rotate}deg)");
        else _tuneValues.Remove("transform");
        void Size(string property, int now, int before)
        {
            if (now != before) _tuneValues[property] = now.ToString(CultureInfo.InvariantCulture) + "px";
            else _tuneValues.Remove(property);
        }
        Size("width", Value("width"), Value("width0"));
        Size("height", Value("height"), Value("height0"));
        Size("borderRadius", Value("radius"), Value("radius0"));
        UpdateTuneReadout();
    }

    private static string TransformLabel(string transform)
    {
        var numbers = System.Text.RegularExpressions.Regex.Matches(transform, @"-?\d+").Select(static match => match.Value).ToArray();
        if (numbers.Length < 3) return transform;
        var parts = new List<string>();
        if (numbers[0] != "0" || numbers[1] != "0") parts.Add($"Verschoben {numbers[0]}/{numbers[1]} px");
        if (numbers[2] != "0") parts.Add($"Drehung {numbers[2]}°");
        return string.Join(" · ", parts);
    }

    private Button? _tuneApplyButton;

    private void UpdateTuneReadout()
    {
        UpdateTuneTooltip();
        if (_tuneValues.Count == 0) { _tuneReadout.Text = "Noch unverändert – ziehe an den Griffen im Element."; return; }
        _tuneReadout.Text = string.Join(" · ", _tuneValues.Select(pair => pair.Key switch
        {
            "transform" => TransformLabel(pair.Value),
            "width" => "Breite " + pair.Value,
            "height" => "Höhe " + pair.Value,
            "borderRadius" => "Radius " + pair.Value,
            "color" => "Text " + pair.Value,
            "backgroundColor" => "Fläche " + pair.Value,
            _ => pair.Key + " " + pair.Value,
        }));
    }

    private async Task SetTuneValueAsync(string property, string value)
    {
        if (_tuneTarget is not { } target || _core is null || Text(target, "selector") is not { } selector) return;
        if (property is "color" or "backgroundColor" && value.Length > 0 && !value.StartsWith('#') && !value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            value = "#" + value;
        _tuneValues[property] = value;
        var changes = JsonSerializer.Serialize(new Dictionary<string, string> { [property] = value });
        await _core.ExecuteScriptAsync($"window.__missum && window.__missum.preview({JsonSerializer.Serialize(selector)}, {changes})");
    }

    private async Task ResetTunePreviewAsync()
    {
        if (_core is not null) await _core.ExecuteScriptAsync("window.__missum && window.__missum.resetPreview()");
    }

    private async Task CommitTuneAsync()
    {
        if (_tuneTarget is not { } target || _workspace is not { } workspace) return;
        var changes = _tuneValues
            .Select(pair => new WebdesignStyleChange(Css(pair.Key), Normalize(pair.Key, _tuneOriginal.GetValueOrDefault(pair.Key, "")), Normalize(pair.Key, pair.Value)))
            .Where(static change => !string.Equals(change.From, change.To, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (changes.Length == 0) { ShowToast("Es wurde noch nichts verändert."); return; }
        await AddAnnotationAsync(workspace, target, "tune", null, changes);
        _tuneValues.Clear();
        _tuneTarget = null;
        ClearUndo();
        RenderTunePanel();
        await ResetTunePreviewAsync();
        // The handles leave the element, so it (or another) can be chosen again right away.
        if (_core is not null) await _core.ExecuteScriptAsync("window.__missum && window.__missum.tuneEnd()");
    }

    private static string Css(string property) => string.Concat(property.Select(static c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString()));

    private static string Normalize(string property, string value) => property is "color" or "backgroundColor" ? ToHex(value) : value;

    /// <summary>rgb()/rgba() from getComputedStyle as #RRGGBB(AA), so the AI gets familiar values.</summary>
    internal static string ToHex(string value)
    {
        var text = value.Trim();
        if (!text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return text;
        var start = text.IndexOf('(');
        var end = text.IndexOf(')');
        if (start < 0 || end <= start) return text;
        var parts = text[(start + 1)..end].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return text;
        var channels = parts.Take(3).Select(static part => (int)Math.Clamp(double.Parse(part, CultureInfo.InvariantCulture), 0, 255)).ToArray();
        var hex = $"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}";
        if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) && alpha < 1)
            hex += ((int)Math.Round(alpha * 255)).ToString("X2", CultureInfo.InvariantCulture);
        return hex;
    }

    // ---------------------------------------------------------------------------------------
    // Before/after
    // ---------------------------------------------------------------------------------------

    // Undo: every fine-tuning step, colour choice and device switch can be taken back, one per click.
    private readonly Button _undoButton = IconButton("\uE7A7", "Rückgängig (Strg+Z)");
    private readonly List<(Dictionary<string, string> Values, string Device, bool Landscape)> _undo = [];
    private DateTimeOffset _lastCoalescedUndo;
    private bool _restoringUndo;

    private void PushUndo(bool coalesce = false)
    {
        if (_restoringUndo) return;
        var now = DateTimeOffset.UtcNow;
        if (coalesce && now - _lastCoalescedUndo < TimeSpan.FromMilliseconds(900)) { _lastCoalescedUndo = now; return; }
        _lastCoalescedUndo = coalesce ? now : DateTimeOffset.MinValue;
        _undo.Add((new Dictionary<string, string>(_tuneValues, StringComparer.Ordinal), _device, _landscape));
        if (_undo.Count > 60) _undo.RemoveAt(0);
        _undoButton.IsEnabled = true;
    }

    private void ClearUndo()
    {
        _undo.Clear();
        _undoButton.IsEnabled = false;
    }

    internal int UndoDepth => _undo.Count;
    internal bool TuneRowVisible => _tunePanel.Visibility == Visibility.Visible;

    internal async Task UndoAsync()
    {
        if (_undo.Count == 0) return;
        var (values, device, landscape) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _undoButton.IsEnabled = _undo.Count > 0;
        _restoringUndo = true;
        try
        {
            if (device != _device || landscape != _landscape) await SetDeviceAsync(device, landscape);
            _tuneValues.Clear();
            foreach (var (key, value) in values) _tuneValues[key] = value;
            await RestoreTuneAsync();
            UpdateTuneReadout();
            if (_tuneTarget is not null) SelectColorTargetView();
        }
        finally { _restoringUndo = false; }
    }

    /// <summary>Puts the page back to exactly the remembered fine-tuning values.</summary>
    private async Task RestoreTuneAsync()
    {
        if (_core is null || _tuneTarget is not { } target || Text(target, "selector") is not { } selector) return;
        double? Px(string key) => _tuneValues.TryGetValue(key, out var value)
            && double.TryParse(value.Replace("px", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
        var numbers = _tuneValues.TryGetValue("transform", out var transform)
            ? System.Text.RegularExpressions.Regex.Matches(transform, @"-?\d+").Select(static match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToArray()
            : [];
        var state = JsonSerializer.Serialize(new
        {
            dx = numbers.Length > 0 ? numbers[0] : 0, dy = numbers.Length > 1 ? numbers[1] : 0, rot = numbers.Length > 2 ? numbers[2] : 0,
            w = Px("width"), h = Px("height"), radius = Px("borderRadius"),
        });
        var colours = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["color"] = _tuneValues.GetValueOrDefault("color", ""),
            ["backgroundColor"] = _tuneValues.GetValueOrDefault("backgroundColor", ""),
        });
        await _core.ExecuteScriptAsync($"window.__missum && (window.__missum.tuneRestore({state}), window.__missum.preview({JsonSerializer.Serialize(selector)}, {colours}))");
    }

    private void UpdateTuneTooltip()
    {
        if (_tuneApplyButton is { } apply)
            ToolTipService.SetToolTip(apply, _tuneValues.Count == 0
                ? "Ziehe an den Griffen im Element oder wähle eine Farbe; dann übernimmt Missum die Änderung direkt."
                : "Direkt umsetzen: " + _tuneReadout.Text);
    }

    // Smoke hooks: the same paths the panel buttons use.
    internal string? TuneSelector => _tuneTarget is { } target ? Text(target, "selector") : null;
    internal Task PreviewTuneAsync(string property, string value) => SetTuneValueAsync(property, value);
    internal string TuneReadout => _tuneReadout.Text;
    internal Task CommitTuneForSmokeAsync() => CommitTuneAsync();
    internal bool CompareAvailable => _compareToggle.IsEnabled && _beforePng is not null && _afterPng is not null;
    internal void ShowCompareForSmoke() => ShowCompare(true);
    /// <summary>The hosting chip is part of the visible tool pill (smoke check).</summary>
    internal bool HostingChipInToolbar => _serverChip.Parent is not null;
    internal void ShowHostingForSmoke() => ShowHostingFlyout();
    internal void HideCompareForSmoke() => ShowCompare(false);

    private void BuildCompare()
    {
        SetRow(_compare, 1);
        _compare.Background = NativeThemeBrushes.Resource("MissumLayerBrush", 30);
        _compare.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _compare.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var images = new Grid { Margin = new Thickness(24) };
        images.Children.Add(_compareAfter);
        _compareBeforeHost.Children.Add(_compareBefore);
        images.Children.Add(_compareBeforeHost);
        images.SizeChanged += (_, _) => UpdateCompareClip();
        _compare.Children.Add(images);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        footer.Children.Add(new TextBlock { Text = "Vorher", VerticalAlignment = VerticalAlignment.Center });
        _compareSlider.ValueChanged += (_, _) => UpdateCompareClip();
        AutomationProperties.SetName(_compareSlider, "Vergleich zwischen vorher und nachher");
        footer.Children.Add(_compareSlider);
        footer.Children.Add(new TextBlock { Text = "Nachher", VerticalAlignment = VerticalAlignment.Center });
        SetRow(footer, 1);
        _compare.Children.Add(footer);
        Children.Add(_compare);
    }

    private void UpdateCompareClip()
    {
        var width = _compareBeforeHost.ActualWidth;
        var height = _compareBeforeHost.ActualHeight;
        _compareBeforeHost.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width * _compareSlider.Value / 100), Math.Max(0, height)) };
    }

    private void ShowCompare(bool visible)
    {
        _compareToggle.IsChecked = visible && _beforePng is not null && _afterPng is not null;
        _compare.Visibility = _compareToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompareClip();
    }

    /// <summary>Called right before a run starts: remembers what the page looked like.</summary>
    public async Task CaptureBeforeAsync()
    {
        if (_core is null || _state is not { Status: "ready" }) return;
        try
        {
            _beforePng = (await CaptureAsync(null, CancellationToken.None)).Png;
            _beforeUrl = _currentUrl;
            _afterPng = null;
            _compareToggle.IsEnabled = false;
            ShowCompare(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _beforePng = null; }
    }

    /// <summary>Called when a run ended: captures the same page again and enables the comparison.</summary>
    public async Task CaptureAfterAsync()
    {
        if (_core is null || _beforePng is null || _state is not { Status: "ready" }) return;
        try
        {
            await Task.Delay(1200);
            if (_beforeUrl is { } url && !SamePage(url, _currentUrl) && IsServerUrl(url)) await NavigateAsync(url, CancellationToken.None);
            _afterPng = (await CaptureAsync(null, CancellationToken.None)).Png;
            _compareBefore.Source = await BitmapAsync(_beforePng);
            _compareAfter.Source = await BitmapAsync(_afterPng);
            _compareToggle.IsEnabled = true;
            ShowToast("Vorher/Nachher ist bereit.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { _afterPng = null; }
    }

    /// <summary>
    /// Decodes a PNG from a temporary file. Feeding managed arrays to WinRT streams through
    /// AsBuffer crashed the process intermittently in the smoke (access violation in coreclr).
    /// </summary>
    private static async Task<BitmapImage> BitmapAsync(byte[] png)
    {
        var path = Path.Combine(Path.GetTempPath(), "missum-webdesign-" + Guid.NewGuid().ToString("N") + ".png");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }
}

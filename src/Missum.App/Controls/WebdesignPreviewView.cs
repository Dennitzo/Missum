using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Missum.App.Services.Webdesign;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

/// <summary>
/// The "Webseite" tab of the Codex tool Webdesign: a native WebView2 that shows the project's
/// local server, follows file changes, emulates devices, carries the marking overlay and serves
/// as the target of the AI preview tools (webdesign.preview). All WebView2 work happens on
/// the UI thread; tool calls from other threads are marshalled through <see cref="RunOnUiAsync{T}"/>.
/// </summary>
public sealed partial class WebdesignPreviewView : Grid, IWebdesignPreviewSurface
{
    internal static readonly IReadOnlyDictionary<string, (int Width, int Height, bool Mobile, string Label, string Glyph)> Devices =
        new Dictionary<string, (int, int, bool, string, string)>(StringComparer.Ordinal)
        {
            ["smartphone"] = (390, 844, true, "Smartphone", ""),
            ["tablet"] = (820, 1180, true, "Tablet", ""),
            ["laptop"] = (1440, 900, false, "Laptop", ""),
            ["free"] = (0, 0, false, "Frei", ""),
        };
    private const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36";
    private const int MaximumConsoleEntries = 200;

    private readonly WebdesignServeService _serve;
    private readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly List<string> _console = [];
    private readonly Lock _consoleLock = new();
    private readonly Dictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
    private readonly Grid _stage = new();
    private readonly Border _deviceFrame = new();
    private readonly Grid _browserHost = new();
    private readonly StackPanel _notice = new() { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    private readonly TextBlock _noticeTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _noticeText = new() { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _noticeRing = new() { Width = 28, Height = 28, IsActive = false };
    private readonly Button _noticeButton = new() { HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private WebView2? _browser;
    private CoreWebView2? _core;
    private Task? _initialization;
    private string? _workspace;
    private WebdesignServerState? _state;
    private string _device = "free";
    private bool _landscape;
    private string? _currentUrl;
    private (double X, double Y)? _restoreScroll;
    private bool _disposed;
    private int _navigationCount;

    public WebdesignPreviewView(WebdesignServeService serve)
    {
        _serve = serve;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Background = NativeThemeBrushes.Resource("MissumWindowBrush", 24);
        AutomationProperties.SetName(this, "Webseite: Live-Vorschau des Webdesign-Projekts");

        _stage.Background = NativeThemeBrushes.Resource("MissumLayerBrush", 30);
        SetRow(_stage, 1);
        _stage.SizeChanged += (_, _) => { _ = ApplyDeviceAsync(); UpdateToolbarLabels(); };
        _deviceFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
        _deviceFrame.VerticalAlignment = VerticalAlignment.Stretch;
        _deviceFrame.Child = _browserHost;
        _stage.Children.Add(_deviceFrame);
        _noticeTitle.Foreground = NativeThemeBrushes.Text;
        _noticeText.Foreground = NativeThemeBrushes.MutedText;
        _notice.Children.Add(_noticeRing);
        _notice.Children.Add(_noticeTitle);
        _notice.Children.Add(_noticeText);
        _notice.Children.Add(_noticeButton);
        _noticeButton.Click += async (_, _) => { if (_workspace is { } workspace) await RestartServerAsync(workspace); };
        _stage.Children.Add(_notice);
        Children.Add(_stage);
        BuildToolbar();
        BuildSidePanel();
        BuildCompare();
        _serve.StateChanged += OnServerStateChanged;
        _serve.PageChanged += OnPageChanged;
        WebdesignAnnotationStore.Shared.Changed += OnAnnotationsChanged;
        Unloaded += (_, _) => { if (_disposed) Close(); };
        ShowNotice("Webseite wird vorbereitet …", "Der lokale Server des Projekts startet.", busy: true);
    }

    /// <summary>Raised when the page changed while the tab may be hidden; the host shows a dot.</summary>
    public event Action? PageActivity;
    /// <summary>Text for the composer (send wishes, request hosting files); the host switches to the chat.</summary>
    public event Action<string>? ComposeRequested;
    /// <summary>A marking's picture for the composer: file name, PNG bytes.</summary>
    public event Func<string, byte[], Task>? AttachmentRequested;
    /// <summary>The host should show this tab (computer control, badge click from the chat).</summary>
    public event Action? ShowRequested;

    public string? Workspace => _workspace;
    public string? CurrentUrl => _currentUrl;
    public string Device => _device;
    public int NavigationCount => _navigationCount;
    internal WebdesignServerState? ServerState => _state;

    /// <summary>Shows a project: starts or reuses its server and navigates to its start page.</summary>
    public async Task SetWorkspaceAsync(string? workspace)
    {
        if (_disposed) return;
        var normalized = string.IsNullOrWhiteSpace(workspace) ? null : WebdesignServeService.Normalize(workspace);
        if (string.Equals(normalized, _workspace, StringComparison.OrdinalIgnoreCase)) return;
        _workspace = normalized;
        _currentUrl = null;
        _serve.SetActiveWorkspace(normalized);
        ClearConsole();
        RenderAnnotations();
        if (normalized is null)
        {
            ShowNotice("Kein Projekt", "Wähle in der Seitenleiste ein Webdesign-Projekt oder lege eines an.", busy: false);
            return;
        }
        ShowNotice("Server startet …", "Missum sucht einen freien Port und startet den Server des Projekts.", busy: true);
        try
        {
            var state = await _serve.EnsureAsync(normalized);
            if (!string.Equals(_workspace, normalized, StringComparison.OrdinalIgnoreCase)) return;
            await ApplyServerStateAsync(state, navigate: true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ShowNotice("Server nicht gestartet", exception.Message, busy: false, action: "Erneut versuchen");
        }
    }

    /// <summary>
    /// The Webdesign tool is off for the open session (or another mode is shown). The server idles out
    /// later; the next <see cref="SetWorkspaceAsync"/> starts it again and reloads the start page.
    /// </summary>
    public Task DeactivateAsync()
    {
        _serve.SetActiveWorkspace(null);
        _workspace = null;
        return Task.CompletedTask;
    }

    private async Task RestartServerAsync(string workspace)
    {
        ShowNotice("Server startet neu …", "", busy: true);
        try { await ApplyServerStateAsync(await _serve.RestartAsync(workspace), navigate: true); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { ShowNotice("Server nicht gestartet", exception.Message, busy: false, action: "Erneut versuchen"); }
    }

    private void OnServerStateChanged(WebdesignServerState state) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_disposed || !string.Equals(state.Workspace, _workspace, StringComparison.OrdinalIgnoreCase)) return;
        _ = ApplyServerStateAsync(state, navigate: _state?.Port != state.Port || _state?.Status != "ready");
    });

    private async Task ApplyServerStateAsync(WebdesignServerState state, bool navigate)
    {
        _state = state;
        UpdateServerChip();
        switch (state.Status)
        {
            case "ready":
                HideNotice();
                await EnsureBrowserAsync();
                if (navigate || _currentUrl is null || !IsServerUrl(_currentUrl))
                    await NavigateCoreAsync(StartUrl(state));
                break;
            case "starting":
            case "restarting":
                ShowNotice("Server startet …", state.Message ?? "", busy: true);
                break;
            case "failed":
                ShowNotice("Server nicht gestartet", state.Message ?? "Unbekannter Fehler.", busy: false, action: "Erneut versuchen");
                break;
            default:
                ShowNotice("Server gestoppt", "Der Server dieses Projekts läuft nicht.", busy: false, action: "Server starten");
                break;
        }
    }

    private static string StartUrl(WebdesignServerState state)
    {
        var start = WebdesignServeService.ServedPath(".", state.StartPage) ?? "";
        return state.LocalUrl.TrimEnd('/') + "/" + start;
    }

    private bool IsServerUrl(string? url)
    {
        if (_state is not { Status: "ready" } state || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Port != state.Port || uri.Scheme != Uri.UriSchemeHttp) return false;
        return uri.Host is "127.0.0.1" or "localhost" or "[::1]"
            || state.LanUrl is { } lan && Uri.TryCreate(lan, UriKind.Absolute, out var lanUri) && lanUri.Host == uri.Host;
    }

    private void ShowNotice(string title, string text, bool busy, string? action = null)
    {
        _noticeTitle.Text = title;
        _noticeText.Text = text;
        _noticeText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        _noticeRing.IsActive = busy;
        _noticeRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _noticeButton.Content = action;
        _noticeButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        _notice.Visibility = Visibility.Visible;
        _deviceFrame.Visibility = Visibility.Collapsed;
    }

    private void HideNotice()
    {
        _notice.Visibility = Visibility.Collapsed;
        _deviceFrame.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------------------------------
    // Browser
    // ---------------------------------------------------------------------------------------

    private Task EnsureBrowserAsync() => _initialization ??= InitializeBrowserAsync();

    private async Task InitializeBrowserAsync()
    {
        var profile = Path.Combine(App.Current.DataDirectory, "WebView2", "Webdesign");
        Directory.CreateDirectory(profile);
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
        var browser = new WebView2();
        AutomationProperties.SetName(browser, "Webseite");
        _browser = browser;
        _browserHost.Children.Insert(0, browser);
        await browser.EnsureCoreWebView2Async(environment);
        var core = browser.CoreWebView2 ?? throw new InvalidOperationException("Der Webseiten-Browser konnte nicht initialisiert werden.");
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        // A managed exception escaping a WebView2 callback ends the whole process (seen in the
        // smoke as an access violation in coreclr). Every handler therefore runs behind Guard.
        core.NavigationStarting += (sender, args) => Guard("navigation.starting", () => OnNavigationStarting(sender, args));
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += (_, _) => Guard("source", () => { _currentUrl = core.Source; UpdateAddress(); });
        core.HistoryChanged += (_, _) => Guard("history", UpdateNavigationButtons);
        core.NewWindowRequested += (sender, args) => Guard("popup", () => OnNewWindowRequested(sender, args));
        core.PermissionRequested += (_, args) => Guard("permission", () => args.State = CoreWebView2PermissionState.Deny);
        core.WebMessageReceived += OnWebMessageReceived;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(OverlayScript().Replace("__MISSUM_NONCE__", _nonce, StringComparison.Ordinal));
        await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Log.enable", "{}");
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        Listen(core, "Runtime.consoleAPICalled", OnConsole);
        Listen(core, "Runtime.exceptionThrown", OnException);
        Listen(core, "Log.entryAdded", OnLogEntry);
        Listen(core, "Network.requestWillBeSent", OnRequest);
        Listen(core, "Network.responseReceived", OnResponse);
        Listen(core, "Network.loadingFailed", OnLoadingFailed);
        _core = core;
        await ApplyDeviceAsync();
    }

    // The receivers must stay referenced: once the garbage collector reclaims a receiver's
    // wrapper, WebView2 dispatches the next event into a released object and the process dies
    // with an access violation (reproduced in the smoke at random steps).
    private readonly List<CoreWebView2DevToolsProtocolEventReceiver> _devToolsReceivers = [];

    private void Listen(CoreWebView2 core, string name, Action<string> handler)
    {
        var receiver = core.GetDevToolsProtocolEventReceiver(name);
        _devToolsReceivers.Add(receiver);
        receiver.DevToolsProtocolEventReceived += (_, args) => Guard(name, () => handler(args.ParameterObjectAsJson));
    }

    private void Guard(string name, Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Diagnose($"handler {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string? _overlayScript;
    private static string OverlayScript()
    {
        if (_overlayScript is { } cached) return cached;
        using var stream = typeof(WebdesignPreviewView).Assembly.GetManifestResourceStream("Missum.Webdesign.overlay.js")
            ?? throw new InvalidOperationException("Das Webdesign-Overlay fehlt in der Anwendung.");
        using var reader = new StreamReader(stream);
        return _overlayScript = reader.ReadToEnd();
    }

    private async Task NavigateCoreAsync(string url)
    {
        await EnsureBrowserAsync();
        if (_core is null) return;
        ClearConsole();
        _core.Navigate(url);
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (args.Uri is "about:blank" || args.Uri.StartsWith("data:", StringComparison.Ordinal) || IsServerUrl(args.Uri))
        {
            ClearConsole();
            return;
        }
        // Links to other sites open in the user's browser; the tab stays on the project.
        args.Cancel = true;
        OpenExternally(args.Uri);
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (IsServerUrl(args.Uri)) sender.Navigate(args.Uri);
        else OpenExternally(args.Uri);
    }

    private static void OpenExternally(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            _ = Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            _navigationCount++;
            _currentUrl = sender.Source;
            UpdateAddress();
            UpdateNavigationButtons();
            if (!args.IsSuccess)
                AddConsole($"[Navigation] {args.WebErrorStatus} beim Laden von {sender.Source}");
            if (_restoreScroll is { } scroll)
            {
                _restoreScroll = null;
                await sender.ExecuteScriptAsync(string.Create(CultureInfo.InvariantCulture, $"window.scrollTo({scroll.X},{scroll.Y})"));
            }
            await PushOverlayStateAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Diagnose("navigation.completed: " + exception.Message); }
    }

    /// <summary>Re-sends mode and annotations, since every page load starts a fresh overlay.</summary>
    private async Task PushOverlayStateAsync()
    {
        if (_core is null) return;
        await _core.ExecuteScriptAsync($"window.__missum && window.__missum.setMode({JsonSerializer.Serialize(_overlayMode)})");
        await RenderAnnotationsAsync();
    }

    /// <summary>A file of the shown project changed: show the page or refresh what is shown.</summary>
    private void OnPageChanged(WebdesignPageChange change) => DispatcherQueue.TryEnqueue(async () =>
    {
        if (_disposed || _core is null || !string.Equals(change.Workspace, _workspace, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            PageActivity?.Invoke();
            if (_state is { Kind: "command" } && change.Kind is "css" or "script") return; // the dev server's own hot reload
            if (change.Kind == "html" && change.Url is { } url)
            {
                if (SamePage(url, _currentUrl)) await ReloadKeepingScrollAsync();
                else await NavigateCoreAsync(url);
                return;
            }
            if (change.Kind == "css")
            {
                await _core.ExecuteScriptAsync("window.__missum && window.__missum.reloadCss()");
                return;
            }
            await ReloadKeepingScrollAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Diagnose("page.changed: " + exception.Message); }
    });

    private static bool SamePage(string a, string? b) =>
        b is not null && Uri.TryCreate(a, UriKind.Absolute, out var left) && Uri.TryCreate(b, UriKind.Absolute, out var right)
        && string.Equals(left.AbsolutePath.TrimEnd('/'), right.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private async Task ReloadKeepingScrollAsync()
    {
        if (_core is null) return;
        try
        {
            var state = JsonDocument.Parse(await _core.ExecuteScriptAsync("JSON.stringify([window.scrollX, window.scrollY])"));
            var text = state.RootElement.GetString();
            if (text is not null && JsonSerializer.Deserialize<double[]>(text) is [var x, var y]) _restoreScroll = (x, y);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { }
        ClearConsole();
        _core.Reload();
    }

    // ---------------------------------------------------------------------------------------
    // Devices
    // ---------------------------------------------------------------------------------------

    internal async Task SetDeviceAsync(string device, bool? landscape = null)
    {
        if (!Devices.ContainsKey(device)) return;
        _device = device;
        if (landscape is { } value) _landscape = value;
        UpdateDeviceButtons();
        await ApplyDeviceAsync();
    }

    internal (int Width, int Height) DeviceSize()
    {
        var (width, height, _, _, _) = Devices[_device];
        return _landscape ? (height, width) : (width, height);
    }

    private async Task ApplyDeviceAsync()
    {
        if (_disposed) return;
        var available = (Width: Math.Max(200, _stage.ActualWidth - 48), Height: Math.Max(200, _stage.ActualHeight - 48));
        var core = _core;
        if (_device == "free")
        {
            _deviceFrame.Width = double.NaN;
            _deviceFrame.Height = double.NaN;
            _deviceFrame.Margin = new Thickness(0);
            _deviceFrame.Padding = new Thickness(0);
            _deviceFrame.CornerRadius = new CornerRadius(0);
            _deviceFrame.BorderThickness = new Thickness(0);
            _deviceFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            _deviceFrame.VerticalAlignment = VerticalAlignment.Stretch;
            if (core is null) return;
            await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", "{\"enabled\":false}");
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", "{\"userAgent\":\"\"}");
            return;
        }
        var (width, height) = DeviceSize();
        var mobile = Devices[_device].Mobile;
        var scale = Math.Min(1, Math.Min(available.Width / width, available.Height / height));
        var bezel = mobile ? 10 : 6;
        _deviceFrame.HorizontalAlignment = HorizontalAlignment.Center;
        _deviceFrame.VerticalAlignment = VerticalAlignment.Center;
        _deviceFrame.Padding = new Thickness(bezel);
        _deviceFrame.CornerRadius = new CornerRadius(_device == "smartphone" ? 28 : _device == "tablet" ? 18 : 8);
        _deviceFrame.BorderThickness = new Thickness(1);
        _deviceFrame.BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 70);
        _deviceFrame.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 18, 16, 28));
        _deviceFrame.Width = Math.Round(width * scale) + bezel * 2 + 2;
        _deviceFrame.Height = Math.Round(height * scale) + bezel * 2 + 2;
        if (core is null) return;
        var metrics = JsonSerializer.Serialize(new
        {
            width, height, deviceScaleFactor = 0, mobile, scale = Math.Round(scale, 4),
            screenWidth = width, screenHeight = height,
            screenOrientation = new { type = width > height ? "landscapePrimary" : "portraitPrimary", angle = mobile && _landscape ? 90 : 0 },
        });
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride", metrics);
        await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled",
            mobile ? "{\"enabled\":true,\"maxTouchPoints\":5}" : "{\"enabled\":false}");
        await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
            JsonSerializer.Serialize(new { userAgent = mobile ? MobileUserAgent : "" }));
    }

    // ---------------------------------------------------------------------------------------
    // Console and network problems (for webdesign.preview console)
    // ---------------------------------------------------------------------------------------

    private void ClearConsole()
    {
        lock (_consoleLock) { _console.Clear(); _requestUrls.Clear(); }
    }

    private void AddConsole(string line)
    {
        if (line.Length > 700) line = line[..700] + " …";
        lock (_consoleLock)
        {
            _console.Add(line);
            if (_console.Count > MaximumConsoleEntries) _console.RemoveAt(0);
        }
    }

    private void OnConsole(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var value) ? value.GetString() : "log";
        if (type is not ("error" or "warning" or "assert")) return;
        var text = root.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? string.Join(" ", args.EnumerateArray().Select(static arg =>
                arg.TryGetProperty("value", out var v) ? v.ToString() : arg.TryGetProperty("description", out var d) ? d.GetString() : ""))
            : "";
        AddConsole($"[console.{type}] {text}");
    }

    private void OnException(string json)
    {
        using var document = JsonDocument.Parse(json);
        var details = document.RootElement.GetProperty("exceptionDetails");
        var text = details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
            ? description.GetString() : details.TryGetProperty("text", out var t) ? t.GetString() : "Unbekannter Fehler";
        var line = details.TryGetProperty("lineNumber", out var number) ? number.GetInt32() + 1 : 0;
        var url = details.TryGetProperty("url", out var source) ? source.GetString() : "";
        AddConsole($"[Fehler] {text} ({url}:{line})");
    }

    private void OnLogEntry(string json)
    {
        using var document = JsonDocument.Parse(json);
        var entry = document.RootElement.GetProperty("entry");
        var level = entry.GetProperty("level").GetString();
        if (level is not ("error" or "warning")) return;
        AddConsole($"[{entry.GetProperty("source").GetString()}.{level}] {entry.GetProperty("text").GetString()}"
            + (entry.TryGetProperty("url", out var url) ? $" ({url.GetString()})" : ""));
    }

    private void OnRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        lock (_consoleLock)
        {
            if (_requestUrls.Count > 2000) _requestUrls.Clear();
            _requestUrls[root.GetProperty("requestId").GetString()!] = root.GetProperty("request").GetProperty("url").GetString()!;
        }
    }

    private void OnResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var response = document.RootElement.GetProperty("response");
        var status = response.GetProperty("status").GetInt32();
        var url = response.GetProperty("url").GetString() ?? "";
        if (status >= 400 && !url.Contains("/__missum/", StringComparison.Ordinal)) AddConsole($"[HTTP {status}] {url}");
    }

    private void OnLoadingFailed(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("canceled", out var canceled) && canceled.GetBoolean()) return;
        string? url;
        lock (_consoleLock) _requestUrls.TryGetValue(root.GetProperty("requestId").GetString()!, out url);
        if (url?.Contains("/__missum/", StringComparison.Ordinal) == true) return;
        AddConsole($"[Netzwerk] {root.GetProperty("errorText").GetString()} {url}");
    }

    // ---------------------------------------------------------------------------------------
    // IWebdesignPreviewSurface (called by tools from any thread)
    // ---------------------------------------------------------------------------------------

    private Task<T> RunOnUiAsync<T>(Func<Task<T>> work)
    {
        if (DispatcherQueue.HasThreadAccess) return work();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await work()); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }))
            completion.TrySetException(new InvalidOperationException("Der Tab Webseite ist nicht mehr verfügbar."));
        return completion.Task;
    }

    private CoreWebView2 RequireCore() => !_disposed && _core is { } core && _state is { Status: "ready" }
        ? core : throw new InvalidOperationException("Der Tab Webseite zeigt noch keine Seite; prüfe webdesign.serve status.");

    public Task ShowAsync(CancellationToken cancellationToken) => RunOnUiAsync(async () =>
    {
        ShowRequested?.Invoke();
        await Task.Delay(150, cancellationToken);
        return true;
    });

    public Task NavigateAsync(string url, CancellationToken cancellationToken) => RunOnUiAsync(async () =>
    {
        if (!IsServerUrl(url)) throw new InvalidOperationException("Der Tab zeigt nur Seiten des Projektservers.");
        var before = _navigationCount;
        await NavigateCoreAsync(url);
        for (var waited = 0; waited < 100 && _navigationCount == before; waited++) await Task.Delay(100, cancellationToken);
        return true;
    });

    // ---------------------------------------------------------------------------------------
    // Visibility: the host shows the tab; a capture renders a hidden tab invisibly
    // ---------------------------------------------------------------------------------------

    private bool _hostVisible;
    private int _offscreenRenders;

    /// <summary>The host page shows (true) or hides (false) the "Webseite" tab.</summary>
    internal void SetHostVisible(bool visible)
    {
        _hostVisible = visible;
        if (visible)
        {
            Opacity = 1;
            IsHitTestVisible = true;
            Visibility = Visibility.Visible;
            return;
        }
        if (_offscreenRenders > 0)
        {
            Opacity = 0;
            IsHitTestVisible = false;
            return;
        }
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// A collapsed WebView2 produces no frames, so a screenshot of the page (webdesign.preview capture,
    /// before/after) would see nothing while the user reads the chat. The tab is laid out and rendered
    /// for the capture, fully transparent and without hit testing, and hidden again afterwards.
    /// </summary>
    private async Task<T> WithRenderedPageAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        if (_hostVisible) return await work();
        _offscreenRenders++;
        Opacity = 0;
        IsHitTestVisible = false;
        Visibility = Visibility.Visible;
        try
        {
            UpdateLayout();
            await Task.Delay(350, cancellationToken);
            return await work();
        }
        finally
        {
            if (--_offscreenRenders == 0)
            {
                if (!_hostVisible) Visibility = Visibility.Collapsed;
                Opacity = 1;
                IsHitTestVisible = true;
            }
        }
    }

    /// <summary>The browser runs and shows the project's ready server (tools may capture and inspect).</summary>
    public bool IsReady => !_disposed && _core is not null && _state is { Status: "ready" };

    public Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken) => RunOnUiAsync(() =>
        WithRenderedPageAsync(() => CaptureRenderedAsync(device, cancellationToken), cancellationToken));

    private async Task<WebdesignCapture> CaptureRenderedAsync(string? device, CancellationToken cancellationToken)
    {
        var core = RequireCore();
        var previous = (_device, _landscape);
        if (device is not null && device != _device && Devices.ContainsKey(device))
        {
            await SetDeviceAsync(device, landscape: false);
            await Task.Delay(400, cancellationToken);
        }
        try
        {
            var state = await EvaluateAsync(core, "window.__missum ? window.__missum.state() : {url:location.href,scrollX:scrollX,scrollY:scrollY,width:innerWidth,height:innerHeight}");
            var elements = await EvaluateAsync(core, "window.__missum ? window.__missum.elements() : []");
            var shot = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"fromSurface\":true}");
            using var document = JsonDocument.Parse(shot);
            var png = Convert.FromBase64String(document.RootElement.GetProperty("data").GetString()!);
            var (pixelWidth, pixelHeight) = PngSize(png);
            return new WebdesignCapture(png, pixelWidth, pixelHeight,
                state.GetProperty("width").GetDouble(), state.GetProperty("height").GetDouble(),
                state.GetProperty("scrollX").GetDouble(), state.GetProperty("scrollY").GetDouble(),
                state.GetProperty("url").GetString() ?? _currentUrl ?? "", _device, elements.Clone(),
                state.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null);
        }
        finally
        {
            if (previous._device != _device || previous._landscape != _landscape) await SetDeviceAsync(previous._device, previous._landscape);
        }
    }

    public Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken) => RunOnUiAsync(() =>
    {
        lock (_consoleLock) return Task.FromResult<IReadOnlyList<string>>([.. _console]);
    });

    public Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken) => RunOnUiAsync(async () =>
        (await EvaluateAsync(RequireCore(), $"window.__missum ? window.__missum.inspect({JsonSerializer.Serialize(selector)}) : null")).Clone());

    public Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken) => RunOnUiAsync(async () =>
        (await EvaluateAsync(RequireCore(), string.Create(CultureInfo.InvariantCulture,
            $"window.__missum ? window.__missum.elementAt({cssX},{cssY}) : null"))).Clone());

    public Task GoBackAsync(CancellationToken cancellationToken) => RunOnUiAsync(async () =>
    {
        var core = RequireCore();
        if (!core.CanGoBack) throw new InvalidOperationException("Im Verlauf des Tabs Webseite gibt es keine vorherige Seite.");
        var before = _navigationCount;
        core.GoBack();
        for (var waited = 0; waited < 50 && _navigationCount == before; waited++) await Task.Delay(100, cancellationToken);
        return true;
    });

    public Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken) => RunOnUiAsync(async () =>
    {
        var state = await EvaluateAsync(RequireCore(), "({x: scrollX, y: scrollY})");
        return (state.GetProperty("x").GetDouble(), state.GetProperty("y").GetDouble());
    });

    /// <summary>The element that has keyboard focus, or null (computer control checks password fields).</summary>
    internal Task<JsonElement> ActiveElementAsync() => RunOnUiAsync(async () =>
        (await EvaluateAsync(RequireCore(), "window.__missum ? window.__missum.active() : null")).Clone());

    public Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken) => RunOnUiAsync(async () =>
    {
        var core = RequireCore();
        async Task Mouse(string type, double x, double y, int clickCount = 0, string button = "left", double deltaY = 0, double deltaX = 0) =>
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
            {
                type, x = Math.Round(x, 2), y = Math.Round(y, 2), button = type is "mouseMoved" or "mouseWheel" ? "none" : button,
                clickCount, deltaX, deltaY,
            }));
        if (input.Kind is "move" or "click" or "double_click" or "drag" or "scroll")
            await core.ExecuteScriptAsync(string.Create(CultureInfo.InvariantCulture, $"window.__missum && window.__missum.pointer({input.X},{input.Y})"));
        switch (input.Kind)
        {
            case "move":
                await Mouse("mouseMoved", input.X, input.Y);
                break;
            case "click":
            case "double_click":
                await Mouse("mouseMoved", input.X, input.Y);
                for (var count = 1; count <= (input.Kind == "double_click" ? 2 : 1); count++)
                {
                    await Mouse("mousePressed", input.X, input.Y, count);
                    await Mouse("mouseReleased", input.X, input.Y, count);
                }
                break;
            case "drag":
                await Mouse("mouseMoved", input.X, input.Y);
                await Mouse("mousePressed", input.X, input.Y, 1);
                for (var step = 1; step <= 12; step++)
                {
                    var t = step / 12d;
                    await Mouse("mouseMoved", input.X + (input.EndX - input.X) * t, input.Y + (input.EndY - input.Y) * t);
                    await Task.Delay(16, cancellationToken);
                }
                await Mouse("mouseReleased", input.EndX, input.EndY, 1);
                break;
            case "scroll":
                await Mouse("mouseWheel", input.X, input.Y, deltaY: input.ScrollY, deltaX: input.ScrollX);
                break;
            case "type":
                await core.CallDevToolsProtocolMethodAsync("Input.insertText", JsonSerializer.Serialize(new { text = input.Text ?? "" }));
                break;
            case "keypress":
                await DispatchKeyAsync(core, input.Key ?? "");
                break;
            case "wait":
                await Task.Delay(1000, cancellationToken);
                break;
            default:
                throw new InvalidOperationException("Diese Aktion gibt es im Tab Webseite nicht.");
        }
        await Task.Delay(120, cancellationToken);
        return true;
    });

    private static async Task DispatchKeyAsync(CoreWebView2 core, string combination)
    {
        var parts = combination.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("Leere Tastenkombination.");
        var modifiers = 0;
        foreach (var part in parts[..^1])
            modifiers |= part.ToLowerInvariant() switch
            {
                "alt" => 1, "ctrl" or "control" or "strg" => 2, "meta" or "win" or "cmd" => 4, "shift" => 8,
                _ => throw new ArgumentException($"Unbekannte Zusatztaste: {part}"),
            };
        var (key, code, virtualKey, text) = KeyInfo(parts[^1]);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = text is not null && modifiers is 0 or 8 ? "keyDown" : "rawKeyDown", modifiers, key, code,
            windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey,
            text = modifiers is 0 or 8 ? text : null, unmodifiedText = text,
        }));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
        {
            type = "keyUp", modifiers, key, code, windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey,
        }));
    }

    internal static (string Key, string Code, int VirtualKey, string? Text) KeyInfo(string name)
    {
        var lower = name.ToLowerInvariant();
        switch (lower)
        {
            case "enter" or "return": return ("Enter", "Enter", 13, "\r");
            case "tab": return ("Tab", "Tab", 9, null);
            case "escape" or "esc": return ("Escape", "Escape", 27, null);
            case "backspace": return ("Backspace", "Backspace", 8, null);
            case "delete" or "del": return ("Delete", "Delete", 46, null);
            case "space": return (" ", "Space", 32, " ");
            case "home": return ("Home", "Home", 36, null);
            case "end": return ("End", "End", 35, null);
            case "pageup": return ("PageUp", "PageUp", 33, null);
            case "pagedown": return ("PageDown", "PageDown", 34, null);
            case "up" or "arrowup": return ("ArrowUp", "ArrowUp", 38, null);
            case "down" or "arrowdown": return ("ArrowDown", "ArrowDown", 40, null);
            case "left" or "arrowleft": return ("ArrowLeft", "ArrowLeft", 37, null);
            case "right" or "arrowright": return ("ArrowRight", "ArrowRight", 39, null);
        }
        if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower[1..], out var function) && function is >= 1 and <= 12)
            return ("F" + function, "F" + function, 111 + function, null);
        if (name.Length == 1)
        {
            var character = name[0];
            if (char.IsAsciiLetter(character))
                return (name, "Key" + char.ToUpperInvariant(character), char.ToUpperInvariant(character), name);
            if (char.IsAsciiDigit(character)) return (name, "Digit" + character, character, name);
            return (name, "", 0, name);
        }
        throw new ArgumentException($"Unbekannte Taste: {name}");
    }

    private static async Task<JsonElement> EvaluateAsync(CoreWebView2 core, string expression)
    {
        var json = await core.ExecuteScriptAsync("(() => { try { return " + expression + "; } catch (e) { return { error: String(e && e.message || e) }; } })()");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json);
        return document.RootElement.Clone();
    }

    internal static (int Width, int Height) PngSize(byte[] png) =>
        png.Length >= 24 ? ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]) : (0, 0);

    /// <summary>A section of the current viewport as PNG, for marking pictures in the composer.</summary>
    private async Task<byte[]?> CropViewportAsync(double x, double y, double width, double height)
    {
        if (_core is null) return null;
        var shot = await _core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\",\"fromSurface\":true}");
        using var document = JsonDocument.Parse(shot);
        var png = Convert.FromBase64String(document.RootElement.GetProperty("data").GetString()!);
        var state = await EvaluateAsync(_core, "({width: innerWidth, height: innerHeight})");
        var (pixelWidth, pixelHeight) = PngSize(png);
        var ratio = pixelWidth / Math.Max(1, state.GetProperty("width").GetDouble());
        const double margin = 24;
        var left = (uint)Math.Clamp((x - margin) * ratio, 0, pixelWidth - 1);
        var top = (uint)Math.Clamp((y - margin) * ratio, 0, pixelHeight - 1);
        var right = (uint)Math.Clamp((x + width + margin) * ratio, left + 1, pixelWidth);
        var bottom = (uint)Math.Clamp((y + height + margin) * ratio, top + 1, pixelHeight);
        // Managed stream adapters only: no managed array is handed to WinRT as a raw buffer.
        using var source = new MemoryStream(png, writable: false);
        using var input = source.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var target = new MemoryStream();
        using (var output = target.AsRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            encoder.BitmapTransform.Bounds = new BitmapBounds { X = left, Y = top, Width = right - left, Height = bottom - top };
            await encoder.FlushAsync();
        }
        return target.ToArray();
    }

    public void Close()
    {
        if (_disposed && _core is null) return;
        _disposed = true;
        _serve.StateChanged -= OnServerStateChanged;
        _serve.PageChanged -= OnPageChanged;
        WebdesignAnnotationStore.Shared.Changed -= OnAnnotationsChanged;
        WebdesignPreviewRegistry.Unregister(this);
        try { _browser?.Close(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        _browser = null;
        _core = null;
    }
}

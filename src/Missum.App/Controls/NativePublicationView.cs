using System.Globalization;
using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Missum.App.Controls;

/// <summary>The browser's real PDF viewer in a retained, session-owned native surface.</summary>
internal sealed class NativePublicationView : Grid, IDisposable, IAsyncDisposable
{
    private WebView2? _browser;
    private CoreWebView2? _core;
    private CoreWebView2Frame? _frame;
    private readonly TextBlock _notice = new()
    {
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed, Margin = new Thickness(12),
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly Queue<BrowserProbe> _probes = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _loadCompletion;
    private Task _loading = Task.CompletedTask;
    private string? _requestedPath, _requestedStamp, _allowedPath, _expectedUri, _pendingError;
    private string _logicalDocumentKey = "publication";
    private string? _committedDocumentKey;
    private CancellationToken _loadToken;
    private long _version, _loadedVersion, _navigationVersion;
    private ulong _navigationId;
    private bool _active, _disposed, _working, _queued, _dirty, _loadFailed, _activationBlocked, _documentReady;
    private PdfViewState _viewState = new(1, null, null, true);

    internal uint PageCount { get; private set; }
    internal bool IsInitialized => _core is not null;
    internal bool HasCompletedNavigation => _documentReady && _loadedVersion == _version;
    internal long NavigationCount { get; private set; }
    internal long BrowserFaultCount { get; private set; }
    internal string? PdfSource { get; private set; }
    internal double BrowserWidth => _browser?.ActualWidth ?? 0;
    internal double BrowserHeight => _browser?.ActualHeight ?? 0;

    internal NativePublicationView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Children.Add(_notice);
        Loaded += (_, _) => GuardCallback("host.loaded", RequestWork);
        Unloaded += (_, _) => GuardCallback("host.unloaded", RequestWork);
        AutomationProperties.SetName(this, "Wissenschaftliche Publikation im PDF-Browser");
    }

    internal void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active && _loadFailed && !_activationBlocked && !_loadToken.IsCancellationRequested) BeginLoad();
        RequestWork();
    }

    internal Task LoadAsync(string path, string? logicalDocumentKey = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Publikationsansicht unterstützt ausschließlich PDF-Dateien.");
        var stamp = SourceStamp(fullPath);
        var key = string.IsNullOrWhiteSpace(logicalDocumentKey) ? "publication" : logicalDocumentKey;
        var documentChanged = !string.Equals(_logicalDocumentKey, key, StringComparison.Ordinal);
        if (documentChanged) { _logicalDocumentKey = key; _viewState = new(1, null, null, true); }
        _loadToken = cancellationToken;
        if (!documentChanged && _requestedPath == fullPath && _requestedStamp == stamp && (!_loadFailed || _activationBlocked)) return _loading;
        _requestedPath = fullPath;
        _requestedStamp = stamp;
        BeginLoad();
        RequestWork();
        return _loading;
    }

    private static string SourceStamp(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Die Publikations-PDF wurde nicht gefunden.", path);
        return file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + file.Length.ToString(CultureInfo.InvariantCulture);
    }

    private void BeginLoad()
    {
        _loadCompletion?.TrySetCanceled();
        _loadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = _loadCompletion.Task;
        _version++;
        _loadFailed = false;
        _pendingError = null;
    }

    private bool Current(long version) => !_disposed && _active && IsLoaded && XamlRoot is not null && version == _version;

    private void RequestWork()
    {
        _dirty = true;
        if (_working || _queued) return;
        _queued = true;
        if (!DispatcherQueue.TryEnqueue(() => { _queued = false; _ = ProcessWorkAsync(); }))
        {
            _queued = false;
            _loadCompletion?.TrySetCanceled();
        }
    }

    private async Task ProcessWorkAsync()
    {
        if (_working) return;
        _working = true;
        try
        {
            while (_dirty)
            {
                _dirty = false;
                if (_disposed) { CloseBrowser(); return; }
                var version = _version;
                var token = _loadToken;
                try
                {
                    if (_pendingError is { } error) ShowError(error);
                    if (Current(version) && !_loadFailed && !_activationBlocked && _loadedVersion != version
                        && _requestedPath is { } path && _requestedStamp is { } stamp)
                        await LoadCurrentAsync(path, stamp, version, token);
                    if (_disposed) { CloseBrowser(); return; }
                    // The retained browser preserves its real PDF sidebar, toolbar,
                    // selection and scroll state. Hiding it does not suspend print or
                    // download jobs or remove its initialized native visual parent.
                    if (_browser is { } browser)
                        browser.Visibility = _active && IsLoaded && XamlRoot is not null ? Visibility.Visible : Visibility.Collapsed;
                    while (_probes.TryDequeue(out var probe))
                    {
                        try
                        {
                            var core = _core ?? throw new InvalidOperationException("Die PDF-Browseransicht ist noch nicht geladen.");
                            if (probe.CapturePath is { } capture)
                            {
                                await using var output = new FileStream(capture, FileMode.Create, FileAccess.Write, FileShare.Read);
                                using var random = output.AsRandomAccessStream();
                                await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, random);
                                probe.Completion.TrySetResult(capture);
                            }
                            else
                            {
                                var script = probe.Script ?? "";
                                var result = probe.Frame
                                    ? await (_frame ?? throw new InvalidOperationException("Der PDF-Browserframe ist noch nicht geladen.")).ExecuteScriptAsync(script)
                                    : await core.ExecuteScriptAsync(script);
                                probe.Completion.TrySetResult(result);
                            }
                        }
                        catch (Exception exception) when (Recoverable(exception)) { probe.Completion.TrySetException(exception); }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || _disposed)
                {
                    if (version == _version) _loadCompletion?.TrySetCanceled();
                    else _dirty = true;
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (version == _version)
                    {
                        _loadFailed = true;
                        _pendingError = "Das PDF konnte nicht angezeigt werden: " + exception.Message;
                        _loadCompletion?.TrySetResult();
                        RecordFailure("load", exception);
                        ShowError(_pendingError);
                    }
                    else _dirty = true;
                }
            }
        }
        finally
        {
            _working = false;
            if (_disposed) CloseBrowser();
            else if (_dirty) RequestWork();
        }
    }

    private async Task LoadCurrentAsync(string path, string stamp, long version, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        var metadata = await PdfDocument.LoadFromFileAsync(file).AsTask(token);
        if (!Current(version)) return;
        if (SourceStamp(path) != stamp)
        {
            _requestedStamp = SourceStamp(path);
            BeginLoad();
            _dirty = true;
            return;
        }
        var documentKey = _logicalDocumentKey;
        if (_documentReady && _committedDocumentKey == documentKey) await CaptureViewStateAsync(documentKey, version);
        if (!Current(version)) return;
        if (_core is null)
        {
            var profile = Path.Combine(App.Current.DataDirectory, "WebView2", "Publications");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            if (!Current(version)) return;
            try
            {
                var browser = new WebView2();
                _browser = browser;
                AutomationProperties.SetName(browser, "Publikation: PDF mit Seitenübersicht, Zoom, Suche, Speichern und Drucken");
                Children.Insert(0, browser);
                await browser.EnsureCoreWebView2Async(environment);
                if (_disposed) return;
                _core = browser.CoreWebView2 ?? throw new InvalidOperationException("Der PDF-Browser konnte nicht initialisiert werden.");
                ConfigureBrowser(_core);
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                _activationBlocked = true;
                CloseBrowser();
                throw;
            }
        }
        if (!Current(version)) return;
        PageCount = metadata.PageCount;
        _allowedPath = path;
        // A source revision also distinguishes overwritten PDFs at the same path
        // from an ordinary in-document fragment/page change in the browser cache.
        var page = Math.Clamp(_viewState.Page, 1, Math.Max(1, (int)PageCount));
        var zoom = _viewState.Zoom is { } value ? "zoom=" + value.ToString(CultureInfo.InvariantCulture) : "view=FitH";
        var mode = _viewState.SidebarOpen ? "thumbs" : "none";
        _expectedUri = new Uri(path).AbsoluteUri + "?missum_pdf_revision=" + Uri.EscapeDataString(stamp)
            + "#page=" + page.ToString(CultureInfo.InvariantCulture) + "&" + zoom + "&pagemode=" + mode;
        _navigationVersion = version;
        _navigationId = 0;
        _loadedVersion = version;
        _committedDocumentKey = documentKey;
        _documentReady = false;
        _notice.Visibility = Visibility.Collapsed;
        _browser!.Visibility = Visibility.Visible;
        _frame = null;
        PdfSource = _expectedUri;
        Diagnostic("load.navigate", new { path, page, zoom, mode, PageCount });
        _core!.Navigate(_expectedUri);
    }

    private void ConfigureBrowser(CoreWebView2 core)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.FrameCreated += OnFrameCreated;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.AddWebResourceRequestedFilter("http://*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => GuardCallback("navigation.starting", () =>
    {
        args.Cancel = _disposed || !AllowedNavigation(args.Uri);
        if (!args.Cancel && SameFile(args.Uri, _allowedPath) && _navigationVersion == _version) _navigationId = args.NavigationId;
        Diagnostic("navigation.starting", new { args.Uri, args.Cancel, args.NavigationId });
    });

    private bool AllowedNavigation(string value) => value is "about:blank" or "about:srcdoc" || SameFile(value, _allowedPath);

    private static bool SameFile(string value, string? path)
    {
        if (path is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        try { return Path.GetFullPath(uri.LocalPath).Equals(path, StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => GuardCallback("navigation.completed", () =>
    {
        Diagnostic("navigation.completed", new { args.IsSuccess, error = args.WebErrorStatus.ToString(), args.NavigationId });
        if (_disposed || _navigationVersion != _version || args.NavigationId != _navigationId) return;
        _documentReady = args.IsSuccess;
        _loadFailed = !args.IsSuccess;
        if (args.IsSuccess)
        {
            NavigationCount++;
            _pendingError = null;
        }
        else _pendingError = "Das PDF konnte nicht angezeigt werden: " + args.WebErrorStatus;
        _loadCompletion?.TrySetResult();
        RequestWork();
    });

    private void OnFrameCreated(CoreWebView2 sender, CoreWebView2FrameCreatedEventArgs args) =>
        GuardCallback("frame.created", () => _frame = args.Frame);

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        GuardCallback("popup", () => args.Handled = true);

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) =>
        GuardCallback("permission", () => args.State = CoreWebView2PermissionState.Deny);

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args) => GuardCallback("network.blocked", () =>
    {
        args.Response = sender.Environment.CreateWebResourceResponse(new InMemoryRandomAccessStream(), 403,
            "Network disabled in publication viewer", "Content-Type: text/plain\r\nCache-Control: no-store");
    });

    private void GuardCallback(string phase, Action callback)
    {
        try { callback(); }
        catch (Exception exception) when (Recoverable(exception))
        {
            RecordFailure(phase, exception);
            _pendingError = "Das PDF konnte nicht angezeigt werden: " + exception.Message;
            RequestWork();
        }
    }

    private async Task CaptureViewStateAsync(string documentKey, long version)
    {
        if (_core is not { } core) return;
        try
        {
            string? result = null;
            if (_frame is { } frame)
            {
                try { result = await frame.ExecuteScriptAsync(ReadViewerStateScript); }
                catch (Exception exception) when (Recoverable(exception)) { Diagnostic("state.frame.unavailable", new { exception.Message }); }
            }
            if (result is null or "null") result = await core.ExecuteScriptAsync(ReadViewerStateScript);
            using var state = System.Text.Json.JsonDocument.Parse(result);
            if (state.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            if (_disposed || _version != version || _logicalDocumentKey != documentKey) return;
            var data = state.RootElement;
            var page = data.TryGetProperty("page", out var number) && number.ValueKind == System.Text.Json.JsonValueKind.Number
                && number.TryGetInt32(out var readPage) && readPage > 0 ? readPage : _viewState.Page;
            var zoom = data.TryGetProperty("zoom", out var scale) && scale.ValueKind == System.Text.Json.JsonValueKind.Number
                && scale.TryGetDouble(out var readZoom) && double.IsFinite(readZoom)
                && readZoom is >= 10 and <= 1000 ? readZoom : _viewState.Zoom;
            var sidebar = data.TryGetProperty("sidebar", out var side) && side.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                ? side.GetBoolean() : _viewState.SidebarOpen;
            _viewState = new(page, zoom, null, sidebar);
        }
        catch (Exception exception) when (Recoverable(exception)) { Diagnostic("state.capture.unavailable", new { exception.Message }); }
    }

    private const string ReadViewerStateScript = """
        (()=>{const all=[];function walk(r){for(const e of r.querySelectorAll('*')){all.push(e);if(e.shadowRoot)walk(e.shadowRoot)}}walk(document);
        const viewer=all.find(e=>e.tagName==='PDF-VIEWER');if(!viewer)return null;
        const pageInput=all.find(e=>e.tagName==='INPUT'&&(/page.?selector|page.?number/i.test(e.id)||/page number|seitennummer/i.test(e.getAttribute('aria-label')||'')));
        const zoomInput=all.find(e=>e.tagName==='INPUT'&&/zoom/i.test(e.id));
        const side=all.find(e=>/VIEWER.*SIDENAV/.test(e.tagName));
        const page=Number(pageInput?.value);const zoom=Number((zoomInput?.value||'').replace('%',''));
        return {page:Number.isInteger(page)&&page>0?page:null,zoom:zoom>0?zoom:null,
        sidebar:side?side.getBoundingClientRect().width>0&&getComputedStyle(side).visibility!=='hidden':null};})()
        """;

    internal Task<string> ExecuteDiagnosticScriptAsync(string script) => QueueProbe(script, false, null);
    internal Task<string> ExecuteFrameDiagnosticScriptAsync(string script) => QueueProbe(script, true, null);
    internal Task CapturePreviewAsync(string path) => QueueProbe(null, false, Path.GetFullPath(path));

    private Task<string> QueueProbe(string? script, bool frame, string? capture)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS") != "1")
            throw new InvalidOperationException("PDF-Diagnostik ist nur im isolierten QA-Modus verfügbar.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes.Enqueue(new(script, frame, capture, completion));
        RequestWork();
        return completion.Task;
    }

    private void ShowError(string message)
    {
        try { _notice.Text = message; _notice.Visibility = Visibility.Visible; }
        catch (Exception exception) when (Recoverable(exception)) { RecordFailure("error.notice", exception); }
    }

    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
        && exception.HResult != unchecked((int)0x8007000E);

    private void RecordFailure(string phase, Exception exception)
    {
        BrowserFaultCount++;
        WriteDiagnostic("native-publication-errors.jsonl", phase,
            new { exceptionType = exception.GetType().FullName, hresult = $"0x{exception.HResult:X8}", exception.Message, exception.StackTrace });
    }

    private void Diagnostic(string phase, object? detail)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS") == "1") WriteDiagnostic("publication-view.jsonl", phase, detail);
    }

    private void WriteDiagnostic(string file, string phase, object? detail)
    {
        try
        {
            var directory = Path.Combine(App.Current.DataDirectory, "Diagnostics");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, file), System.Text.Json.JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, processId = Environment.ProcessId, phase, active = _active,
                loaded = IsLoaded, disposed = _disposed, path = _requestedPath, version = _version, detail,
            }) + Environment.NewLine);
        }
        catch (Exception exception) when (Recoverable(exception)) { }
    }

    private void CloseBrowser()
    {
        var browser = _browser;
        var core = _core;
        _browser = null; _core = null; _frame = null; _documentReady = false;
        while (_probes.TryDequeue(out var probe)) probe.Completion.TrySetCanceled();
        if (browser is null) { if (_disposed) _closed.TrySetResult(); return; }
        try
        {
            if (core is not null)
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.FrameCreated -= OnFrameCreated;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.PermissionRequested -= OnPermissionRequested;
                core.WebResourceRequested -= OnWebResourceRequested;
            }
            browser.Close();
            Children.Remove(browser);
        }
        catch (Exception exception) when (Recoverable(exception)) { RecordFailure("close", exception); }
        finally { if (_disposed) _closed.TrySetResult(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCompletion?.TrySetCanceled();
        RequestWork();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return new(_closed.Task);
    }

    internal Task DisposeForSmokeAsync()
    {
        Dispose();
        return _closed.Task;
    }

    private sealed record BrowserProbe(string? Script, bool Frame, string? CapturePath, TaskCompletionSource<string> Completion);
    private sealed record PdfViewState(int Page, double? Zoom, double? Scroll, bool SidebarOpen);
}

using Missum.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Missum.App.Controls;

/// <summary>A retained simulation surface with a single queued browser lifecycle.</summary>
internal sealed class NativeSimulationView : Grid, IDisposable
{
    private WebView2? _webView;
    private CoreWebView2? _core;
    private CoreWebView2Frame? _frame;
    private readonly TextBlock _error = new()
    {
        TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
        Visibility = Visibility.Collapsed, Margin = new Thickness(16),
    };
    private readonly Queue<DiagnosticRequest> _probes = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _loadCompletion;
    private Task _loading = Task.CompletedTask;
    private ScientificSimulationArtifact? _lastArtifact;
    private CancellationToken _loadCancellationToken;
    private string? _path, _sha256, _expectedDocumentUri, _pendingError;
    private bool _disposed, _active, _attached, _loadFailed, _activationBlocked;
    private bool _queued, _working, _dirty, _documentReady, _suspended, _captureRequested;
    private long _loadVersion, _loadedVersion, _activityRevision, _appliedActivityRevision = -1, _navigationVersion;
    private ulong _expectedNavigationId;
    private double _scale = 1, _appliedScale = double.NaN;
    internal bool IsInitialized => _core is not null;
    internal bool IsSuspended => _suspended;
    internal bool HasCompletedNavigation => _documentReady && _loadedVersion == _loadVersion;
    internal long CommittedNavigationCount { get; private set; }
    internal long BrowserFaultCount { get; private set; }

    internal NativeSimulationView()
    {
        Height = 680; MinHeight = 460;
        // The browser is created only after this retained visual host is loaded.
        // Constructing a card never activates/appends an initialized WebView2.
        Children.Add(_error);
        Loaded += (_, _) => GuardCallback("host.loaded", () => { _attached = IsLoaded && XamlRoot is not null; _activityRevision++; RequestWork(); });
        Unloaded += (_, _) => GuardCallback("host.unloaded", () => { _attached = IsLoaded && XamlRoot is not null; _activityRevision++; RequestWork(); });
    }

    internal void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        _activityRevision++;
        Diagnostic("activity.desired", new { active, attached = _attached });
        if (active && _loadFailed && !_activationBlocked && !_loadCancellationToken.IsCancellationRequested)
            BeginLoadAttempt();
        RequestWork();
    }

    internal Task LoadAsync(ScientificSimulationArtifact artifact, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastArtifact = artifact;
        _loadCancellationToken = cancellationToken;
        if (_path == artifact.ImagePath && _sha256 == artifact.Sha256 && (!_loadFailed || _activationBlocked)) return _loading;
        _path = artifact.ImagePath; _sha256 = artifact.Sha256;
        BeginLoadAttempt();
        RequestWork();
        return _loading;
    }

    internal void Reload()
    {
        if (_disposed || _lastArtifact is null || _activationBlocked) return;
        BeginLoadAttempt();
        RequestWork();
    }

    internal void SetScale(double scale)
    {
        if (_disposed) return;
        _scale = Math.Clamp(scale, .5, 2);
        RequestWork();
    }

    private void BeginLoadAttempt()
    {
        _loadCompletion?.TrySetCanceled();
        _loadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _loading = _loadCompletion.Task;
        _loadVersion++;
        _loadFailed = false;
        _pendingError = null;
    }

    private void RequestWork()
    {
        _dirty = true;
        if (_working || _queued) return;
        _queued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _queued = false;
            _ = ProcessWorkAsync();
        }))
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
                var attemptVersion = _loadVersion;
                var attemptToken = _loadCancellationToken;
                try
                {
                    if (_pendingError is { } error) ShowError(error);
                    if (Current(attemptVersion) && !_activationBlocked && !_loadFailed
                        && _loadedVersion != _loadVersion && _lastArtifact is { } artifact)
                        await LoadCurrentAsync(artifact, attemptVersion, attemptToken);
                    if (_disposed) { CloseBrowser(); return; }
                    await ApplyActivityAsync();
                    if (_disposed) { CloseBrowser(); return; }
                    if (_documentReady && !_suspended && _scale != _appliedScale)
                    {
                        var scale = _scale;
                        var number = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        await _core!.ExecuteScriptAsync("(()=>{const frame=document.querySelector('iframe');if(!frame)return;frame.style.zoom="
                            + number + ";frame.style.width=(100/" + number + ")+ '%';frame.style.height=(100/" + number + ")+ '%';})()");
                        _appliedScale = scale;
                    }
                    if (_captureRequested && _documentReady && !_suspended)
                    {
                        _captureRequested = false;
                        await CaptureDocumentDiagnosticAsync("navigation.completed.dom");
                    }
                    while (_probes.TryDequeue(out var probe))
                    {
                        try
                        {
                            var result = probe.Frame
                                ? await (_frame ?? throw new InvalidOperationException("Der Simulationsframe ist noch nicht geladen.")).ExecuteScriptAsync(probe.Script)
                                : await (_core ?? throw new InvalidOperationException("Die Simulation ist noch nicht geladen.")).ExecuteScriptAsync(probe.Script);
                            probe.Completion.TrySetResult(result);
                        }
                        catch (Exception exception) when (Recoverable(exception)) { probe.Completion.TrySetException(exception); }
                    }
                }
                catch (OperationCanceledException) when (attemptToken.IsCancellationRequested || _disposed)
                {
                    if (attemptVersion == _loadVersion) _loadCompletion?.TrySetCanceled();
                    else _dirty = true;
                }
                catch (Exception exception) when (Recoverable(exception))
                {
                    if (attemptVersion == _loadVersion)
                    {
                        FailLoad("lifecycle", exception);
                        ShowError(_pendingError!);
                    }
                    else
                    {
                        Diagnostic("load.superseded", new { attemptVersion, currentVersion = _loadVersion, exception.Message });
                        _dirty = true;
                    }
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

    private async Task LoadCurrentAsync(ScientificSimulationArtifact artifact, long version, CancellationToken token)
    {
        Diagnostic("load.begin", new { artifact.ImagePath, artifact.Sha256, version });
        token.ThrowIfCancellationRequested();
        var bytes = await File.ReadAllBytesAsync(artifact.ImagePath, token);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        if (hash != artifact.Sha256) throw new InvalidDataException("Die Simulation wurde während des Ladens verändert. Aktualisiere die Simulation-Ansicht.");
        var document = ScientificSimulationHtml.NativeDocument(System.Text.Encoding.UTF8.GetString(bytes));
        if (!Current(version)) return;
        if (_core is null)
        {
            var profile = Path.Combine(App.Current.DataDirectory, "WebView2", "Simulations");
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
            if (!Current(version)) return;
            try
            {
                var browser = new WebView2();
                _webView = browser;
                AutomationProperties.SetName(browser, "Interaktive Simulation im Simulation-Tab");
                Children.Insert(0, browser);
                await browser.EnsureCoreWebView2Async(environment);
                if (_disposed) return;
                _core = browser.CoreWebView2
                    ?? throw new InvalidOperationException("Die Browserdarstellung der Simulation konnte nicht initialisiert werden.");
                ConfigureBrowser(_core);
            }
            catch (Exception exception) when (Recoverable(exception))
            {
                // Avoid repeatedly activating the same failed native component.
                _activationBlocked = true;
                CloseBrowser();
                throw;
            }
        }
        if (!Current(version)) return;
        if (_suspended) { _core!.Resume(); _suspended = false; }
        _error.Visibility = Visibility.Collapsed;
        _documentReady = false;
        _appliedScale = double.NaN;
        _expectedNavigationId = 0;
        _expectedDocumentUri = ScientificSimulationHtml.HostDocumentUri(document);
        _navigationVersion = version;
        _loadedVersion = version;
        _appliedActivityRevision = -1;
        Diagnostic("load.navigate", new { documentLength = document.Length, active = _active, attached = _attached });
        _core!.NavigateToString(document);
    }

    private bool Current(long version) => !_disposed && _active && _attached && IsLoaded && XamlRoot is not null && version == _loadVersion;

    private void ConfigureBrowser(CoreWebView2 core)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.NewWindowRequested += OnNewWindowRequested;
        core.PermissionRequested += OnPermissionRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.FrameCreated += OnFrameCreated;
    }

    private void OnFrameCreated(CoreWebView2 sender, CoreWebView2FrameCreatedEventArgs args) => GuardCallback("frame.created", () =>
    {
        _frame = args.Frame;
    });

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        GuardCallback("popup", () => args.Handled = true);

    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args) =>
        GuardCallback("permission", () => args.State = CoreWebView2PermissionState.Deny);

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args) => GuardCallback("navigation.starting", () =>
    {
        args.Cancel = _disposed || !ScientificSimulationHtml.IsHostNavigationAllowed(args.Uri, _expectedDocumentUri);
        if (!args.Cancel && string.Equals(args.Uri, _expectedDocumentUri, StringComparison.Ordinal)) _expectedNavigationId = args.NavigationId;
        Diagnostic("navigation.starting", new { uriPrefix = args.Uri[..Math.Min(args.Uri.Length, 60)], uriLength = args.Uri.Length,
            args.Cancel, args.IsUserInitiated, args.NavigationId });
    });

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => GuardCallback("navigation.completed", () =>
    {
        Diagnostic("navigation.completed", new { args.IsSuccess, status = args.WebErrorStatus.ToString(), args.NavigationId });
        if (_disposed || args.NavigationId != _expectedNavigationId || _navigationVersion != _loadVersion) return;
        _documentReady = args.IsSuccess;
        _loadFailed = !args.IsSuccess;
        _pendingError = args.IsSuccess ? null : "Die interaktive Simulation konnte nicht angezeigt werden: " + args.WebErrorStatus;
        if (args.IsSuccess) CommittedNavigationCount++;
        _loadCompletion?.TrySetResult();
        _captureRequested = args.IsSuccess;
        _appliedActivityRevision = -1;
        RequestWork();
    });

    private void GuardCallback(string phase, Action callback)
    {
        try { callback(); }
        catch (Exception exception) when (Recoverable(exception))
        {
            FailLoad(phase, exception);
            RequestWork();
        }
    }

    private async Task ApplyActivityAsync()
    {
        if (_core is not { } core || !_documentReady || _appliedActivityRevision == _activityRevision) return;
        var revision = _activityRevision;
        try
        {
            if (_active && _attached && IsLoaded && XamlRoot is not null)
            {
                if (_webView is { } browser) browser.Visibility = Visibility.Visible;
                if (_suspended) { core.Resume(); _suspended = false; }
                Diagnostic("activity.resumed", null);
            }
            else if (!_suspended)
            {
                // An ancestor becoming Collapsed does not synchronously update the
                // native WebView controller. Hide the actual retained browser and
                // wait for its visibility transition before requesting suspension.
                if (_webView is { } browser) browser.Visibility = Visibility.Collapsed;
                if (!await SuspendHiddenAsync(core, revision)) return;
            }
            _appliedActivityRevision = revision;
        }
        catch (Exception exception) when (Recoverable(exception))
        {
            // Transient visibility rejection gets no immediate retry loop.
            _appliedActivityRevision = revision;
            RecordFailure("activity", exception);
        }
        if (_activityRevision != revision) _dirty = true;
    }

    private bool HiddenTargetCurrent(long revision) => !_disposed && _activityRevision == revision
        && !(_active && _attached && IsLoaded && XamlRoot is not null);

    private async Task<bool> SuspendHiddenAsync(CoreWebView2 core, long revision)
    {
        // A rapid tab round trip needs no suspended renderer. The debounce also
        // gives WinUI's native visibility work a chance to complete independently
        // of the managed queue that received the tab click.
        await Task.Delay(120);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (!HiddenTargetCurrent(revision)) { _dirty = !_disposed; return false; }
            if (attempt > 0) await Task.Delay(attempt * 50);
            if (!HiddenTargetCurrent(revision)) { _dirty = !_disposed; return false; }
            var visibility = await core.ExecuteScriptAsync("document.visibilityState");
            if (!HiddenTargetCurrent(revision)) { _dirty = !_disposed; return false; }
            if (visibility != "\"hidden\"")
            {
                Diagnostic("activity.visibility.pending", new { attempt, visibility });
                continue;
            }
            try
            {
                _suspended = await core.TrySuspendAsync();
                Diagnostic("activity.suspended", new { success = _suspended, attempt });
                if (_activityRevision != revision) _dirty = true;
                return true;
            }
            catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult == unchecked((int)0x8007139F))
            {
                // Chromium's document visibility and the WinUI controller can be
                // updated on adjacent native callbacks. Retry only this documented
                // transition error, with a bound and a fresh desired-state check.
                Diagnostic("activity.visibility.retry", new { attempt, hresult = "0x8007139F" });
            }
        }
        if (!HiddenTargetCurrent(revision)) { _dirty = !_disposed; return false; }
        throw new InvalidOperationException("Der Browser wurde während der Sichtbarkeitsumschaltung nicht unsichtbar; die Simulation konnte nicht pausiert werden.");
    }

    private void FailLoad(string phase, Exception exception)
    {
        _loadFailed = true;
        _pendingError = "Die interaktive Simulation konnte nicht geladen werden: " + exception.Message;
        _loadCompletion?.TrySetResult();
        RecordFailure(phase, exception);
    }

    private void ShowError(string error)
    {
        try { _error.Text = error; _error.Visibility = Visibility.Visible; }
        catch (Exception exception) when (Recoverable(exception)) { RecordFailure("error.notice", exception); }
    }

    internal Task<string> ExecuteDiagnosticScriptAsync(string script) => QueueDiagnostic(script, frame: false);
    internal Task<string> ExecuteFrameDiagnosticScriptAsync(string script) => QueueDiagnostic(script, frame: true);

    private Task<string> QueueDiagnostic(string script, bool frame)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS") != "1")
            throw new InvalidOperationException("Simulationsdiagnostik ist nur im isolierten QA-Modus verfügbar.");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _probes.Enqueue(new(script, frame, completion));
        RequestWork();
        return completion.Task;
    }

    private async Task CaptureDocumentDiagnosticAsync(string phase)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS") != "1" || _core is not { } core || _disposed) return;
        try
        {
            var result = await core.ExecuteScriptAsync("JSON.stringify({hrefPrefix:location.href.slice(0,60),ready:document.readyState,title:document.title,html:document.documentElement?.outerHTML.slice(0,1000),bodyText:document.body?.innerText.slice(0,1000),frames:[...document.querySelectorAll('iframe')].map(f=>({src:f.src,sourceLength:f.srcdoc.length,width:f.clientWidth,height:f.clientHeight,sandbox:f.getAttribute('sandbox')}))})");
            Diagnostic(phase, new { result });
        }
        catch (Exception exception) when (Recoverable(exception)) { RecordFailure(phase, exception); }
    }

    private static bool Recoverable(Exception exception) => exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
        && exception.HResult != unchecked((int)0x8007000E);

    private void RecordFailure(string phase, Exception exception)
    {
        BrowserFaultCount++;
        WriteDiagnostic("native-simulation-errors.jsonl", phase,
            new { exceptionType = exception.GetType().FullName, hresult = $"0x{exception.HResult:X8}", exception.Message, exception.StackTrace });
    }

    private void Diagnostic(string phase, object? detail)
    {
        if (Environment.GetEnvironmentVariable("MISSUM_SCIENCE_QA_DIAGNOSTICS") == "1") WriteDiagnostic("simulation-view.jsonl", phase, detail);
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
                attached = _attached, disposed = _disposed, path = _path, version = _loadVersion, detail,
            }) + Environment.NewLine);
        }
        catch (Exception exception) when (Recoverable(exception)) { }
    }

    private void CloseBrowser()
    {
        var browser = _webView;
        var core = _core;
        _webView = null; _core = null; _frame = null; _suspended = false; _documentReady = false;
        while (_probes.TryDequeue(out var probe)) probe.Completion.TrySetCanceled();
        if (browser is null) { if (_disposed) _closed.TrySetResult(); return; }
        try
        {
            if (core is not null)
            {
                core.NewWindowRequested -= OnNewWindowRequested;
                core.PermissionRequested -= OnPermissionRequested;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.FrameCreated -= OnFrameCreated;
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
        _lastArtifact = null;
        _loadCompletion?.TrySetCanceled();
        // Cleanup waits for the serialized EnsureCore/suspend work to settle.
        RequestWork();
    }

    internal Task DisposeAsync()
    {
        Dispose();
        return _closed.Task;
    }

    internal Task DisposeForSmokeAsync() => DisposeAsync();

    private sealed record DiagnosticRequest(string Script, bool Frame, TaskCompletionSource<string> Completion);
}

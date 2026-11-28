using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Missum.App.Services;

public enum ComputerActionDecision { Execute, ManualDone, Cancel }

public sealed record ComputerApprovalRequest(Guid SessionId, string RunId, string ObservationId,
    string Action, string TargetTitle, int? X, int? Y, string? Text);

public interface IComputerActionApproval
{
    Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken);
    Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken);
}

public sealed record ComputerWindow(string Id, string Title, int ProcessId, long ProcessStartTimeUtcTicks,
    int X, int Y, int Width, int Height, string Process = "");

public sealed record ComputerImageRegion(int X, int Y, int Width, int Height);

public sealed record ComputerViewport(double Width, double Height, double ScrollX, double ScrollY, string Device);

public sealed record ComputerObservation(string ObservationId, byte[] Content, string ContentType,
    int Width, int Height, int DesktopX, int DesktopY, int DesktopWidth, int DesktopHeight,
    string ForegroundTitle, int ForegroundProcessId, IReadOnlyList<ComputerWindow> Windows,
    ComputerImageRegion? ImageRegion = null, string? Url = null, ComputerViewport? Viewport = null,
    System.Text.Json.JsonElement? Elements = null, ComputerApp? App = null, bool Grid = false);

public sealed record ComputerAction(string ObservationId, string Kind, double? X = null, double? Y = null,
    double? EndX = null, double? EndY = null, int? ImageWidth = null, int? ImageHeight = null,
    string? Text = null, string? Key = null, int? ScrollY = null, string? WindowId = null,
    string? CoordinateSpace = null, int? ElementIndex = null, int? ScrollX = null, string? Target = null,
    string? Arguments = null, string? WindowAction = null);

public sealed record ComputerElement(string Name, int ControlType, int ProcessId, bool IsEnabled, bool IsPassword, string RuntimeId = "");

public sealed record ComputerActionResult(string Status, string Message, ComputerElement? Element = null, ComputerAfter? After = null);

/// <summary>What an action changed on the desktop, so the model can verify it before the next step.</summary>
public sealed record ComputerAfter(string ForegroundTitle, string ForegroundProcess, IReadOnlyList<string> OpenedWindows,
    IReadOnlyList<string> ClosedWindows, string? Focus);

internal sealed record ComputerFrame(ComputerObservation Public, byte[] PhysicalPixels,
    ComputerWindow? Foreground, nint FocusHandle, ComputerElement? FocusedElement, DateTimeOffset CapturedAt,
    WebViewFrameState? WebView = null, IReadOnlyList<ComputerUiTarget>? Targets = null, ComputerWindow? Subject = null);

internal interface IComputerDesktopPlatform
{
    Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken);
    Task<ComputerFrame> CaptureWindowAsync(CancellationToken cancellationToken) => CaptureAsync(cancellationToken);
    Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken);
    Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken);
    /// <summary>Describes the desktop after an action (new or closed windows, foreground, focus).</summary>
    Task<ComputerAfter?> DescribeAsync(ComputerFrame frame, CancellationToken cancellationToken) => Task.FromResult<ComputerAfter?>(null);
}

/// <summary>
/// Serializes input and owns a run-bound desktop session. Computer-Use controls the real desktop
/// directly: actions run without per-action approvals and without waiting for a particular
/// foreground window; the user stops a run with "Stoppen". Each observation still authorizes
/// exactly one action, so the model always acts on what it has just seen.
/// </summary>
public sealed class ComputerDesktopBroker : IDisposable
{
    /// <summary>
    /// How long one observation authorizes an action. Local vision models need minutes to read a
    /// 4K desktop and decide; 60 s failed every action. Staleness is still caught where it matters:
    /// the foreground revision must match and the platform re-checks the pixels at the target point.
    /// </summary>
    internal static readonly TimeSpan ObservationLifetime = TimeSpan.FromMinutes(15);

    private sealed class Session(Guid sessionId, string runId, IComputerForegroundMonitor monitor)
    {
        public Guid SessionId { get; } = sessionId;
        public string RunId { get; } = runId;
        public CancellationTokenSource Lifetime { get; } = new();
        public bool Activated { get; set; }
        public ComputerFrame? Frame { get; set; }
        public IComputerForegroundMonitor Monitor { get; } = monitor;
        public long FrameRevision { get; set; }
        public WebViewComputerPlatform? WebPlatform { get; set; }
    }

    private readonly IComputerActionApproval _approval;
    private readonly IComputerDesktopPlatform _desktop;
    private readonly Func<IComputerForegroundMonitor> _monitorFactory;
    private readonly Func<WebViewComputerPlatform?> _webViewFactory;
    private readonly ConcurrentDictionary<(Guid, string), Session> _sessions = new();
    private readonly HashSet<(Guid, string)> _endedRuns = [];
    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ComputerDesktopBroker(IComputerActionApproval approval, DesktopScreenshotService screenshots)
        : this(approval, new WindowsComputerDesktopPlatform(screenshots), static () => new WindowsComputerForegroundMonitor()) { }

    internal ComputerDesktopBroker(IComputerActionApproval approval, IComputerDesktopPlatform desktop,
        Func<IComputerForegroundMonitor>? monitorFactory = null, Func<WebViewComputerPlatform?>? webViewFactory = null)
    {
        _approval = approval;
        _desktop = desktop;
        _monitorFactory = monitorFactory ?? (static () => new NoComputerForegroundMonitor());
        _webViewFactory = webViewFactory ?? (static () => Webdesign.WebdesignPreviewRegistry.Current is { } surface
            ? new WebViewComputerPlatform(surface) : null);
    }

    public void Begin(Guid sessionId, string runId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Computer-Use benötigt eine Sitzung und einen Lauf.");
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_endedRuns.Contains((sessionId, runId)))
                throw new UnauthorizedAccessException("Dieser Computerlauf wurde bereits beendet; ein neuer Lauf ist erforderlich.");
            _sessions.GetOrAdd((sessionId, runId), _ => new Session(sessionId, runId, _monitorFactory()));
        }
    }

    public Task<ComputerObservation> ObserveAsync(Guid sessionId, string runId, CancellationToken cancellationToken = default) =>
        ObserveAsync(sessionId, runId, "desktop", cancellationToken);

    public async Task<ComputerObservation> ObserveAsync(Guid sessionId, string runId, string scope, CancellationToken cancellationToken = default)
    {
        if (scope is not ("desktop" or "window" or "webview")) throw new ArgumentException("Beobachtungsbereich muss desktop, window oder webview sein.", nameof(scope));
        var session = GetSession(sessionId, runId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            // A failed or interrupted new capture must never leave an older frame usable.
            session.Frame = null;
            if (!session.Activated)
            {
                if (!await _approval.ActivateAsync(sessionId, runId, linked.Token).ConfigureAwait(false))
                    throw new UnauthorizedAccessException("Computer-Use wurde nicht freigegeben.");
                linked.Token.ThrowIfCancellationRequested();
                session.Activated = true;
            }
            session.WebPlatform = null;
            if (scope == "webview")
            {
                // The website tab lives inside Missum and does not depend on the foreground window.
                var web = _webViewFactory() ?? throw new InvalidOperationException(
                    "Der Tab Webseite ist nicht geöffnet. Öffne eine Webdesign-Sitzung mit Projekt in Missum.");
                var webFrame = await web.CaptureAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                session.Frame = webFrame;
                session.WebPlatform = web;
                return webFrame.Public;
            }
            var frame = scope == "window" ? await _desktop.CaptureWindowAsync(linked.Token).ConfigureAwait(false)
                : await _desktop.CaptureAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            session.Monitor.Expect(frame.Foreground?.Id);
            session.Frame = frame;
            session.FrameRevision = session.Monitor.Revision;
            return frame.Public;
        }
        finally { _gate.Release(); }
    }

    public async Task<ComputerActionResult> ExecuteAsync(Guid sessionId, string runId, ComputerAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var session = GetSession(sessionId, runId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            var frame = session.Frame;
            // A frame can authorize at most one action, including a refused or failed action.
            session.Frame = null;
            if (!session.Activated || frame is null || frame.Public.ObservationId != action.ObservationId)
                throw new InvalidOperationException("Die Beobachtung ist nicht mehr gültig. Beobachte den Desktop erneut.");
            if (DateTimeOffset.UtcNow - frame.CapturedAt > ObservationLifetime)
                throw new InvalidOperationException("Die Beobachtung ist abgelaufen. Beobachte den Desktop erneut.");
            if (frame.WebView is not null)
                return await ExecuteWebViewAsync(session, frame, action, linked.Token).ConfigureAwait(false);
            action = NormalizeCoordinates(action, frame.Public);
            ValidateAction(action, frame.Public, frame.Targets?.Count ?? 0);
            var kind = action.Kind.ToLowerInvariant();
            if (kind == "focus" && !frame.Public.Windows.Any(window => window.Id == action.WindowId))
                throw new InvalidOperationException("Das anzusteuernde Fenster gehört nicht zur aktuellen Beobachtung.");
            if (kind == "inspect")
            {
                var element = await _desktop.InspectAsync(frame, action, linked.Token).ConfigureAwait(false);
                return new("completed", "Zugängliches UI-Element gelesen. Beobachte den Desktop vor weiteren Aktionen erneut.", element);
            }
            // Direct desktop control: no approval step and no foreground lock. The user can stop the
            // run at any time; Missum never steers its own window (checked by the platform).
            if (kind == "focus") session.Monitor.Expect(action.WindowId);
            await _desktop.ExecuteAsync(frame, action, true, linked.Token).ConfigureAwait(false);
            ComputerAfter? after = null;
            if (kind != "wait")
            {
                try { after = await _desktop.DescribeAsync(frame, linked.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return new("completed", "Aktion ausgeführt. Prüfe after und beobachte den Desktop erneut, bevor du fortfährst.", null, after);
        }
        finally { session.Monitor.AllowMissum(false); _gate.Release(); }
    }

    /// <summary>
    /// An action in the website tab: same coordinate and validity rules as on the desktop, but
    /// the page itself is the target. Actions run directly, like on the desktop.
    /// </summary>
    private static async Task<ComputerActionResult> ExecuteWebViewAsync(Session session, ComputerFrame frame, ComputerAction action,
        CancellationToken cancellationToken)
    {
        var web = session.WebPlatform ?? throw new InvalidOperationException("Die Webseiten-Beobachtung ist nicht mehr gültig. Beobachte erneut.");
        action = ResolveWebElement(NormalizeCoordinates(action, frame.Public), frame.Public);
        ValidateAction(action, frame.Public);
        if (action.Kind.ToLowerInvariant() is "right_click" or "middle_click" or "set_value" or "window" or "launch")
            throw new ArgumentException("Diese Aktion ist im Tab Webseite nicht verfügbar; nutze click, type, keypress, scroll oder drag.");
        var kind = action.Kind.ToLowerInvariant();
        if (kind == "inspect")
        {
            var element = await web.InspectAsync(frame, action, cancellationToken).ConfigureAwait(false);
            return new("completed", "Element der Webseite gelesen. Beobachte erneut, bevor du fortfährst.", element);
        }
        await web.ExecuteAsync(frame, action, true, cancellationToken).ConfigureAwait(false);
        return new("completed", "Aktion in der Webseite ausgeführt. Beobachte erneut, bevor du fortfährst.");
    }

    public void End(Guid sessionId, string runId)
    {
        Session? session;
        lock (_lifecycle)
        {
            _endedRuns.Add((sessionId, runId));
            _sessions.TryRemove((sessionId, runId), out session);
        }
        if (session is not null)
        {
            session.Frame = null;
            session.Lifetime.Cancel();
            session.Monitor.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lifecycle) _disposed = true;
        foreach (var key in _sessions.Keys) End(key.Item1, key.Item2);
        _gate.Dispose();
    }

    private Session GetSession(Guid sessionId, string runId) =>
        _sessions.TryGetValue((sessionId, runId), out var value) ? value
            : throw new UnauthorizedAccessException("Kein aktives Computer-Use für diese Sitzung und diesen Lauf.");

    internal static ComputerAction NormalizeCoordinates(ComputerAction action, ComputerObservation frame)
    {
        if (action.CoordinateSpace is null)
        {
            if (action.X is not null || action.Y is not null || action.EndX is not null || action.EndY is not null)
                throw new ArgumentException("Für Koordinaten muss coordinateSpace ausdrücklich 'pixels' oder 'normalized1000' sein; die Aktion wurde nicht ausgeführt.");
            return action;
        }
        if (action.CoordinateSpace == "pixels") return action;
        if (action.CoordinateSpace != "normalized1000")
            throw new ArgumentException("Unbekannter Koordinatenraum. Nutze pixels oder normalized1000.");
        static double? Scale(double? value, int pixels)
        {
            if (value is null) return null;
            if (!double.IsFinite(value.Value) || value.Value < 0 || value.Value >= 1000)
                throw new ArgumentOutOfRangeException(nameof(value), "Normalisierte Koordinaten müssen zwischen 0 und 999 liegen.");
            return value.Value * pixels / 1000d;
        }
        return action with { X = Scale(action.X, frame.Width), Y = Scale(action.Y, frame.Height),
            EndX = Scale(action.EndX, frame.Width), EndY = Scale(action.EndY, frame.Height), CoordinateSpace = "pixels" };
    }

    internal static readonly string[] Kinds =
    [
        "inspect", "move", "click", "double_click", "right_click", "middle_click", "drag", "scroll",
        "type", "set_value", "keypress", "focus", "window", "launch", "wait",
    ];

    internal static void ValidateAction(ComputerAction action, ComputerObservation frame, int targetCount = 0)
    {
        var kind = action.Kind?.ToLowerInvariant();
        if (kind is null || !Kinds.Contains(kind))
            throw new ArgumentException("Nicht unterstützte Computeraktion.");
        if ((action.ImageWidth is { } w && w != frame.Width) || (action.ImageHeight is { } h && h != frame.Height))
            throw new InvalidOperationException("Die Bildabmessungen stimmen nicht mit der Beobachtung überein.");
        if (action.ElementIndex is { } index && (index < 1 || index > targetCount))
            throw new ArgumentOutOfRangeException(nameof(action), $"elementIndex {index} gehört nicht zur aktuellen Beobachtung (1–{targetCount}).");
        static bool Within(double? n, int size) => n is { } x && double.IsFinite(x) && x >= 0 && x < size;
        var byElement = action.ElementIndex is not null;
        if (kind is "inspect" or "move" or "click" or "double_click" or "right_click" or "middle_click" or "drag" or "scroll" && !byElement)
        {
            if (kind == "scroll" && (action.X is null || action.Y is null))
                throw new ArgumentException("Für scroll sind elementIndex oder x und y im scrollbaren Bereich erforderlich.");
            if (!Within(action.X, frame.Width) || !Within(action.Y, frame.Height))
                throw new ArgumentOutOfRangeException(nameof(action), "Die Koordinate liegt außerhalb der Beobachtung; nutze elementIndex oder x/y.");
        }
        if (kind == "drag" && (!Within(action.EndX, frame.Width) || !Within(action.EndY, frame.Height)))
            throw new ArgumentOutOfRangeException(nameof(action), "Das Drag-Ziel endX/endY liegt außerhalb der Beobachtung.");
        if (kind == "type" && (string.IsNullOrEmpty(action.Text) || action.Text.Length > 4096))
            throw new ArgumentException("Texteingabe muss zwischen 1 und 4096 Zeichen lang sein.");
        if (kind == "set_value" && (!byElement || action.Text is null || action.Text.Length > 4096))
            throw new ArgumentException("set_value benötigt elementIndex und text (höchstens 4096 Zeichen).");
        if (kind == "keypress" && (string.IsNullOrWhiteSpace(action.Key) || action.Key.Length > 80))
            throw new ArgumentException("Ungültige Tastenkombination.");
        if (kind == "focus" && string.IsNullOrWhiteSpace(action.WindowId))
            throw new ArgumentException("Für Fensterwechsel ist eine beobachtete Fensterkennung erforderlich.");
        if (kind == "window" && action.WindowAction is not ("focus" or "maximize" or "minimize" or "restore" or "close"))
            throw new ArgumentException("windowAction muss focus, maximize, minimize, restore oder close sein.");
        if (kind == "launch" && (string.IsNullOrWhiteSpace(action.Target) || action.Target.Length > 1024 || action.Arguments?.Length > 2048))
            throw new ArgumentException("launch benötigt target (Programm, Datei, Ordner, URL oder unreal).");
        if (kind == "scroll")
        {
            var vertical = action.ScrollY ?? 0;
            var horizontal = action.ScrollX ?? 0;
            if (vertical == 0 && horizontal == 0 || Math.Abs((long)vertical) > 2400 || Math.Abs((long)horizontal) > 2400)
                throw new ArgumentException("Ungültiger Scrollwert: scrollY oder scrollX zwischen -2400 und 2400, ungleich 0.");
        }
    }

    /// <summary>In the website tab an elementIndex selects the n-th listed page element (its center).</summary>
    internal static ComputerAction ResolveWebElement(ComputerAction action, ComputerObservation frame)
    {
        if (action.ElementIndex is not { } index) return action;
        if (frame.Elements is not { ValueKind: System.Text.Json.JsonValueKind.Array } elements || index < 1 || index > elements.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(action), "elementIndex gehört nicht zur aktuellen Beobachtung.");
        var element = elements[index - 1];
        double Read(string name) => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
        var x = Math.Clamp(Read("x") + Read("width") / 2, 0, frame.Width - 1);
        var y = Math.Clamp(Read("y") + Read("height") / 2, 0, frame.Height - 1);
        return action with { ElementIndex = null, X = x, Y = y, CoordinateSpace = "pixels" };
    }
}

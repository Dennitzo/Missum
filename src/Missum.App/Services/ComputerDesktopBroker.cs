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
    int X, int Y, int Width, int Height);

public sealed record ComputerImageRegion(int X, int Y, int Width, int Height);

public sealed record ComputerObservation(string ObservationId, byte[] Content, string ContentType,
    int Width, int Height, int DesktopX, int DesktopY, int DesktopWidth, int DesktopHeight,
    string ForegroundTitle, int ForegroundProcessId, IReadOnlyList<ComputerWindow> Windows,
    ComputerImageRegion? ImageRegion = null);

public sealed record ComputerAction(string ObservationId, string Kind, double? X = null, double? Y = null,
    double? EndX = null, double? EndY = null, int? ImageWidth = null, int? ImageHeight = null,
    string? Text = null, string? Key = null, int? ScrollY = null, string? WindowId = null,
    string? CoordinateSpace = null);

public sealed record ComputerElement(string Name, int ControlType, int ProcessId, bool IsEnabled, bool IsPassword, string RuntimeId = "");

public sealed record ComputerActionResult(string Status, string Message, ComputerElement? Element = null);

internal sealed record ComputerFrame(ComputerObservation Public, byte[] PhysicalPixels,
    ComputerWindow? Foreground, nint FocusHandle, ComputerElement? FocusedElement, DateTimeOffset CapturedAt);

internal interface IComputerDesktopPlatform
{
    Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken);
    Task<ComputerFrame> CaptureWindowAsync(CancellationToken cancellationToken) => CaptureAsync(cancellationToken);
    Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken);
    Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken);
}

/// <summary>Serializes input and owns an explicitly activated, run-bound desktop session.</summary>
public sealed class ComputerDesktopBroker : IDisposable
{
    private sealed class Session(Guid sessionId, string runId, IComputerForegroundMonitor monitor)
    {
        public Guid SessionId { get; } = sessionId;
        public string RunId { get; } = runId;
        public CancellationTokenSource Lifetime { get; } = new();
        public bool Activated { get; set; }
        public ComputerFrame? Frame { get; set; }
        public IComputerForegroundMonitor Monitor { get; } = monitor;
        public long FrameRevision { get; set; }
    }

    private readonly IComputerActionApproval _approval;
    private readonly IComputerDesktopPlatform _desktop;
    private readonly Func<IComputerForegroundMonitor> _monitorFactory;
    private readonly ConcurrentDictionary<(Guid, string), Session> _sessions = new();
    private readonly HashSet<(Guid, string)> _endedRuns = [];
    private readonly object _lifecycle = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public ComputerDesktopBroker(IComputerActionApproval approval, DesktopScreenshotService screenshots)
        : this(approval, new WindowsComputerDesktopPlatform(screenshots), static () => new WindowsComputerForegroundMonitor()) { }

    internal ComputerDesktopBroker(IComputerActionApproval approval, IComputerDesktopPlatform desktop,
        Func<IComputerForegroundMonitor>? monitorFactory = null)
    {
        _approval = approval;
        _desktop = desktop;
        _monitorFactory = monitorFactory ?? (static () => new NoComputerForegroundMonitor());
    }

    public void Begin(Guid sessionId, string runId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Computersteuerung benötigt eine Sitzung und einen Lauf.");
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
        if (scope is not ("desktop" or "window")) throw new ArgumentException("Beobachtungsbereich muss desktop oder window sein.", nameof(scope));
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
                    throw new UnauthorizedAccessException("Die Computersteuerung wurde nicht freigegeben.");
                linked.Token.ThrowIfCancellationRequested();
                session.Activated = true;
            }
            await session.Monitor.WaitForExpectedAsync(linked.Token).ConfigureAwait(false);
            var revision = session.Monitor.Revision;
            var frame = scope == "window" ? await _desktop.CaptureWindowAsync(linked.Token).ConfigureAwait(false)
                : await _desktop.CaptureAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            if (session.Monitor.Revision != revision)
                throw new InvalidOperationException("Das Vordergrundfenster hat sich während der Aufnahme geändert. Bitte erneut beobachten.");
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
            if (DateTimeOffset.UtcNow - frame.CapturedAt > TimeSpan.FromSeconds(60))
                throw new InvalidOperationException("Die Beobachtung ist abgelaufen. Beobachte den Desktop erneut.");
            var waited = await session.Monitor.WaitForExpectedAsync(linked.Token).ConfigureAwait(false);
            if (waited || session.Monitor.Revision != session.FrameRevision)
                throw new InvalidOperationException("Das Vordergrundfenster hat sich geändert. Beobachte den Desktop nach der Pause erneut.");
            action = NormalizeCoordinates(action, frame.Public);
            ValidateAction(action, frame.Public);
            var kind = action.Kind.ToLowerInvariant();
            if (kind == "focus" && !frame.Public.Windows.Any(window => window.Id == action.WindowId))
                throw new InvalidOperationException("Das anzusteuernde Fenster gehört nicht zur aktuellen Beobachtung.");
            var approved = false;
            // The broker, not the model, classifies a narrow set of navigation
            // actions. Unknown controls still require explicit user review.
            if (kind == "inspect")
            {
                var element = await _desktop.InspectAsync(frame, action, linked.Token).ConfigureAwait(false);
                return new("completed", "Zugängliches UI-Element gelesen. Beobachte den Desktop vor weiteren Aktionen erneut.", element);
            }
            var safeNavigation = kind is "move" or "wait" or "focus"
                || kind == "keypress" && string.Equals(action.Key, "Tab", StringComparison.OrdinalIgnoreCase);
            if (kind == "scroll")
            {
                try
                {
                    var element = await _desktop.InspectAsync(frame, action, linked.Token).ConfigureAwait(false);
                    // UIA Document, List, ListItem and ScrollBar describe navigation
                    // targets; editable and unknown controls still require review.
                    safeNavigation = element.IsEnabled && !element.IsPassword
                        && element.ProcessId == frame.Foreground?.ProcessId && element.ControlType is 50007 or 50008 or 50018 or 50030;
                }
                catch (InvalidOperationException) { safeNavigation = false; }
            }
            if (kind == "click")
            {
                try
                {
                    // An ordinary click into a verified edit control is navigation.
                    // Buttons and controls whose identity cannot be checked still need review.
                    var element = await _desktop.InspectAsync(frame, action, linked.Token).ConfigureAwait(false);
                    safeNavigation = IsOrdinaryEdit(element, frame.Foreground);
                }
                catch (InvalidOperationException) { safeNavigation = false; }
            }
            if (kind == "type")
            {
                // The platform rechecks the focus handle and UIA identity immediately
                // before injection. Control characters can submit or move focus, so
                // only plain text in the observed edit control is automatic.
                safeNavigation = frame.FocusHandle != 0
                    && IsOrdinaryEdit(frame.FocusedElement, frame.Foreground)
                    && action.Text!.All(character => !char.IsControl(character));
            }
            if (!safeNavigation)
            {
                var approvalTarget = kind == "focus"
                    ? frame.Public.Windows.SingleOrDefault(window => window.Id == action.WindowId)
                        ?? throw new InvalidOperationException("Das anzusteuernde Fenster gehört nicht zur aktuellen Beobachtung.")
                    : frame.Foreground;
                session.Monitor.AllowMissum(true);
                var decision = await _approval.RequestAsync(new(sessionId, runId, action.ObservationId,
                    kind, approvalTarget?.Title ?? "Desktop", Round(action.X), Round(action.Y),
                    kind == "type" ? action.Text : action.Key), linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                if (decision == ComputerActionDecision.Cancel)
                    return new("cancelled", "Die Aktion wurde abgebrochen. Vor weiteren Aktionen neu beobachten.");
                if (decision == ComputerActionDecision.ManualDone)
                    return new("manual", "Die Aktion wurde durch den Nutzer übernommen. Vor weiteren Aktionen neu beobachten.");
                if (decision != ComputerActionDecision.Execute)
                    throw new UnauthorizedAccessException("Die Aktion wurde nicht freigegeben.");
                waited = await session.Monitor.WaitForExpectedAsync(linked.Token).ConfigureAwait(false);
                if (waited || session.Monitor.Revision != session.FrameRevision)
                    throw new InvalidOperationException("Das Vordergrundfenster hat sich während der Freigabe geändert. Bitte erneut beobachten.");
                if (DateTimeOffset.UtcNow - frame.CapturedAt > TimeSpan.FromSeconds(60))
                    throw new InvalidOperationException("Die Beobachtung ist während der Freigabe abgelaufen. Bitte erneut beobachten.");
                approved = true;
            }
            waited = await session.Monitor.WaitForExpectedAsync(linked.Token).ConfigureAwait(false);
            if (waited || session.Monitor.Revision != session.FrameRevision)
                throw new InvalidOperationException("Das Vordergrundfenster hat sich geändert. Bitte erneut beobachten.");
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            void OnWindowChanged() { try { interrupted.Cancel(); } catch (ObjectDisposedException) { } }
            session.Monitor.UnexpectedForeground += OnWindowChanged;
            try
            {
                if (session.Monitor.Revision != session.FrameRevision)
                    throw new InvalidOperationException("Das Vordergrundfenster hat sich geändert. Bitte erneut beobachten.");
                if (kind == "focus") session.Monitor.Expect(action.WindowId);
                await _desktop.ExecuteAsync(frame, action, approved, interrupted.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested && interrupted.IsCancellationRequested)
            {
                if (kind == "focus") session.Monitor.Expect(frame.Foreground?.Id);
                throw new InvalidOperationException("Das Vordergrundfenster hat sich geändert. Bitte erneut beobachten.");
            }
            catch
            {
                if (kind == "focus") session.Monitor.Expect(frame.Foreground?.Id);
                throw;
            }
            finally { session.Monitor.UnexpectedForeground -= OnWindowChanged; }
            return new("completed", "Aktion ausgeführt. Beobachte den Desktop erneut, bevor du fortfährst.");
        }
        finally { session.Monitor.AllowMissum(false); _gate.Release(); }
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
            : throw new UnauthorizedAccessException("Keine aktive Computersteuerung für diese Sitzung und diesen Lauf.");

    private static int? Round(double? value) => value is { } number && double.IsFinite(number) ? (int)Math.Round(number) : null;

    private static bool IsOrdinaryEdit(ComputerElement? element, ComputerWindow? foreground) =>
        element is { ControlType: 50004, IsEnabled: true, IsPassword: false }
        && foreground is not null && element.ProcessId == foreground.ProcessId
        && !string.IsNullOrWhiteSpace(element.RuntimeId);

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

    internal static void ValidateAction(ComputerAction action, ComputerObservation frame)
    {
        var kind = action.Kind?.ToLowerInvariant();
        if (kind is not ("inspect" or "move" or "click" or "double_click" or "drag" or "scroll" or "type" or "keypress" or "focus" or "wait"))
            throw new ArgumentException("Nicht unterstützte Computeraktion.");
        if ((action.ImageWidth is { } w && w != frame.Width) || (action.ImageHeight is { } h && h != frame.Height))
            throw new InvalidOperationException("Die Bildabmessungen stimmen nicht mit der Beobachtung überein.");
        static bool Within(double? n, int size) => n is { } x && double.IsFinite(x) && x >= 0 && x < size;
        if (kind is "inspect" or "move" or "click" or "double_click" or "drag" or "scroll")
        {
            if (kind == "scroll" && (action.X is null || action.Y is null))
                throw new ArgumentException("Für scroll sind x und y als Bildpunkt im scrollbaren Bereich erforderlich.");
            if (!Within(action.X, frame.Width) || !Within(action.Y, frame.Height))
                throw new ArgumentOutOfRangeException(nameof(action), "Die Koordinate liegt außerhalb der Beobachtung.");
            if (kind == "drag" && (!Within(action.EndX, frame.Width) || !Within(action.EndY, frame.Height)))
                throw new ArgumentOutOfRangeException(nameof(action), "Das Drag-Ziel liegt außerhalb der Beobachtung.");
        }
        if (kind == "type" && (string.IsNullOrEmpty(action.Text) || action.Text.Length > 4096))
            throw new ArgumentException("Texteingabe muss zwischen 1 und 4096 Zeichen lang sein.");
        if (kind == "keypress" && (string.IsNullOrWhiteSpace(action.Key) || action.Key.Length > 80))
            throw new ArgumentException("Ungültige Tastenkombination.");
        if (kind == "focus" && string.IsNullOrWhiteSpace(action.WindowId))
            throw new ArgumentException("Für Fensterwechsel ist eine beobachtete Fensterkennung erforderlich.");
        if (kind == "scroll" && (action.ScrollY is null or 0 || Math.Abs((long)action.ScrollY.Value) > 2400))
            throw new ArgumentException("Ungültiger Scrollwert.");
    }
}

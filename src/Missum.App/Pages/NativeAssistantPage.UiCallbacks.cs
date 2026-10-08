using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private readonly Dictionary<string, (DateTimeOffset WrittenAt, int Suppressed)> _uiCallbackDiagnostics = new(StringComparer.Ordinal);
    private long _uiCallbackFaultCount, _successfulUiRenderTicks;
    private string? _lastUiEventType, _lastUiEventRevision;
    private bool _uiRenderTickActive, _conversationLayoutSyncing;
    private Action? _uiRenderTickSmokeProbe;

    internal static bool IsRecoverableUiProjectionException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        // A COM projection may represent E_OUTOFMEMORY without the managed
        // OutOfMemoryException type. Fatal conditions never become UI retries.
        if (exception is OutOfMemoryException or AccessViolationException or StackOverflowException
            || exception.HResult == unchecked((int)0x8007000E)) return false;
        return exception is COMException or InvalidOperationException or ArgumentException
            || exception.HResult == unchecked((int)0x80004005);
    }

    private bool RunUiCallback(string phase, Action projection)
    {
        if (_disposed) return false;
        try { projection(); return true; }
        catch (Exception exception) when (IsRecoverableUiProjectionException(exception))
        {
            // Keep the latest committed messages and tool receipts. A failed
            // presentation is retried; it never cancels or restarts a model run.
            _messagesDirty = true;
            _uiCallbackFaultCount++;
            ReportUiProjectionFailure(phase, exception);
            return false;
        }
    }

    private void OnNativeRenderTimerTick()
    {
        if (_disposed || _uiRenderTickActive) return;
        _uiRenderTickActive = true;
        try
        {
            RunUiCallback("renderTimer.Tick", () =>
            {
                var tickFaults = _uiCallbackFaultCount;
                _uiRenderTickSmokeProbe?.Invoke();
                UpdateVoiceVisual();
                UpdateRunDurations();
                if (_messagesDirty)
                {
                    var projectionFaults = _uiCallbackFaultCount;
                    RenderMessagesNow();
                    _messagesDirty = _uiCallbackFaultCount != projectionFaults;
                }
                RefreshThinkingIndicators();
                if (_uiCallbackFaultCount == tickFaults) _successfulUiRenderTicks++;
            });
        }
        finally { _uiRenderTickActive = false; }
    }

    private static string UiProjectionDiagnosticPath => Path.Combine(App.Current.DataDirectory, "Diagnostics", "native-ui-render-errors.jsonl");

    private void ReportUiProjectionFailure(string phase, Exception exception)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var key = phase + "|" + exception.GetType().FullName + "|" + exception.HResult + "|" + exception.Message;
            var previous = _uiCallbackDiagnostics.GetValueOrDefault(key);
            if (previous.WrittenAt != default && now - previous.WrittenAt < TimeSpan.FromSeconds(5))
            {
                _uiCallbackDiagnostics[key] = (previous.WrittenAt, previous.Suppressed + 1);
                return;
            }
            if (!_uiCallbackDiagnostics.ContainsKey(key) && _uiCallbackDiagnostics.Count >= 128)
                _uiCallbackDiagnostics.Remove(_uiCallbackDiagnostics.MinBy(entry => entry.Value.WrittenAt).Key);
            _uiCallbackDiagnostics[key] = (now, 0);
            var diagnostic = new
            {
                at = now, phase, sessionId = _session, mode = _mode, view = ConversationViewKey,
                lastEventType = _lastUiEventType, lastEventRevision = _lastUiEventRevision,
                running = _running, disposed = _disposed, messageCount = _messages.Count,
                displayedMessageCount = DisplayMessages.Count, cachedMessageViews = _messageViews.Count,
                faultCount = _uiCallbackFaultCount, suppressedIdenticalFailures = previous.Suppressed,
                managedThreadId = Environment.CurrentManagedThreadId, threadName = Thread.CurrentThread.Name,
                threadPool = Thread.CurrentThread.IsThreadPoolThread, apartment = Thread.CurrentThread.GetApartmentState().ToString(),
                exceptionType = exception.GetType().FullName, hresult = $"0x{exception.HResult:X8}",
                message = exception.Message, stack = exception.StackTrace, exception = exception.ToString(),
            };
            var file = UiProjectionDiagnosticPath;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, JsonSerializer.Serialize(diagnostic, JsonOptions) + Environment.NewLine);
        }
        catch (Exception loggingException) when (loggingException is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
            && loggingException.HResult != unchecked((int)0x8007000E))
        {
            Debug.WriteLine($"Missum UI projection diagnostic failed ({phase}): {loggingException}; original: {exception}");
        }
    }
}

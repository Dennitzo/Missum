using System.Runtime.InteropServices;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Portable smoke only: exercise the actual 80-ms dispatcher timer, rather
    // than directly invoking a helper and assuming the WinRT ABI is protected.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201", Justification = "The isolated UI smoke deliberately injects the exact COM E_FAIL observed in the crash dump.")]
    private async Task<object> VerifyUiCallbackGuardSmokeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("UI-Callback-Fehlerinjektion ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        if (_uiRenderTickSmokeProbe is not null) throw new InvalidOperationException("Ein UI-Callback-Smoke läuft bereits.");
        var owner = _session;
        var mode = _mode;
        var running = _running;
        var snapshot = _snapshot.GetRawText();
        var messages = string.Join("|", _messages.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + ":" + pair.Value.GetRawText()));
        var originalDirty = _messagesDirty;
        var faults = _uiCallbackFaultCount;
        var successfulTicks = _successfulUiRenderTicks;
        var invocations = 0;
        var marker = "MISSUM_SMOKE_UI_RENDER:" + Guid.NewGuid().ToString("N");
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiRenderTickSmokeProbe = () =>
        {
#pragma warning disable CA2201 // Deliberate isolated smoke injection of the observed COM E_FAIL.
            if (++invocations == 1) throw new COMException(marker, unchecked((int)0x80004005));
#pragma warning restore CA2201
            _uiRenderTickSmokeProbe = null;
            resumed.TrySetResult();
        };
        try
        {
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token);
            if (_uiCallbackFaultCount != faults + 1 || _successfulUiRenderTicks <= successfulTicks)
                throw new InvalidOperationException("Die Timer-Fehlerinjektion wurde nicht abgefangen oder die nächste Projektion blieb aus.");
            if (_session != owner || _mode != mode || _running != running || _snapshot.GetRawText() != snapshot
                || string.Join("|", _messages.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => pair.Key + ":" + pair.Value.GetRawText())) != messages)
                throw new InvalidOperationException("Die UI-Fehlerbehandlung hat den Sitzungseigentümer, Auftrag oder gespeicherte Nachrichten verändert.");
            var diagnostic = await File.ReadAllTextAsync(UiProjectionDiagnosticPath, _lifetime.Token);
            if (!diagnostic.Contains(marker, StringComparison.Ordinal) || !diagnostic.Contains("renderTimer.Tick", StringComparison.Ordinal))
                throw new InvalidOperationException("Die echte Timer-Ausnahme wurde nicht dauerhaft diagnostiziert.");
            return new
            {
                processId = Environment.ProcessId, timerMilliseconds = 80, injected = "COM_E_FAIL",
                caughtFailures = _uiCallbackFaultCount - faults, nextProjectionSucceeded = true,
                ownerUnchanged = true, messagesUnchanged = true, runUnchanged = true,
                sessionId = owner, diagnosticPath = UiProjectionDiagnosticPath,
            };
        }
        finally { _uiRenderTickSmokeProbe = null; _messagesDirty |= originalDirty; }
    }
}

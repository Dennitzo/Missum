namespace Missum.App.Services;

/// <summary>Writes an immutable copy snapshot without blocking the UI when another application owns the clipboard.</summary>
internal sealed class ClipboardTextWriter(Action<string> write, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private long _request;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(360)];

    internal async Task<bool> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var request = Interlocked.Increment(ref _request);
        for (var attempt = 0; ; attempt++)
        {
            if (cancellationToken.IsCancellationRequested || request != Volatile.Read(ref _request)) return false;
            try { write(text); return true; }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                if (!IsBusy(exception) || attempt >= RetryDelays.Length) return false;
            }
            // Deliberately keep the caller's UI context: WinRT Clipboard is a UI-thread API.
            try { await _delay(RetryDelays[attempt], cancellationToken); }
            catch (Exception exception) when (IsRecoverable(exception)) { return false; }
        }
    }

    private static bool IsBusy(Exception exception) => exception.HResult is
        unchecked((int)0x800401D0) or unchecked((int)0x800401D1) or unchecked((int)0x800401D2)
        or unchecked((int)0x800401D4) or unchecked((int)0x80070005) or unchecked((int)0x800700AA);

    private static bool IsRecoverable(Exception exception) => exception is not (OutOfMemoryException or AccessViolationException or StackOverflowException)
        && exception.HResult != unchecked((int)0x8007000E);
}

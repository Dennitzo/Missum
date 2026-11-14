using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Missum.App.Services;

internal interface IComputerForegroundMonitor : IDisposable
{
    long Revision { get; }
    event Action? UnexpectedForeground;
    void Expect(string? windowId);
    void AllowMissum(bool allow);
    Task<bool> WaitForExpectedAsync(CancellationToken cancellationToken);
}

// Records foreground-window changes, not keyboard or mouse events or content.
// The hook exists only for the lifetime of the computer-control run.
internal sealed class WindowsComputerForegroundMonitor : IComputerForegroundMonitor
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WinEventProcedure _callback;
    private readonly Thread _thread;
    private long _expected, _revision;
    private int _allowMissum;
    private uint _threadId;
    private nint _hook;
    private bool _disposed;
    public event Action? UnexpectedForeground;

    public WindowsComputerForegroundMonitor()
    {
        _callback = ForegroundChanged;
        _thread = new Thread(Pump) { IsBackground = true, Name = "Missum computer foreground monitor" };
        _thread.Start();
        try { _ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        catch { Dispose(); throw; }
    }

    public long Revision => Interlocked.Read(ref _revision);
    public void Expect(string? windowId) => Interlocked.Exchange(ref _expected, ParseHandle(windowId).ToInt64());
    public void AllowMissum(bool allow) => Volatile.Write(ref _allowMissum, allow ? 1 : 0);

    public async Task<bool> WaitForExpectedAsync(CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expected = (nint)Interlocked.Read(ref _expected);
            var foreground = GetForegroundWindow();
            if (expected == 0 || foreground == expected || IsAllowedMissum(foreground)) return waited;
            waited = true;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Pump()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _ = PeekMessage(out _, 0, 0, 0, 0);
            _hook = SetWinEventHook(3, 3, 0, _callback, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND, out of context
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult();
            while (GetMessage(out _, 0, 0, 0) > 0) { }
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
        finally { if (_hook != 0) _ = UnhookWinEvent(_hook); }
    }

    private void ForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
    {
        var expected = (nint)Interlocked.Read(ref _expected);
        if (expected != 0 && window != expected && !IsAllowedMissum(window))
        {
            Interlocked.Increment(ref _revision);
            UnexpectedForeground?.Invoke();
        }
    }

    private bool IsAllowedMissum(nint window)
    {
        if (Volatile.Read(ref _allowMissum) == 0 || window == 0) return false;
        _ = GetWindowThreadProcessId(window, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static nint ParseHandle(string? id) => id is not null && id.StartsWith("window-", StringComparison.Ordinal)
        && long.TryParse(id[7..], System.Globalization.NumberStyles.HexNumber, null, out var value) ? (nint)value : 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) _ = PostThreadMessage(_threadId, 0x0012, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private delegate void WinEventProcedure(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Point Point; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProcedure callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal sealed class NoComputerForegroundMonitor : IComputerForegroundMonitor
{
    public long Revision => 0;
    public event Action? UnexpectedForeground { add { } remove { } }
    public void Expect(string? windowId) { }
    public void AllowMissum(bool allow) { }
    public Task<bool> WaitForExpectedAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    public void Dispose() { }
}

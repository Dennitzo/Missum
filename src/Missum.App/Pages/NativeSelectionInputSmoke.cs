using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace Missum.App.Pages;

/// <summary>Real pointer input restricted to the current isolated smoke window.</summary>
internal sealed partial class NativeSelectionInputSmoke : IDisposable
{
    private const uint LeftDownFlag = 0x0002;
    private const uint LeftUpFlag = 0x0004;
    private const uint WheelFlag = 0x0800;
    private const uint MoveFlag = 0x0001;
    private const uint MoveNoCoalesceFlag = 0x2000;
    private const uint VirtualDesktopFlag = 0x4000;
    private const uint AbsoluteFlag = 0x8000;
    private const int HideWindow = 0;
    private const int RestoreWindow = 9;
    private readonly Window _window;
    private readonly nint _windowHandle;
    private readonly nint _originalForeground;
    private readonly NativePoint _originalCursor;
    private readonly bool _originallyVisible;
    private bool _shownForSmoke;
    private bool _leftPressed;
    private bool _disposed;

    internal NativeSelectionInputSmoke()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Real pointer smoke input requires an isolated smoke instance.");
        if (IntPtr.Size != 8 || Marshal.SizeOf<NativeInput>() != 40)
            throw new PlatformNotSupportedException("The pointer smoke helper requires the x64 INPUT layout.");

        _window = App.Current.MainWindow
            ?? throw new InvalidOperationException("The smoke window is not available.");
        _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _ = GetWindowThreadProcessId(_windowHandle, out var processId);
        if (_windowHandle == 0 || processId != (uint)Environment.ProcessId)
            throw new InvalidOperationException("The pointer smoke window must belong to this process.");
        _originalForeground = GetForegroundWindow();
        _originallyVisible = IsWindowVisible(_windowHandle) != 0;
        if (GetCursorPos(out _originalCursor) == 0)
            throw NativeFailure("Reading the original cursor position failed.");
    }

    internal int Moves { get; private set; }
    internal int Wheels { get; private set; }
    internal int ButtonDowns { get; private set; }
    internal int ButtonUps { get; private set; }

    internal async Task EnsureForegroundAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsWindowVisible(_windowHandle) == 0)
        {
            // The smoke process normally starts hidden. Reveal only our verified
            // QA HWND while its real mouse gestures require an onscreen surface.
            _ = ShowWindow(_windowHandle, RestoreWindow);
            _shownForSmoke = true;
            await Task.Delay(20);
            (_window.Content as FrameworkElement)?.UpdateLayout();
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        var started = Stopwatch.GetTimestamp();
        do
        {
            ActivateOwnWindow();
            if (GetForegroundWindow() == _windowHandle)
            {
                await Task.Delay(20);
                (_window.Content as FrameworkElement)?.UpdateLayout();
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (GetForegroundWindow() == _windowHandle) return;
            }
            // WinUI activation completes through its UI queue. Yield rather than
            // blocking that queue while Windows applies the foreground change.
            await Task.Delay(20);
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
        VerifyForeground();
    }

    private async Task EnsureInputForegroundAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetForegroundWindow() == _windowHandle) return;
        // Idle layout/wait steps can lose foreground between gestures. Acquire
        // only our verified QA HWND before starting the next input operation.
        // A held gesture must fail instead: activation cannot restore its capture.
        if (_leftPressed) VerifyForeground();
        await EnsureForegroundAsync();
    }

    internal async Task MoveToAsync(FrameworkElement target, Point localPoint)
    {
        ArgumentNullException.ThrowIfNull(target);
        await EnsureInputForegroundAsync();
        VerifyForeground();
        if (_window.Content is not UIElement content || target.XamlRoot is null
            || target.XamlRoot != content.XamlRoot)
            throw new InvalidOperationException("The pointer target must be in the smoke window's XAML root.");
        var point = target.TransformToVisual(content).TransformPoint(localPoint);
        var scale = target.XamlRoot.RasterizationScale;
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !double.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("The pointer target has invalid layout coordinates.");
        var clientPoint = new NativePoint(
            checked((int)Math.Round(point.X * scale)),
            checked((int)Math.Round(point.Y * scale)));
        if (GetClientRect(_windowHandle, out var rect) == 0)
            throw NativeFailure("Reading the smoke window's client bounds failed.");
        if (!Contains(rect, clientPoint))
            throw new InvalidOperationException("Pointer smoke input cannot leave the smoke window's client area.");
        if (ClientToScreen(_windowHandle, ref clientPoint) == 0)
            throw NativeFailure("Converting the smoke pointer coordinates failed.");
        var desktopX = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        var desktopY = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        var desktopWidth = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        var desktopHeight = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (desktopWidth <= 0 || desktopHeight <= 0)
            throw new InvalidOperationException("The smoke virtual desktop has invalid bounds.");
        // Aim at each physical pixel's center. Absolute SendInput coordinates
        // map 0..65535 across the virtual desktop, including negative origins.
        var absoluteX = AbsoluteCoordinate(clientPoint.X, desktopX, desktopWidth);
        var absoluteY = AbsoluteCoordinate(clientPoint.Y, desktopY, desktopHeight);
        // SetCursorPos changes position but can omit WinUI's held-drag move
        // packet. Inject a real, uncoalesced MOVE for the actual pointer path.
        SendMouse(MoveFlag | MoveNoCoalesceFlag | VirtualDesktopFlag | AbsoluteFlag,
            dx: absoluteX, dy: absoluteY);
        Moves++;
    }

    internal async Task LeftDownAsync()
    {
        await EnsureInputForegroundAsync();
        VerifyForeground();
        if (_leftPressed) throw new InvalidOperationException("The smoke pointer's left button is already pressed.");
        VerifyCursorInClient();
        SendMouse(LeftDownFlag);
        _leftPressed = true;
        ButtonDowns++;
    }

    internal async Task LeftUpAsync()
    {
        await EnsureInputForegroundAsync();
        LeftUp();
    }

    private void LeftUp()
    {
        VerifyForeground();
        if (!_leftPressed) return;
        SendMouse(LeftUpFlag);
        _leftPressed = false;
        ButtonUps++;
    }

    internal async Task WheelAsync(int delta)
    {
        await EnsureInputForegroundAsync();
        VerifyForeground();
        if (delta == 0) return;
        VerifyCursorInClient();
        SendMouse(WheelFlag, unchecked((uint)delta));
        Wheels++;
    }

    public void Dispose()
    {
        if (_disposed) return;
        var restoreForeground = false;
        try
        {
            // A pressed synthetic button must be released, including on smoke failure.
            // Reacquire only our own QA window before sending that release.
            if (_leftPressed)
            {
                if (GetForegroundWindow() != _windowHandle) ActivateOwnWindow();
                LeftUp();
            }
            restoreForeground = GetForegroundWindow() == _windowHandle;
        }
        finally
        {
            if (!_originallyVisible && _shownForSmoke && IsWindow(_windowHandle) != 0)
                _ = ShowWindow(_windowHandle, HideWindow);
            // Hiding the QA HWND can itself change foreground. Decide whether
            // restoration is permitted before hiding, while ownership is known.
            if (restoreForeground)
            {
                _ = SetCursorPos(_originalCursor.X, _originalCursor.Y);
                if (_originalForeground != 0 && _originalForeground != _windowHandle
                    && IsWindow(_originalForeground) != 0)
                    _ = SetForegroundWindow(_originalForeground);
            }
            _disposed = true;
        }
    }

    private void ActivateOwnWindow()
    {
        _window.Activate();
        _ = SetForegroundWindow(_windowHandle);
        if (GetForegroundWindow() == _windowHandle) return;

        // Foreground policy can reject activation from an automation process.
        // Temporarily join only the foreground input queue, activate our own
        // window, and always detach; no keyboard input is sent to that window.
        var foreground = GetForegroundWindow();
        if (foreground == 0) return;
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == currentThread
            || AttachThreadInput(currentThread, foregroundThread, 1) == 0)
            return;
        try
        {
            _window.Activate();
            _ = SetForegroundWindow(_windowHandle);
        }
        finally
        {
            _ = AttachThreadInput(currentThread, foregroundThread, 0);
        }
    }

    private void VerifyForeground()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (GetForegroundWindow() != _windowHandle)
            throw new InvalidOperationException("Pointer smoke input stopped because its own window is not foreground.");
    }

    private void VerifyCursorInClient()
    {
        if (GetCursorPos(out var cursor) == 0 || GetClientRect(_windowHandle, out var rect) == 0)
            throw NativeFailure("Reading the smoke pointer's client position failed.");
        var origin = new NativePoint(0, 0);
        if (ClientToScreen(_windowHandle, ref origin) == 0)
            throw NativeFailure("Reading the smoke window's screen position failed.");
        var point = new NativePoint(cursor.X - origin.X, cursor.Y - origin.Y);
        if (!Contains(rect, point))
            throw new InvalidOperationException("Pointer smoke input cannot act outside its own client area.");
    }

    private void SendMouse(uint flags, uint mouseData = 0, int dx = 0, int dy = 0)
    {
        VerifyForeground();
        var input = new NativeInput
        {
            Type = 0,
            Mouse = new NativeMouseInput { Flags = flags, MouseData = mouseData, Dx = dx, Dy = dy },
        };
        if (SendInput(1, in input, 40) != 1)
            throw NativeFailure("Sending the smoke pointer input failed.");
    }

    private static bool Contains(NativeRect rect, NativePoint point) =>
        point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;

    private static int AbsoluteCoordinate(int coordinate, int origin, int extent) =>
        (int)Math.Clamp(Math.Round(((double)coordinate - origin + 0.5) * 65536 / extent), 0, 65535);

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastPInvokeError(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    // Native INPUT's union starts at byte 8 on x64, and MOUSEINPUT is 32 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public NativeMouseInput Mouse;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetClientRect(nint window, out NativeRect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ClientToScreen(nint window, ref NativePoint point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint SendInput(uint count, in NativeInput inputs, int size);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AttachThreadInput(uint sourceThread, uint targetThread, int attach);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();
}

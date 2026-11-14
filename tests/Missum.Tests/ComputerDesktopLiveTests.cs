using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class ComputerDesktopLiveTests
{
    // Explicit opt-in because this test temporarily raises a window and injects real input.
    // Build tests/Missum.ComputerControl.TestTarget first and provide its executable path.
    [Fact]
    public async Task ControlsOnlyTheOwnedTestWindow()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_LIVE") != "1") return;
        // The production WinUI executable declares PerMonitorV2 in its manifest;
        // the xUnit host does not. Set process awareness before creating windows so
        // that all continuation threads use physical desktop/control coordinates.
        if (!SetProcessDpiAwarenessContext((nint)(-4)))
            throw new InvalidOperationException("Der isolierte Testprozess konnte PerMonitorV2-DPI-Awareness nicht setzen.");
        var executable = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_TARGET");
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new InvalidOperationException("MISSUM_COMPUTER_TEST_TARGET muss auf das gebaute Testfenster zeigen.");
        var title = "Missum Computer Test " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        using var target = Process.Start(start) ?? throw new InvalidOperationException("Testfenster konnte nicht gestartet werden.");
        try
        {
            nint window = 0, edit = 0, button = 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline && !target.HasExited)
            {
                window = FindWindow(null, title);
                edit = window == 0 ? 0 : FindWindowEx(window, 0, "Edit", null);
                button = window == 0 ? 0 : FindWindowEx(window, 0, "Button", null);
                if (window != 0 && edit != 0 && button != 0 && GetForegroundWindow() == window) break;
                await Task.Delay(100);
            }
            Assert.NotEqual((nint)0, window);
            Assert.NotEqual((nint)0, edit);
            Assert.NotEqual((nint)0, button);
            Assert.Equal(window, GetForegroundWindow());
            Assert.True(GetWindowRect(edit, out var rect));
            Assert.True(GetWindowRect(button, out var buttonRect));
            var approval = new LiveApproval();
            using var broker = new ComputerDesktopBroker(approval, new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance));
            var session = Guid.NewGuid(); var run = Guid.NewGuid().ToString("N");
            broker.Begin(session, run);
            var first = await broker.ObserveAsync(session, run, "window");
            Assert.Equal(target.Id, first.ForegroundProcessId);
            var region = Assert.IsType<ComputerImageRegion>(first.ImageRegion);
            var imageX = (rect.Left + 20 - region.X) * (double)first.Width / region.Width;
            var imageY = ((rect.Top + rect.Bottom) / 2 - region.Y) * (double)first.Height / region.Height;
            var buttonX = ((buttonRect.Left + buttonRect.Right) / 2 - region.X) * (double)first.Width / region.Width;
            var buttonY = ((buttonRect.Top + buttonRect.Bottom) / 2 - region.Y) * (double)first.Height / region.Height;
            Assert.True(imageX >= 0 && imageY >= 0 && imageX < first.Width && imageY < first.Height,
                $"Test-Geometrie: image={first.Width}x{first.Height}, desktop=({first.DesktopX},{first.DesktopY},{first.DesktopWidth},{first.DesktopHeight}), "
                + $"region={region}, target={first.Windows.FirstOrDefault(item => item.ProcessId == target.Id)}, "
                + $"edit=({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom}), button=({buttonRect.Left},{buttonRect.Top})-({buttonRect.Right},{buttonRect.Bottom}), point=({imageX},{imageY})");
            var inspected = await broker.ExecuteAsync(session, run, new(first.ObservationId, "inspect", X: imageX, Y: imageY, CoordinateSpace: "pixels"));
            Assert.NotNull(inspected.Element);
            Assert.False(inspected.Element.IsPassword);
            var focusBeforeManual = FocusFor(window);
            var second = await broker.ObserveAsync(session, run, "window");
            approval.Decision = ComputerActionDecision.ManualDone;
            var manual = await broker.ExecuteAsync(session, run, new(second.ObservationId, "click", X: buttonX, Y: buttonY, CoordinateSpace: "pixels"));
            Assert.Equal("manual", manual.Status);
            Assert.Equal(focusBeforeManual, FocusFor(window));
            Assert.Equal(1, approval.Requests);
            var third = await broker.ObserveAsync(session, run, "window");
            approval.Decision = ComputerActionDecision.Execute;
            Assert.Equal("completed", (await broker.ExecuteAsync(session, run, new(third.ObservationId, "click", X: buttonX, Y: buttonY, CoordinateSpace: "pixels"))).Status);
            Assert.Equal(button, FocusFor(window));
            Assert.Equal(2, approval.Requests);
            var fourth = await broker.ObserveAsync(session, run, "window");
            Assert.Equal("completed", (await broker.ExecuteAsync(session, run, new(fourth.ObservationId, "click", X: imageX, Y: imageY, CoordinateSpace: "pixels"))).Status);
            var fifth = await broker.ObserveAsync(session, run, "window");
            Assert.Equal("completed", (await broker.ExecuteAsync(session, run, new(fifth.ObservationId, "type", Text: "Missum Test"))).Status);
            Assert.Equal(2, approval.Requests);
            var buffer = new char[256];
            _ = SendMessage(edit, 0x000D, (nuint)buffer.Length, buffer);
            Assert.Equal("Missum Test", new string(buffer).TrimEnd('\0'));
            broker.End(session, run);
        }
        finally
        {
            if (!target.HasExited) { target.Kill(entireProcessTree: true); await target.WaitForExitAsync(); }
        }
    }

    private sealed class LiveApproval : IComputerActionApproval
    {
        public ComputerActionDecision Decision { get; set; }
        public int Requests { get; private set; }
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        { Requests++; return Task.FromResult(Decision); }
    }

    private static nint FocusFor(nint window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint window, uint message, nuint wParam, [Out] char[] buffer);
}

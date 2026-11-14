using System.Diagnostics;
using System.Runtime.InteropServices;
using Missum.App.Services;

namespace Missum.Tests;

/// <summary>Opt-in foreground hook check using two windows created solely for this test.</summary>
public sealed class ComputerForegroundLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task RealWindowsSwitchPausesAndReturnInvalidatesObservation()
    {
        if (Environment.GetEnvironmentVariable("MISSUM_COMPUTER_FOREGROUND_LIVE") != "1") return;
        var executable = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_TEST_TARGET")!;
        var signal = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_FOREGROUND_SIGNAL")!;
        var id = Guid.NewGuid().ToString("N");
        using var first = Start(executable, "Missum Pause Test A " + id);
        using var second = Start(executable, "Missum Pause Test B " + id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            var a = await WindowAsync(first, timeout.Token);
            var b = await WindowAsync(second, timeout.Token);
            await File.WriteAllTextAsync(signal, "activate-a", timeout.Token);
            await UntilAsync(() => GetForegroundWindow() == a, timeout.Token);
            using var monitor = new WindowsComputerForegroundMonitor();
            monitor.Expect($"window-{a:X}");
            var observedRevision = monitor.Revision;
            await File.WriteAllTextAsync(signal, "activate-b", timeout.Token);
            await UntilAsync(() => GetForegroundWindow() == b && monitor.Revision > observedRevision, timeout.Token);
            var waiting = monitor.WaitForExpectedAsync(timeout.Token);
            await Task.Delay(300, timeout.Token);
            Assert.False(waiting.IsCompleted);
            await File.WriteAllTextAsync(signal, "return-a", timeout.Token);
            Assert.True(await waiting);
            Assert.Equal(a, GetForegroundWindow());
            Assert.True(monitor.Revision > observedRevision);
            await File.WriteAllTextAsync(signal, "complete", timeout.Token);
        }
        finally
        {
            if (!first.HasExited) { first.Kill(entireProcessTree: true); await first.WaitForExitAsync(); }
            if (!second.HasExited) { second.Kill(entireProcessTree: true); await second.WaitForExitAsync(); }
        }
    }

    private static Process Start(string executable, string title)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.Environment["MISSUM_COMPUTER_TEST_WINDOW"] = title;
        return Process.Start(start) ?? throw new InvalidOperationException("Testfenster konnte nicht gestartet werden.");
    }

    private static async Task<nint> WindowAsync(Process process, CancellationToken token)
    {
        nint handle = 0;
        await UntilAsync(() => { process.Refresh(); handle = process.MainWindowHandle; return handle != 0; }, token);
        return handle;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(100, token);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}

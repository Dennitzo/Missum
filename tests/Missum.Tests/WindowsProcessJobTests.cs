using Missum.Core.Coding;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Missum.Tests;

public sealed class WindowsProcessJobTests
{
    private static readonly TimeSpan ProcessDeadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public async Task ProcessThatExitedBeforeAssignmentKeepsItsExitCodeAndOutput(int exitCode)
    {
        using var job = WindowsProcessJob.Create();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/d");
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("echo EXIT_STDOUT& echo EXIT_STDERR 1>&2& exit /b " + exitCode.ToString(CultureInfo.InvariantCulture));
        Assert.True(process.Start());
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            // Make the launch/assignment race deterministic, without relying on CPU timing.
            await process.WaitForExitAsync().WaitAsync(ProcessDeadline);
            Assert.True(process.HasExited);

            job.Assign(process);

            Assert.Equal(exitCode, process.ExitCode);
            Assert.Equal("EXIT_STDOUT", (await stdout.WaitAsync(ProcessDeadline)).Trim());
            Assert.Equal("EXIT_STDERR", (await stderr.WaitAsync(ProcessDeadline)).Trim());
        }
        finally { await StopOwnedProcessAsync(process); }
    }

    [Fact]
    public async Task AssigningLiveProcessStillOwnsItsLifetime()
    {
        using var job = WindowsProcessJob.Create();
        using var process = await StartWaitingProcessAsync();
        try
        {
            job.Assign(process);
            Assert.False(process.HasExited);

            job.Dispose();

            await process.WaitForExitAsync().WaitAsync(ProcessDeadline);
            Assert.True(process.HasExited);
        }
        finally
        {
            job.Dispose();
            await StopOwnedProcessAsync(process);
        }
    }

    [Fact]
    public async Task ActiveProcessLimitFailureIsNotMistakenForAnAlreadyCompletedCommand()
    {
        using var job = WindowsProcessJob.Create(new CodingProcessLimits(0, 1));
        using var first = await StartWaitingProcessAsync();
        Process? second = null;
        try
        {
            second = await StartWaitingProcessAsync();
            job.Assign(first);
            Assert.False(first.HasExited);
            Assert.False(second.HasExited);

            // Assignment must fail regardless of whether the inherited job hierarchy
            // terminates the rejected process or leaves it running for caller cleanup.
            var failure = Assert.Throws<Win32Exception>(() => job.Assign(second));
            Assert.NotEqual(0, failure.NativeErrorCode);
            Assert.False(first.HasExited);
        }
        finally
        {
            job.Dispose();
            try
            {
                if (second is not null) await StopOwnedProcessAsync(second);
            }
            finally
            {
                second?.Dispose();
                await StopOwnedProcessAsync(first);
            }
        }
    }

    private static async Task<Process> StartWaitingProcessAsync()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add("[Console]::WriteLine('READY'); [Threading.Thread]::Sleep(60000)");
        Assert.True(process.Start());
        try
        {
            Assert.Equal("READY", await process.StandardOutput.ReadLineAsync().WaitAsync(ProcessDeadline));
            Assert.False(process.HasExited);
            return process;
        }
        catch
        {
            try { await StopOwnedProcessAsync(process); }
            finally { process.Dispose(); }
            throw;
        }
    }

    private static async Task StopOwnedProcessAsync(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync().WaitAsync(ProcessDeadline);
    }
}

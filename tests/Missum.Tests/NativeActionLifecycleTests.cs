using System.Diagnostics;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class NativeActionLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledCaptionStartReleasesStateBeforeAndAfterClientCreation(bool beforeClientCreation)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            MissumAiServerUrl = "http://127.0.0.1:65000",
        });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        CancellationTokenSource? currentCancellation = null;
        var attempts = 0;
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () =>
        {
            attempts++;
            var cancellation = currentCancellation ?? throw new InvalidOperationException("No test attempt is active.");
            if (beforeClientCreation)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return new CancelCaptionRequestHandler(cancellation);
        });
        using var captions = new SystemAudioCaptionService(connection, settings, NullLogger<SystemAudioCaptionService>.Instance);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            currentCancellation = cancellation;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => captions.StartAsync(LiveCaptionMode.Transcribe, cancellation.Token));
            Assert.False(captions.IsRunning);
            Assert.False(captions.Current.IsActive);
            Assert.Equal("Abgebrochen", captions.Current.Status);
            Assert.False((await captions.StopAsync()).IsActive);
            captions.ClearCompleted();
            Assert.Null(captions.Current.StartedAt);
        }
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PdfRendererCancellationAndTimeoutWaitForActualProcessTermination(bool cancelCaller)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 60" })
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        try
        {
            using var cancellation = new CancellationTokenSource();
            var wait = DocumentPdfExporter.WaitForExportExitAsync(process,
                cancelCaller ? TimeSpan.FromMinutes(1) : TimeSpan.FromMilliseconds(50), cancellation.Token);
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
            }
            else await Assert.ThrowsAsync<TimeoutException>(() => wait);
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class CancelCaptionRequestHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real caption client reaches its create-session transport, but the
            // test never connects to a gateway or opens a system-audio capture device.
            Assert.Contains("caption", request.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}

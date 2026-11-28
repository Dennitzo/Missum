using System.Text.Json;
using Missum.App.Services;

namespace Missum.Tests;

/// <summary>
/// Live Set-of-Mark capture of the real desktop (MISSUM_COMPUTER_MARKS_LIVE=&lt;output folder&gt;).
/// Writes the marked observation image and the element list for visual inspection.
/// </summary>
public sealed class ComputerSetOfMarkLiveTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    [Trait("Category", "Live")]
    public async Task RealDesktopObservationCarriesNumberedElements()
    {
        var output = Environment.GetEnvironmentVariable("MISSUM_COMPUTER_MARKS_LIVE");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var platform = new WindowsComputerDesktopPlatform(new DesktopScreenshotService(Microsoft.Extensions.Logging.Abstractions.NullLogger<DesktopScreenshotService>.Instance));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var frame = await platform.CaptureAsync(CancellationToken.None);
        var elapsed = watch.ElapsedMilliseconds;
        var observation = frame.Public;
        await File.WriteAllBytesAsync(Path.Combine(output, "observation.png"), observation.Content);
        await File.WriteAllTextAsync(Path.Combine(output, "observation.json"), JsonSerializer.Serialize(new
        {
            elapsedMs = elapsed, observation.Width, observation.Height, observation.ForegroundTitle, app = observation.App,
            windows = observation.Windows.Select(window => new { window.Id, window.Title, window.Process }),
            elements = observation.Elements,
            uiaError = ComputerUiAutomation.LastError?.ToString(),
        }, Indented));
        Assert.NotNull(frame.Targets);
        Assert.True(observation.Width <= 1920 && observation.Height <= 1920);
    }
}

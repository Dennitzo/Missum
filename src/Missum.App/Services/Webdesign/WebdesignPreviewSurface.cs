using System.Text.Json;

namespace Missum.App.Services.Webdesign;

/// <summary>A rendered picture of the website tab plus what is needed to aim at it.</summary>
public sealed record WebdesignCapture(
    byte[] Png,
    int PixelWidth,
    int PixelHeight,
    double CssWidth,
    double CssHeight,
    double ScrollX,
    double ScrollY,
    string Url,
    string Device,
    JsonElement Elements);

/// <summary>One input event for the website, in CSS pixels of the page viewport.</summary>
public sealed record WebdesignInput(
    string Kind,
    double X = 0,
    double Y = 0,
    double EndX = 0,
    double EndY = 0,
    double ScrollX = 0,
    double ScrollY = 0,
    string? Text = null,
    string? Key = null,
    int ClickCount = 1);

/// <summary>
/// The website tab as seen by tools: AI preview tools and computer control talk to this, the
/// WinUI control implements it and does all WebView2 work on the UI thread.
/// </summary>
public interface IWebdesignPreviewSurface
{
    string? Workspace { get; }
    string? CurrentUrl { get; }
    string Device { get; }
    Task ShowAsync(CancellationToken cancellationToken);
    Task NavigateAsync(string url, CancellationToken cancellationToken);
    Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken);
    Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken);
    Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken);
    Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken);
    Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken);
}

/// <summary>Where tools find the live website tab. The page registers it while Webdesign is shown.</summary>
public static class WebdesignPreviewRegistry
{
    private static IWebdesignPreviewSurface? _current;

    public static IWebdesignPreviewSurface? Current => Volatile.Read(ref _current);

    public static void Register(IWebdesignPreviewSurface surface) => Volatile.Write(ref _current, surface);

    public static void Unregister(IWebdesignPreviewSurface surface) =>
        Interlocked.CompareExchange(ref _current, null, surface);

    /// <summary>The tab of the given project, or a clear error when it is not open.</summary>
    public static IWebdesignPreviewSurface For(string? workspace)
    {
        var surface = Current ?? throw new InvalidOperationException(
            "Der Tab Webseite ist nicht geöffnet. Öffne die Webdesign-Sitzung des Projekts in Missum.");
        if (workspace is not null && surface.Workspace is { } shown
            && !string.Equals(WebdesignServeService.Normalize(shown), WebdesignServeService.Normalize(workspace), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Tab Webseite zeigt gerade ein anderes Projekt. Öffne die Sitzung dieses Projekts.");
        return surface;
    }
}

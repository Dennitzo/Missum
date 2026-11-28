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
    JsonElement Elements,
    string? Title = null);

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
    /// <summary>The browser runs and the project's server is ready, so captures and inspection work.</summary>
    bool IsReady => true;
    Task ShowAsync(CancellationToken cancellationToken);
    Task NavigateAsync(string url, CancellationToken cancellationToken);
    Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken);
    Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken);
    Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken);
    Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken);
    Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken);
    /// <summary>Goes one page back in the tab's history (browser.use "back").</summary>
    Task GoBackAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Der Tab Webseite kann nicht zurücknavigieren.");
}

/// <summary>
/// Where tools find the live website tab. The page registers it while the open Codex session has the
/// Webdesign tool switched on.
/// </summary>
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
            "Der Tab Webseite ist nicht geöffnet. Öffne die Codex-Sitzung des Projekts in Missum und schalte das Werkzeug Webdesign ein.");
        if (workspace is not null && surface.Workspace is { } shown
            && !string.Equals(WebdesignServeService.Normalize(shown), WebdesignServeService.Normalize(workspace), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Tab Webseite zeigt gerade ein anderes Projekt. Öffne die Sitzung dieses Projekts.");
        return surface;
    }

    /// <summary>
    /// The registered tab when it shows this project (any project for null), otherwise null. For
    /// in-app features that work on the website tab without the AI tool error texts.
    /// </summary>
    internal static IWebdesignPreviewSurface? TryGet(string? workspace)
    {
        var surface = Current;
        if (surface is null) return null;
        if (workspace is null) return surface;
        return surface.Workspace is { } shown
            && string.Equals(WebdesignServeService.Normalize(shown), WebdesignServeService.Normalize(workspace), StringComparison.OrdinalIgnoreCase)
                ? surface : null;
    }

    /// <summary>
    /// Waits, bounded, until the tab of this project is registered (and with <paramref name="ready"/>
    /// also shows the running server). Returns false on timeout; the caller's tool reports the reason.
    /// </summary>
    internal static async Task<bool> WaitForAsync(string workspace, bool ready, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryGet(workspace) is { } surface && (!ready || surface.IsReady)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The model used webdesign.* in a Codex session whose Webdesign tool was off. The tool service has
/// switched it on in the database; the page refreshes so chip and "Webseite" tab appear and the tab
/// registers its preview surface.
/// </summary>
public static class WebdesignToolActivation
{
    public static event Action<Guid>? Activated;

    internal static void Raise(Guid sessionId) => Activated?.Invoke(sessionId);
}

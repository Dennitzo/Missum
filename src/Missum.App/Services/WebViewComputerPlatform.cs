using System.Text.Json;
using Missum.App.Services.Webdesign;

namespace Missum.App.Services;

/// <summary>What a website observation needs to aim an action and to notice a changed page.</summary>
internal sealed record WebViewFrameState(string Url, double CssPerPixelX, double CssPerPixelY, double ScrollX, double ScrollY);

/// <summary>
/// Computer control inside Missum's "Webseite" tab. It sees the page as rendered by the tab
/// (screenshot plus a list of visible controls) and acts through the WebView's DevTools input,
/// so neither the user's mouse nor window focus is involved, and Missum's own-process lock for
/// desktop input does not apply. An action is refused when the page or its scroll position
/// changed since the observation; the model then observes again.
/// </summary>
internal sealed class WebViewComputerPlatform(IWebdesignPreviewSurface surface) : IComputerDesktopPlatform
{
    public IWebdesignPreviewSurface Surface { get; } = surface;

    public async Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken)
    {
        await Surface.ShowAsync(cancellationToken).ConfigureAwait(false);
        var capture = await Surface.CaptureAsync(null, cancellationToken).ConfigureAwait(false);
        if (capture.PixelWidth <= 0 || capture.PixelHeight <= 0)
            throw new InvalidOperationException("Der Tab Webseite lieferte kein Bild.");
        var state = new WebViewFrameState(capture.Url, capture.CssWidth / capture.PixelWidth, capture.CssHeight / capture.PixelHeight,
            capture.ScrollX, capture.ScrollY);
        var observation = new ComputerObservation(
            "web-" + Guid.NewGuid().ToString("N"), capture.Png, "image/png", capture.PixelWidth, capture.PixelHeight,
            0, 0, capture.PixelWidth, capture.PixelHeight, "Webseite · " + capture.Url, Environment.ProcessId, [],
            Url: capture.Url, Viewport: new ComputerViewport(capture.CssWidth, capture.CssHeight, capture.ScrollX, capture.ScrollY, capture.Device),
            Elements: ScaleElements(capture.Elements, capture.PixelWidth / Math.Max(1, capture.CssWidth)));
        return new ComputerFrame(observation, [], null, 0, null, DateTimeOffset.UtcNow, WebView: state);
    }

    public Task<ComputerFrame> CaptureWindowAsync(CancellationToken cancellationToken) => CaptureAsync(cancellationToken);

    /// <summary>The element list in image pixels, so the model can aim with the same numbers it sees.</summary>
    private static JsonElement ScaleElements(JsonElement elements, double pixelsPerCss)
    {
        if (elements.ValueKind != JsonValueKind.Array) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        var scaled = elements.EnumerateArray().Select(element => new
        {
            role = Text(element, "role"), name = Text(element, "name"), selector = Text(element, "selector"),
            x = Math.Round(Number(element, "x") * pixelsPerCss), y = Math.Round(Number(element, "y") * pixelsPerCss),
            width = Math.Round(Number(element, "width") * pixelsPerCss), height = Math.Round(Number(element, "height") * pixelsPerCss),
            disabled = element.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True,
        }).ToArray();
        return JsonSerializer.SerializeToElement(scaled);
    }

    public async Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken)
    {
        var state = frame.WebView ?? throw new InvalidOperationException("Die Beobachtung stammt nicht aus dem Tab Webseite.");
        await EnsureUnchangedAsync(state, cancellationToken).ConfigureAwait(false);
        var kind = action.Kind.ToLowerInvariant();
        var input = kind switch
        {
            "move" or "click" or "double_click" or "scroll" => new WebdesignInput(kind, Css(action.X, state.CssPerPixelX), Css(action.Y, state.CssPerPixelY),
                ScrollY: action.ScrollY ?? 0),
            "drag" => new WebdesignInput(kind, Css(action.X, state.CssPerPixelX), Css(action.Y, state.CssPerPixelY),
                Css(action.EndX, state.CssPerPixelX), Css(action.EndY, state.CssPerPixelY)),
            "type" => new WebdesignInput(kind, Text: action.Text),
            "keypress" => new WebdesignInput(kind, Key: action.Key),
            "wait" => new WebdesignInput(kind),
            "focus" => throw new InvalidOperationException("Im Tab Webseite gibt es keine Fenster; nutze click auf das gewünschte Element."),
            _ => throw new InvalidOperationException("Diese Aktion gibt es im Tab Webseite nicht."),
        };
        await Surface.DispatchAsync(input, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken)
    {
        var state = frame.WebView ?? throw new InvalidOperationException("Die Beobachtung stammt nicht aus dem Tab Webseite.");
        var element = await Surface.ElementAtAsync(Css(action.X, state.CssPerPixelX), Css(action.Y, state.CssPerPixelY), cancellationToken).ConfigureAwait(false);
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("An dieser Stelle liegt kein Element der Webseite.");
        return ToElement(element);
    }

    /// <summary>
    /// Why an action needs the user's approval, or null when it stays inside the local page:
    /// passwords, file choosers and links to other sites are the user's decision.
    /// </summary>
    public async Task<string?> RiskAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken)
    {
        var kind = action.Kind.ToLowerInvariant();
        if (kind == "type" || kind == "keypress")
        {
            if (Surface is Controls.WebdesignPreviewView view)
            {
                var active = await view.ActiveElementAsync().ConfigureAwait(false);
                if (active.ValueKind == JsonValueKind.Object && active.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True)
                    return "Eingabe in ein Passwortfeld";
            }
            return null;
        }
        if (kind is not ("click" or "double_click")) return null;
        var state = frame.WebView!;
        var element = await Surface.ElementAtAsync(Css(action.X, state.CssPerPixelX), Css(action.Y, state.CssPerPixelY), cancellationToken).ConfigureAwait(false);
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (element.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.True) return "Dateiauswahl öffnen";
        if (element.TryGetProperty("password", out var secret) && secret.ValueKind == JsonValueKind.True) return "Passwortfeld anklicken";
        var href = Text(element, "href");
        if (href.Length > 0 && Uri.TryCreate(new Uri(state.Url), href, out var target)
            && target.Scheme is "http" or "https" && Uri.TryCreate(state.Url, UriKind.Absolute, out var current)
            && !string.Equals(target.Authority, current.Authority, StringComparison.OrdinalIgnoreCase))
            return "Link zu einer anderen Webseite: " + target.Host;
        return null;
    }

    private async Task EnsureUnchangedAsync(WebViewFrameState state, CancellationToken cancellationToken)
    {
        if (!string.Equals(Surface.CurrentUrl, state.Url, StringComparison.Ordinal))
            throw new InvalidOperationException("Die Webseite hat sich seit der Beobachtung geändert. Beobachte erneut.");
        var (scrollX, scrollY) = await Surface.ScrollAsync(cancellationToken).ConfigureAwait(false);
        if (Math.Abs(scrollX - state.ScrollX) > 2 || Math.Abs(scrollY - state.ScrollY) > 2)
            throw new InvalidOperationException("Die Webseite wurde seit der Beobachtung gescrollt. Beobachte erneut.");
    }

    internal static ComputerElement ToElement(JsonElement element)
    {
        var role = Text(element, "role");
        var tag = Text(element, "tag");
        var controlType = role switch
        {
            "button" => 50000, "checkbox" => 50002, "combobox" => 50003, "textbox" => 50004, "link" => 50005,
            "img" => 50006, "radio" => 50013, "slider" => 50015, "heading" => 50020,
            _ => tag is "p" or "span" or "h1" or "h2" or "h3" or "li" ? 50020 : 50026,
        };
        var name = Text(element, "text");
        return new ComputerElement(name.Length == 0 ? tag : name, controlType, Environment.ProcessId,
            !(element.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True),
            element.TryGetProperty("password", out var password) && password.ValueKind == JsonValueKind.True,
            Text(element, "selector"));
    }

    private static double Css(double? pixels, double cssPerPixel) => (pixels ?? 0) * cssPerPixel;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}

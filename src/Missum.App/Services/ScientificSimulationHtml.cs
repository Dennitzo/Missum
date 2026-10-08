using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Missum.App.Services;

/// <summary>Standalone interactive research documents, with no access to the host application.</summary>
internal static partial class ScientificSimulationHtml
{
    // The native WebView2 document limit includes the base64 encoding of the iframe source.
    internal const int MaximumBytes = 1024 * 1024;
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; connect-src 'none'; base-uri 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; sandbox allow-scripts";

    internal static bool IsHtmlPath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    internal static string HostDocumentUri(string document) =>
        "data:text/html;charset=utf-8;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(document));

    internal static bool IsHostNavigationAllowed(string uri, string? expectedDocumentUri) =>
        uri is "about:blank" or "about:srcdoc"
        || expectedDocumentUri is not null && string.Equals(uri, expectedDocumentUri, StringComparison.Ordinal);

    internal static bool IsInteractiveDocument(string source) => source.Length > 0
        && Encoding.UTF8.GetByteCount(source) <= MaximumBytes
        && HtmlDocument().IsMatch(source) && ScriptElement().IsMatch(source)
        && VisualizationElement().IsMatch(source);

    internal static string Title(string source, string fallback)
    {
        var match = TitleElement().Match(source);
        var title = match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : fallback;
        return title.Length > 200 ? title[..200] : title;
    }

    internal static string NativeDocument(string source)
    {
        if (!IsInteractiveDocument(source)) throw new InvalidDataException("Die HTML-Simulation ist ungültig oder zu groß.");
        var protectedSource = "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\""
            + WebUtility.HtmlEncode(ContentSecurityPolicy) + "\">" + source;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(protectedSource));
        // The frame has an opaque origin. No folder mapping, file URL, host object,
        // web bridge, popup permission or network connection is exposed to the document.
        return "<!doctype html><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; frame-src about:; base-uri 'none'\">"
            + "<style>html,body,iframe{width:100%;height:100%;margin:0;border:0;overflow:hidden;background:transparent}</style>"
            + "<iframe title=\"Interaktive Simulation\" sandbox=\"allow-scripts\"></iframe><script>"
            + "const bytes=Uint8Array.from(atob('" + encoded + "'),c=>c.charCodeAt(0));"
            + "document.querySelector('iframe').srcdoc=new TextDecoder().decode(bytes);</script>";
    }

    [GeneratedRegex(@"<!doctype\s+html\b|<html(?:\s|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlDocument();
    [GeneratedRegex(@"<script(?:\s|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptElement();
    [GeneratedRegex(@"<(?:canvas|svg)(?:\s|>)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisualizationElement();
    [GeneratedRegex(@"<title\b[^>]*>([^<]{0,1000})</title\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleElement();
}

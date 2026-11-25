using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Missum.App.Services.Webdesign;

/// <summary>
/// Built-in file server of a Webdesign project. It serves only below the project root, never
/// .git/.missum/node_modules, without caching. An empty project gets a placeholder from memory.
/// HTML responses carry a tiny live-reload script for external browsers (smartphone via LAN);
/// inside Missum's own tab the native view reloads instead and the script stays idle.
/// </summary>
internal sealed class WebdesignStaticServer : IAsyncDisposable
{
    internal const string LiveScriptPath = "/__missum/live.js";
    internal const string EventsPath = "/__missum/events";
    private static readonly string[] HiddenSegments = [".git", ".missum", "node_modules", ".vs", ".idea"];
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly string _root;
    private readonly List<Channel<string>> _listeners = [];
    private readonly Lock _listenersLock = new();
    private WebApplication? _app;

    private WebdesignStaticServer(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public int Port { get; private set; }

    /// <summary>
    /// Starts on the preferred port when it is usable, otherwise on a port Windows assigns.
    /// With <paramref name="lan"/> the server also answers other devices in the local network.
    /// </summary>
    public static async Task<WebdesignStaticServer> StartAsync(string root, int? preferredPort, bool lan,
        IReadOnlySet<int> taken, CancellationToken cancellationToken)
    {
        var server = new WebdesignStaticServer(root);
        if (preferredPort is { } preferred && !taken.Contains(preferred) && WebdesignPorts.IsFree(preferred))
        {
            try
            {
                await server.StartOnAsync(preferred, lan, cancellationToken).ConfigureAwait(false);
                return server;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException
                or System.Net.Sockets.SocketException) { await server.StopAppAsync().ConfigureAwait(false); }
        }
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await server.StartOnAsync(0, lan, cancellationToken).ConfigureAwait(false);
            if (!taken.Contains(server.Port) && !WebdesignPorts.IsReserved(server.Port)) return server;
            await server.StopAppAsync().ConfigureAwait(false);
        }
        throw new InvalidOperationException("Es wurde kein freier Port für den Webdesign-Server gefunden.");
    }

    private async Task StartOnAsync(int port, bool lan, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "Missum.Webdesign" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // Any covers loopback as well; two listeners on port 0 would get two different ports.
            if (lan) options.Listen(IPAddress.Any, port);
            else options.Listen(IPAddress.Loopback, port);
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 1024;
        });
        builder.Services.AddRouting();
        var app = builder.Build();
        app.MapGet("/__missum/health", () => Results.Json(new { ok = true, service = "missum-webdesign" }));
        app.MapGet(LiveScriptPath, () => Results.Text(LiveScript, "text/javascript", Encoding.UTF8));
        app.MapGet(EventsPath, StreamEventsAsync);
        app.MapMethods("/{**path}", ["GET", "HEAD"], ServeAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Der Webdesign-Server meldete keine Adresse.");
        Port = new Uri(address.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)).Port;
        _app = app;
    }

    /// <summary>Tells connected external browsers that a file changed.</summary>
    public void Broadcast(string relativePath, string kind)
    {
        var message = System.Text.Json.JsonSerializer.Serialize(new { path = relativePath, kind });
        lock (_listenersLock)
            foreach (var listener in _listeners) listener.Writer.TryWrite(message);
    }

    private async Task StreamEventsAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "text/event-stream";
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_listenersLock) _listeners.Add(channel);
        try
        {
            await context.Response.WriteAsync(": connected\n\n", context.RequestAborted).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            {
                await context.Response.WriteAsync("data: " + message + "\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_listenersLock) _listeners.Remove(channel); }
    }

    private async Task ServeAsync(HttpContext context, string? path)
    {
        context.Response.Headers.CacheControl = "no-store";
        var resolved = Resolve(_root, path);
        if (resolved is null)
        {
            await WriteHtmlAsync(context, StatusCodes.Status404NotFound, Page("Nicht freigegeben",
                "Dieser Pfad liegt außerhalb des Projekts oder in einem geschützten Ordner.")).ConfigureAwait(false);
            return;
        }
        if (Directory.Exists(resolved))
        {
            if (!(path ?? "").EndsWith('/') && !string.IsNullOrEmpty(path))
            {
                context.Response.Redirect("/" + path!.TrimStart('/') + "/");
                return;
            }
            var index = Path.Combine(resolved, "index.html");
            if (!File.Exists(index))
            {
                await WriteHtmlAsync(context, StatusCodes.Status200OK, Placeholder(resolved)).ConfigureAwait(false);
                return;
            }
            resolved = index;
        }
        if (!File.Exists(resolved))
        {
            await WriteHtmlAsync(context, StatusCodes.Status404NotFound, Page("Seite nicht gefunden",
                $"„/{WebUtility.HtmlEncode(path)}“ gibt es in diesem Projekt noch nicht.")).ConfigureAwait(false);
            return;
        }
        if (!ContentTypes.TryGetContentType(resolved, out var contentType)) contentType = "application/octet-stream";
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            string html;
            try { html = await File.ReadAllTextAsync(resolved, context.RequestAborted).ConfigureAwait(false); }
            catch (IOException) { context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            await WriteHtmlAsync(context, StatusCodes.Status200OK, InjectLiveScript(html)).ConfigureAwait(false);
            return;
        }
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || contentType is "application/javascript")
            contentType += "; charset=utf-8";
        await Results.File(resolved, contentType, enableRangeProcessing: true).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a request path to a file below the root. Rejects "..", rooted paths, hidden folders
    /// and reparse points, so a request can never leave the project.
    /// </summary>
    internal static string? Resolve(string root, string? requestPath)
    {
        var relative = Uri.UnescapeDataString((requestPath ?? "").Replace('\\', '/')).Trim('/');
        var parts = relative.Length == 0 ? [] : relative.Split('/');
        if (parts.Any(part => part is "" or "." or ".." || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)) return null;
        if (parts.Any(part => HiddenSegments.Contains(part, StringComparer.OrdinalIgnoreCase))) return null;
        var full = Path.GetFullPath(Path.Combine([root, .. parts]));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return null;
        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    return null;
            }
            catch (IOException) { return null; }
        }
        return full;
    }

    internal static string InjectLiveScript(string html)
    {
        const string tag = "<script src=\"" + LiveScriptPath + "\" defer></script>";
        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close >= 0 ? html.Insert(close, tag) : html + tag;
    }

    private static async Task WriteHtmlAsync(HttpContext context, int status, string html)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Folder without index.html: the placeholder, plus links to pages that already exist.</summary>
    private static string Placeholder(string directory)
    {
        var pages = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.htm*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true })
                .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
                .Where(file => !file.Split('/').Any(part => HiddenSegments.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .Order(StringComparer.OrdinalIgnoreCase).Take(40).ToArray()
            : [];
        var body = pages.Length == 0
            ? "Beschreibe im Chat, wie deine Webseite aussehen soll. Sobald Missum die erste Seite anlegt, erscheint sie hier automatisch."
            : "Es gibt noch keine Startseite (index.html). Vorhandene Seiten:<ul>"
              + string.Concat(pages.Select(page => $"<li><a href=\"{WebUtility.HtmlEncode(page)}\">{WebUtility.HtmlEncode(page)}</a></li>")) + "</ul>";
        return InjectLiveScript(Page("Noch keine Seite", body));
    }

    private static string Page(string title, string body) => $$$"""
        <!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>{{{WebUtility.HtmlEncode(title)}}} · Missum Webdesign</title>
        <style>
        :root{color-scheme:light dark;--bg:#f6f5fb;--fg:#24213a;--muted:#6b6785;--accent:#7c5cff}
        @media (prefers-color-scheme:dark){:root{--bg:#1b1830;--fg:#ecebf5;--muted:#a3a0bd}}
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--fg);font:16px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif}
        main{max-width:520px;padding:32px;text-align:center}
        .mark{width:56px;height:56px;margin:0 auto 18px;border-radius:16px;background:linear-gradient(135deg,var(--accent),#3ad0c5);opacity:.9}
        h1{font-size:22px;margin:0 0 8px}p,li{color:var(--muted)}ul{text-align:left;display:inline-block}a{color:var(--accent)}
        </style></head><body><main><div class="mark"></div><h1>{{{WebUtility.HtmlEncode(title)}}}</h1><p>{{{body}}}</p></main></body></html>
        """;

    private const string LiveScript = """
        (() => {
          if (window.chrome && window.chrome.webview) return; // Missum's own tab reloads natively.
          if (!window.EventSource) return;
          const source = new EventSource("/__missum/events");
          source.onmessage = event => {
            let change; try { change = JSON.parse(event.data); } catch { return; }
            if (change.kind === "css") {
              for (const link of document.querySelectorAll('link[rel="stylesheet"]')) {
                const url = new URL(link.href, location.href); url.searchParams.set("missum", Date.now()); link.href = url.toString();
              }
              return;
            }
            sessionStorage.setItem("missum-scroll", String(window.scrollY));
            location.reload();
          };
          const saved = sessionStorage.getItem("missum-scroll");
          if (saved) { sessionStorage.removeItem("missum-scroll"); addEventListener("load", () => scrollTo(0, Number(saved))); }
        })();
        """;

    private async Task StopAppAsync()
    {
        var app = Interlocked.Exchange(ref _app, null);
        if (app is null) return;
        try { await app.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_listenersLock)
            foreach (var listener in _listeners) listener.Writer.TryComplete();
        await StopAppAsync().ConfigureAwait(false);
    }
}

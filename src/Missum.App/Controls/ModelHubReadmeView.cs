using System.Globalization;
using System.Net;
using Markdig;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Missum.App.Controls;

/// <summary>
/// Shows a Hugging Face model card the way the website does: GitHub-flavoured markdown with
/// raw HTML, rendered by Markdig into a sandboxed WebView2. The view grows with its content
/// (the surrounding page scrolls); links open in the default browser.
/// </summary>
public sealed partial class ModelHubReadmeView : Grid
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseEmojiAndSmiley().Build();
    private WebView2? _browser;
    private CoreWebView2? _core;
    private string? _pendingHtml;
    private bool _initializing;

    public ModelHubReadmeView()
    {
        MinHeight = 120;
        AutomationProperties.SetName(this, "Modellbeschreibung von Hugging Face");
    }

    /// <summary>Height of the rendered card in device-independent pixels, reported by the page.</summary>
    public double ContentHeight { get; private set; }

    /// <summary>A vertical wheel turn over the card, in pixels; the page around it scrolls instead.</summary>
    public event Action<double>? WheelScrolled;
    public bool Rendered { get; private set; }

    public async Task ShowAsync(string repo, string commit, string markdown, Windows.UI.Color background, Windows.UI.Color text)
    {
        Rendered = false;
        _pendingHtml = BuildHtml(repo, commit, markdown, background, text);
        RenderedBackground = Css(background);
        if (_browser is not null) _browser.DefaultBackgroundColor = background;
        if (_core is null)
        {
            if (_initializing) return;
            _initializing = true;
            try
            {
                var profile = Path.Combine(App.Current.DataDirectory, "WebView2", "ModelHub");
                Directory.CreateDirectory(profile);
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, profile, null);
                var browser = new WebView2 { DefaultBackgroundColor = background };
                _browser = browser;
                Children.Add(browser);
                await browser.EnsureCoreWebView2Async(environment);
                _core = browser.CoreWebView2;
                Configure(_core);
            }
            finally { _initializing = false; }
        }
        _core?.NavigateToString(_pendingHtml);
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsWebMessageEnabled = true;
        core.NavigationStarting += (_, args) => Guard(() =>
        {
            // Only the card itself is shown; every link opens in the default browser.
            if (args.Uri.StartsWith("data:", StringComparison.Ordinal) || args.Uri.StartsWith("about:", StringComparison.Ordinal)) return;
            args.Cancel = true;
            OpenExternal(args.Uri);
        });
        core.NewWindowRequested += (_, args) => Guard(() => { args.Handled = true; OpenExternal(args.Uri); });
        core.PermissionRequested += (_, args) => Guard(() => args.State = CoreWebView2PermissionState.Deny);
        core.WebMessageReceived += (_, args) => Guard(() =>
        {
            var message = args.TryGetWebMessageAsString() ?? "";
            if (message.StartsWith("wheel:", StringComparison.Ordinal))
            {
                if (double.TryParse(message.AsSpan(6), NumberStyles.Float, CultureInfo.InvariantCulture, out var delta)) WheelScrolled?.Invoke(delta);
                return;
            }
            if (double.TryParse(message, NumberStyles.Float, CultureInfo.InvariantCulture, out var height) && height > 0)
            {
                ContentHeight = Math.Ceiling(height) + 8;
                Height = ContentHeight;
                Rendered = true;
            }
        });
    }

    /// <summary>Dispatches a wheel event inside the card, as a mouse would (smoke tests).</summary>
    internal async Task WheelForSmokeAsync(double deltaY)
    {
        if (_core is null) return;
        await _core.ExecuteScriptAsync("window.dispatchEvent(new WheelEvent('wheel', { deltaY: " + deltaY.ToString(CultureInfo.InvariantCulture) + ", cancelable: true }))");
    }

    internal string? RenderedBackground { get; private set; }

    private static string Css(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Windows.UI.Color Mix(Windows.UI.Color from, Windows.UI.Color to, double amount) => Windows.UI.Color.FromArgb(255,
        (byte)(from.R + (to.R - from.R) * amount), (byte)(from.G + (to.G - from.G) * amount), (byte)(from.B + (to.B - from.B) * amount));

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var target) && target.Scheme is "https" or "http")
            _ = Windows.System.Launcher.LaunchUriAsync(target);
    }

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { System.Diagnostics.Debug.WriteLine("ModelHub readme: " + exception); }
    }

    /// <summary>Markdown → a complete page: relative images load from the repository, relative links point to its file view.</summary>
    internal static string BuildHtml(string repo, string commit, string markdown, Windows.UI.Color background, Windows.UI.Color text)
    {
        var dark = (background.R * 299 + background.G * 587 + background.B * 114) / 1000 < 128;
        var body = Markdown.ToHtml(markdown, Pipeline);
        var resolve = $"https://huggingface.co/{repo}/resolve/{commit}/";
        var blob = $"https://huggingface.co/{repo}/blob/{commit}/";
        // The card takes Missum's background (Settings › Darstellung) and mixes its lines and code
        // blocks from it, so it reads as part of the page in every colour.
        var palette = $"--text:{Css(text)};--muted:{Css(Mix(text, background, .38))};--line:{Css(Mix(background, text, .16))};"
            + $"--code:{Css(Mix(background, text, .06))};--link:{(dark ? "#7cb4ff" : "#0969da")};--bg:{Css(background)}";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8">
            <base href="{{WebUtility.HtmlEncode(resolve)}}">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src https: data:; media-src https:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src https: data:">
            <style>
            :root{ {{palette}}; color-scheme:{{(dark ? "dark" : "light")}}; }
            html,body{margin:0;background:var(--bg);color:var(--text);}
            body{font:15px/1.6 "Segoe UI Variable Text","Segoe UI",sans-serif;padding:0 4px 8px 0;overflow:hidden;word-wrap:break-word;}
            h1,h2,h3,h4{line-height:1.25;margin:1.4em 0 .6em;font-weight:600}
            h1{font-size:1.75em;padding-bottom:.3em;border-bottom:1px solid var(--line)}
            h2{font-size:1.4em;padding-bottom:.3em;border-bottom:1px solid var(--line)}
            h3{font-size:1.15em}
            p,ul,ol,table,pre,blockquote{margin:0 0 1em}
            a{color:var(--link);text-decoration:none} a:hover{text-decoration:underline}
            img{max-width:100%;height:auto;border-radius:6px}
            code{font:13px Consolas,"Cascadia Mono",monospace;background:var(--code);padding:.15em .35em;border-radius:5px}
            pre{background:var(--code);padding:12px 14px;border-radius:10px;overflow:auto} pre code{padding:0;background:none}
            blockquote{margin-left:0;padding:0 1em;color:var(--muted);border-left:3px solid var(--line)}
            table{border-collapse:collapse;display:block;overflow:auto;max-width:100%}
            th,td{border:1px solid var(--line);padding:6px 12px} th{font-weight:600}
            hr{border:0;border-top:1px solid var(--line);margin:1.5em 0}
            details summary{cursor:pointer}
            </style></head><body>
            <article id="card">{{body}}</article>
            <script>
            (function(){
              var blob = {{System.Text.Json.JsonSerializer.Serialize(blob)}};
              document.querySelectorAll('a[href]').forEach(function(a){
                var raw = a.getAttribute('href');
                if(!raw || raw.charAt(0)==='#') return;
                if(!/^[a-z]+:/i.test(raw) && raw.charAt(0)!=='/') a.setAttribute('href', blob + raw);
                else if(raw.charAt(0)==='/') a.setAttribute('href', 'https://huggingface.co' + raw);
                a.setAttribute('target','_blank');
              });
              document.querySelectorAll('a[href^="#"]').forEach(function(a){
                a.addEventListener('click', function(e){ e.preventDefault(); var t=document.getElementById(a.getAttribute('href').slice(1)); if(t) t.scrollIntoView(); });
              });
              function report(){ window.chrome.webview.postMessage(String(document.documentElement.scrollHeight)); }
              // The card never scrolls itself: vertical wheel turns go to the page around it.
              window.addEventListener('wheel', function(e){
                if (Math.abs(e.deltaY) < Math.abs(e.deltaX)) return;
                e.preventDefault();
                var px = e.deltaMode === 1 ? e.deltaY * 40 : e.deltaMode === 2 ? e.deltaY * 800 : e.deltaY;
                window.chrome.webview.postMessage('wheel:' + px);
              }, { passive: false });
              new ResizeObserver(report).observe(document.body);
              window.addEventListener('load', report);
              report();
            })();
            </script></body></html>
            """;
    }
}

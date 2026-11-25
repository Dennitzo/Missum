using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.App.Services.Webdesign;

/// <summary>Runs the AI tools webdesign.serve and webdesign.preview for a Webdesign project.</summary>
public sealed class WebdesignToolService(MissumAiConnectionService connection, WebdesignServeService serve)
{
    public async Task<object> ExecuteAsync(ToolProposal proposal, string? workspace, Uri? gatewayOverride, CancellationToken token)
    {
        WorkspaceTools.Validate(proposal.Name, proposal.Arguments);
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
            throw new InvalidOperationException("Webdesign-Werkzeuge benötigen den Projektordner der Sitzung.");
        var args = proposal.Arguments;
        var operation = args.GetProperty("operation").GetString()!;
        return proposal.Name == WorkspaceTools.WebdesignServe
            ? await ServeAsync(operation, args, workspace, token).ConfigureAwait(false)
            : await PreviewAsync(operation, args, workspace, gatewayOverride, token).ConfigureAwait(false);
    }

    private async Task<object> ServeAsync(string operation, JsonElement args, string workspace, CancellationToken token)
    {
        switch (operation)
        {
            case "status":
                return Describe(serve.Get(workspace) ?? await serve.EnsureAsync(workspace, token).ConfigureAwait(false));
            case "start":
                return Describe(await serve.EnsureAsync(workspace, token).ConfigureAwait(false));
            case "restart":
                return Describe(await serve.RestartAsync(workspace, token).ConfigureAwait(false));
            case "stop":
                await serve.StopAsync(workspace).ConfigureAwait(false);
                return new { stopped = true, instruction = "Der Tab Webseite zeigt nichts mehr, bis der Server wieder läuft." };
            case "log":
                var lines = serve.Log(workspace);
                return new { lines = lines.TakeLast(120), total = lines.Count,
                    note = lines.Count == 0 ? "Der eingebaute statische Server schreibt kein Protokoll; nur Projekt-Dev-Server tun das." : null };
            case "configure":
                var state = await serve.ConfigureAsync(workspace, config => config with
                {
                    Root = Text(args, "root") ?? config.Root,
                    StartPage = Text(args, "startPage") ?? config.StartPage,
                    Kind = Text(args, "kind") ?? config.Kind,
                    Command = args.TryGetProperty("command", out _) ? Text(args, "command") : config.Command,
                }, token).ConfigureAwait(false);
                return Describe(state);
            default:
                throw new InvalidOperationException("Unbekannte Serve-Operation.");
        }
    }

    private async Task<object> PreviewAsync(string operation, JsonElement args, string workspace, Uri? gatewayOverride, CancellationToken token)
    {
        switch (operation)
        {
            case "annotations":
            {
                var open = WebdesignAnnotationStore.Shared.Open(workspace);
                return new
                {
                    open = open.Select(item => new
                    {
                        number = item.Number, item.Kind, item.Page, item.Device, item.ViewportWidth, item.Selector, item.Tag,
                        item.Text, item.Box, item.Comment, item.Styles, item.SourceHint,
                    }),
                    count = open.Count,
                    instruction = open.Count == 0 ? "Keine offenen Wünsche." : "Setze die Nummern um und melde sie danach mit resolve(ids, note).",
                };
            }
            case "resolve":
            {
                var ids = args.GetProperty("ids").EnumerateArray().Select(static id => id.GetInt32()).ToArray();
                var resolved = WebdesignAnnotationStore.Shared.Resolve(workspace, ids, Text(args, "note"));
                return new { resolved, notOpen = ids.Except(resolved), instruction = "Im Tab Webseite sind diese Nummern jetzt als erledigt markiert." };
            }
        }

        var state = await serve.EnsureAsync(workspace, token).ConfigureAwait(false);
        if (state.Status != "ready")
            throw new InvalidOperationException("Der Webserver des Projekts läuft nicht: " + state.Message + " Prüfe webdesign.serve log oder status.");
        var surface = WebdesignPreviewRegistry.For(workspace);
        switch (operation)
        {
            case "open":
            {
                var path = Text(args, "path")!;
                var url = serve.UrlFor(workspace, path)
                    ?? throw new InvalidOperationException($"„{path}“ liegt nicht im bedienten Ordner „{state.Root}“.");
                await surface.NavigateAsync(url, token).ConfigureAwait(false);
                return new { opened = true, url, instruction = "Die Seite ist im Tab Webseite geöffnet. Für eine Sichtprüfung capture und danach media.analyze verwenden." };
            }
            case "capture":
            {
                if (Text(args, "path") is { } path && serve.UrlFor(workspace, path) is { } target)
                    await surface.NavigateAsync(target, token).ConfigureAwait(false);
                var capture = await surface.CaptureAsync(Text(args, "device"), token).ConfigureAwait(false);
                var page = Uri.TryCreate(capture.Url, UriKind.Absolute, out var uri) ? uri.AbsolutePath.Trim('/') : "";
                var name = "webseite-" + (page.Length == 0 ? "start" : string.Concat(page.Select(static c => char.IsLetterOrDigit(c) ? c : '-')))
                    + "-" + capture.Device + ".png";
                var directory = Path.Combine(Path.GetTempPath(), "missum-webdesign-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, name.Length > 120 ? name[..100] + ".png" : name);
                try
                {
                    await File.WriteAllBytesAsync(file, capture.Png, token).ConfigureAwait(false);
                    using var client = gatewayOverride is null
                        ? await connection.CreateClientAsync(token).ConfigureAwait(false)
                        : await connection.CreateClientAsync(gatewayOverride, ensureProfileNativeRuntime: false, token).ConfigureAwait(false);
                    var upload = await client.UploadFileAsync(file, "image/png", cancellationToken: token).ConfigureAwait(false);
                    return new
                    {
                        uploadId = upload.UploadId, mediaType = "image/png", source = "webdesign", sha256 = upload.Sha256, upload.Length,
                        url = capture.Url, device = capture.Device, viewport = new { width = capture.CssWidth, height = capture.CssHeight },
                        instruction = "Die gerenderte Seite ist geladen, noch nicht analysiert. Rufe jetzt media.analyze mit dieser uploadId und einer konkreten Prüffrage auf.",
                    };
                }
                finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
            }
            case "console":
            {
                var entries = await surface.ConsoleAsync(token).ConfigureAwait(false);
                return new { url = surface.CurrentUrl, entries = entries.TakeLast(80), count = entries.Count,
                    instruction = entries.Count == 0 ? "Keine Fehler oder fehlgeschlagenen Anfragen seit dem letzten Laden." : null };
            }
            case "inspect":
                return new { url = surface.CurrentUrl, element = await surface.InspectAsync(Text(args, "selector")!, token).ConfigureAwait(false) };
            default:
                throw new InvalidOperationException("Unbekannte Vorschau-Operation.");
        }
    }

    private static object Describe(WebdesignServerState state) => new
    {
        status = state.Status, kind = state.Kind, port = state.Port, url = state.LocalUrl, lanUrl = state.LanUrl,
        root = state.Root, startPage = state.StartPage, empty = state.Empty, command = state.Command, message = state.Message,
        instruction = state.Status == "ready"
            ? "Der Tab Webseite zeigt diesen Server. Neue oder geänderte Seiten erscheinen dort automatisch."
            : "Der Server läuft nicht; prüfe message und webdesign.serve log.",
    };

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim() : null;
}

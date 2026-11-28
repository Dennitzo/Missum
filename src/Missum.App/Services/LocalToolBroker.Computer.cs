using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class LocalToolBroker
{
    internal void EndComputerRun(Guid sessionId, string runId)
    {
        computerDesktop?.End(sessionId, runId);
        computerApproval?.EndRun(runId);
    }

    private static void ValidateComputerArguments(string name, JsonElement arguments)
    {
        if (name == "computer.observe")
        {
            // Computersteuerung sees the Windows desktop only; the website tab belongs to webdesign.preview.
            ValidateProperties(arguments, [], ["scope"]);
            if (arguments.TryGetProperty("scope", out _)
                && ValidateString(arguments, "scope", 1, 16) is not ("desktop" or "window"))
                throw new InvalidDataException("Beobachtungsbereich muss desktop oder window sein.");
            return;
        }
        ValidateProperties(arguments, ["observationId", "kind"],
            ["observationId", "kind", "elementIndex", "x", "y", "endX", "endY", "imageWidth", "imageHeight", "text", "key",
                "scrollY", "scrollX", "windowId", "windowAction", "target", "arguments", "coordinateSpace"]);
        _ = ValidateString(arguments, "observationId", 1, 128);
        _ = ValidateString(arguments, "kind", 1, 32);
        if (arguments.TryGetProperty("text", out _)) _ = ValidateString(arguments, "text", 0, 10_000);
        if (arguments.TryGetProperty("key", out _)) _ = ValidateString(arguments, "key", 1, 100);
        if (arguments.TryGetProperty("windowId", out _)) _ = ValidateString(arguments, "windowId", 1, 128);
        if (arguments.TryGetProperty("windowAction", out _)) _ = ValidateString(arguments, "windowAction", 1, 16);
        if (arguments.TryGetProperty("target", out _)) _ = ValidateString(arguments, "target", 1, 1024);
        if (arguments.TryGetProperty("arguments", out _)) _ = ValidateString(arguments, "arguments", 0, 2048);
        if (arguments.TryGetProperty("coordinateSpace", out _)) _ = ValidateString(arguments, "coordinateSpace", 1, 32);
        _ = JsonSerializer.Deserialize<ComputerAction>(arguments, JsonOptions)
            ?? throw new InvalidDataException("Die Computeraktion ist ungültig.");
    }

    private async Task<ClientToolResult> ExecuteComputerToolAsync(ToolProposal proposal, Guid sessionId,
        Uri? gatewayOverride, CancellationToken token)
    {
        var broker = computerDesktop ?? throw new InvalidOperationException("Der Desktop-Broker ist nicht verfügbar.");
        broker.Begin(sessionId, proposal.RunId);
        if (proposal.Name == "computer.observe")
        {
            using var client = gatewayOverride is null
                ? await connection.CreateClientAsync(token).ConfigureAwait(false)
                : await connection.CreateClientAsync(gatewayOverride, ensureProfileNativeRuntime: false, token).ConfigureAwait(false);
            if (!ComputerLocalEndpoint.IsLocal(client.BaseAddress))
                throw new InvalidOperationException("Bildschirmbilder dürfen nur an ein lokales Missum-Gateway übertragen werden.");
            var scope = proposal.Arguments.TryGetProperty("scope", out var requestedScope) ? requestedScope.GetString()! : "desktop";
            var observation = await broker.ObserveAsync(sessionId, proposal.RunId, scope, token).ConfigureAwait(false);
            var transferId = await client.UploadComputerObservationAsync(proposal.RunId, proposal.ProposalId, observation.Content, token).ConfigureAwait(false);
            // Only an opaque, short-lived transfer reference is journalled. Never
            // put screenshot bytes or a base64 image into the durable tool result.
            // Compact window list: the id is what focus/window actions need; bounds are in the image.
            var windows = observation.Windows.Select(window => new { window.Id, window.Title, window.Process }).ToArray();
            return Result(proposal, "completed", new
            {
                observation.ObservationId, observationTransferId = transferId, observation.Width, observation.Height,
                observation.DesktopX, observation.DesktopY, observation.DesktopWidth, observation.DesktopHeight,
                observation.ForegroundTitle, observation.ForegroundProcessId, app = observation.App, windows, observation.ImageRegion,
                url = observation.Url, viewport = observation.Viewport, elements = observation.Elements,
                grid = observation.Grid
                    ? "Das Programm meldet kaum Bedienelemente; gelbe Rasterlinien mit Werten je 100 zeigen normalized1000-Koordinaten. "
                        + "Ziele dann mit x/y und coordinateSpace normalized1000 anhand des Rasters."
                    : null,
                instruction = observation.Url is null
                    ? "Nummerierte Rahmen im Bild entsprechen elements (i = elementIndex, Bildpixel x/y/w/h). Bevorzuge elementIndex statt Koordinaten; "
                        + "area popup markiert offene Menüs, taskbar die Taskleiste. Programme startest du mit launch, Fenster mit window."
                    : "Beobachtung des Tabs Webseite. elements nennt sichtbare Bedienelemente in Bildpixeln (x, y, width, height); "
                        + "elementIndex n wählt das n-te Element, sonst ziele auf deren Mitte.",
            });
        }
        var action = JsonSerializer.Deserialize<ComputerAction>(proposal.Arguments, JsonOptions)
            ?? throw new InvalidDataException("Die Computeraktion ist ungültig.");
        var result = await broker.ExecuteAsync(sessionId, proposal.RunId, action, token).ConfigureAwait(false);
        var completed = result.Status is "completed" or "manual";
        return Result(proposal, completed ? "completed" : "failed", result,
            completed ? null : "computer.action_not_executed", result.Message);
    }
}

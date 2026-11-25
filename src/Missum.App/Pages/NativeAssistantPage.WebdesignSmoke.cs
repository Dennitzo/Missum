using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.App.Services.Webdesign;

namespace Missum.App.Pages;

/// <summary>
/// MISSUM_SMOKE_UI_WEBDESIGN=1: drives the Webdesign mode end to end in the real app: empty
/// project with placeholder, automatic display of a written page, CSS swap without reload,
/// devices, LAN with QR code, marking, comment, frame, fine-tuning, done badges, computer
/// control on the tab, console errors and before/after. Writes window captures (including the
/// WebView) and native-webdesign-validation.json.
/// </summary>
public sealed partial class NativeAssistantPage
{
    private async Task VerifyWebdesignSmokeAsync()
    {
        if (App.Current.MainWindow is { } window)
            window.AppWindow.Resize(SmokeWindowSize(1640, 1000)); // logical size, also on high-DPI displays
        var report = new Dictionary<string, object?>();
        var checks = new Dictionary<string, object?>();
        var serve = App.Current.GetService<WebdesignServeService>();

        await NavigateAsync("mode.switch", new { chatMode = WebdesignMode });
        var project = Path.Combine(App.Current.DataDirectory, "SmokeWebdesign Portfolio");
        if (Directory.Exists(project)) Directory.Delete(project, true);
        Directory.CreateDirectory(project);
        await NavigateAsync("session.projectCreate", new { workspacePath = project, chatMode = WebdesignMode });
        await WaitForAsync(() => _mode == WebdesignMode && _websiteTabButton?.Parent is not null, "Tab Webseite fehlt");
        var view = WebsiteView();
        await WaitForAsync(() => view.ServerState is { Status: "ready" } && view.NavigationCount >= 1, "Server oder erste Seite kam nicht");
        OpenWebsiteView();
        await Task.Delay(800);
        var state = view.ServerState!;
        checks["port"] = state.Port;
        checks["portFree"] = !WebdesignPorts.IsReserved(state.Port);
        checks["placeholder"] = Text(await view.InspectAsync("h1", CancellationToken.None)) == "Noch keine Seite";
        checks["onlyConfigWritten"] = Directory.EnumerateFileSystemEntries(project).Select(Path.GetFileName).SequenceEqual([".missum"]);
        report["placeholder"] = await CaptureWebdesignAsync("webdesign-1-placeholder.png");

        SmokeStep("A second project never shares the port.");
        // A second project never shares the port.
        var other = Path.Combine(App.Current.DataDirectory, "SmokeWebdesign Zweitprojekt");
        Directory.CreateDirectory(other);
        checks["secondPort"] = (await serve.EnsureAsync(other)).Port;
        checks["portsDiffer"] = (int)checks["secondPort"]! != state.Port;
        await serve.StopAsync(other);

        SmokeStep("The AI writes a page: the tab shows it on its own, in the");
        // The AI writes a page: the tab shows it on its own, in the background with a dot.
        ShowChatView();
        RenderSessionTabs();
        Directory.CreateDirectory(Path.Combine(project, "css"));
        await File.WriteAllTextAsync(Path.Combine(project, "css", "site.css"),
            "body{font-family:Segoe UI,sans-serif;margin:0;background:#f4f2fb;color:#24213a}header{padding:48px;background:#7c5cff;color:#fff}"
            + "h1{margin:0;font-size:32px;color:#ffffff}main{padding:32px;display:grid;gap:16px}button{padding:12px 20px;border-radius:8px;border:0;background:#24213a;color:#fff;font-size:16px}"
            + "@media (max-width:600px){header{padding:24px}h1{font-size:24px}}");
        var before = view.NavigationCount;
        await File.WriteAllTextAsync(Path.Combine(project, "index.html"), """
            <!doctype html><html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Smoke Portfolio</title><link rel="stylesheet" href="css/site.css"></head>
            <body><header><h1>Smoke Portfolio</h1></header>
            <main><p class="intro">Willkommen auf der Testseite.</p>
            <button id="cta" onclick="this.textContent='Gesendet'">Jetzt buchen</button>
            <a href="https://example.org/">Externer Link</a><input type="password" id="secret" placeholder="Passwort"></main>
            <script src="js/fehlt.js"></script></body></html>
            """);
        await WaitUntilAsync(async () => view.NavigationCount > before && Text(await view.InspectAsync("h1", CancellationToken.None)) == "Smoke Portfolio",
            "Geschriebene Seite wurde nicht angezeigt");
        checks["pageShownAutomatically"] = true;
        checks["dotWhileHidden"] = _websiteDot?.Visibility == Visibility.Visible;
        var inspectorInChat = Inspector.Visibility;
        OpenWebsiteView();
        checks["dotClearedOnOpen"] = _websiteDot?.Visibility != Visibility.Visible;
        checks["sidebarHiddenOnWebsite"] = Inspector.Visibility == Visibility.Collapsed && InspectorColumn.Width.Value == 0;
        await Task.Delay(600);
        report["page"] = await CaptureWebdesignAsync("webdesign-2-page.png");

        SmokeStep("The tools float in a translucent pill: it moves, stays in");
        // The tools float in a translucent pill: it moves, stays inside the tab and collapses.
        var placed = view.OverlayPosition;
        checks["overlayPlacedBottom"] = placed.Top > 300;
        view.MoveOverlayForSmoke(-500, -500);
        checks["overlayClamped"] = view.OverlayPosition == (0, 0);
        view.MoveOverlayForSmoke(40, 40);
        await Task.Delay(300);
        report["overlayMoved"] = await CaptureWebdesignAsync("webdesign-2b-overlay-moved.png");
        view.CollapseOverlayForSmoke(true);
        await Task.Delay(300);
        checks["overlayCollapsed"] = view.OverlayCollapsed;
        report["overlayCollapsed"] = await CaptureWebdesignAsync("webdesign-2c-overlay-collapsed.png");
        view.CollapseOverlayForSmoke(false);
        view.MoveOverlayForSmoke(placed.Left, placed.Top);

        SmokeStep("Console: the missing script is reported.");
        // Console: the missing script is reported.
        var console = await view.ConsoleAsync(CancellationToken.None);
        checks["console404"] = console.Any(line => line.Contains("fehlt.js", StringComparison.Ordinal));

        SmokeStep("CSS changes are swapped in without a reload.");
        // CSS changes are swapped in without a reload.
        var navigations = view.NavigationCount;
        await File.WriteAllTextAsync(Path.Combine(project, "css", "site.css"),
            (await File.ReadAllTextAsync(Path.Combine(project, "css", "site.css"))).Replace("font-size:32px;color:#ffffff", "font-size:32px;color:#ffd166", StringComparison.Ordinal));
        await WaitUntilAsync(async () => await StyleAsync(view, "h1", "color") == "rgb(255, 209, 102)", "CSS wurde nicht übernommen");
        checks["cssWithoutReload"] = view.NavigationCount == navigations;

        SmokeStep("Devices.");
        // Devices.
        foreach (var device in new[] { "smartphone", "tablet", "laptop" })
        {
            await view.SetDeviceAsync(device);
            await Task.Delay(700);
            var capture = await view.CaptureAsync(null, CancellationToken.None);
            checks["viewport-" + device] = capture.CssWidth.ToString(CultureInfo.InvariantCulture) + "x" + capture.CssHeight.ToString(CultureInfo.InvariantCulture);
            report["device-" + device] = await CaptureWebdesignAsync($"webdesign-3-{device}.png");
            await File.WriteAllBytesAsync(Path.Combine(App.Current.DataDirectory, $"webdesign-3-{device}-page.png"), capture.Png);
        }
        checks["smartphoneWidth"] = (string)checks["viewport-smartphone"]! == "390x844";
        await view.SetDeviceAsync("free");

        SmokeStep("LAN with QR code.");
        // LAN with QR code.
        var lanState = await serve.ConfigureAsync(project, config => config with { Lan = true });
        checks["lanUrl"] = lanState.LanUrl;
        checks["lanPortKept"] = lanState.Port == state.Port;
        if (lanState.LanUrl is { } lanUrl)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            checks["lanReachable"] = (await http.GetStringAsync(lanUrl)).Contains("Smoke Portfolio", StringComparison.Ordinal);
            var qr = WebdesignQrCode.Encode(lanUrl).ToPng();
            await File.WriteAllBytesAsync(Path.Combine(App.Current.DataDirectory, "webdesign-4-qr.png"), qr);
        }
        await serve.ConfigureAsync(project, config => config with { Lan = false });
        await WaitForAsync(() => view.ServerState is { Status: "ready", Lan: false } && view.NavigationCount > navigations, "Server kam nach LAN nicht zurück");
        await Task.Delay(800);

        SmokeStep("Marking: a click in mark mode becomes wish #1 and a pictu");
        // Marking: a click in mark mode becomes wish #1 and a picture in the composer.
        var store = WebdesignAnnotationStore.Shared;
        // Wishes go straight to Missum; the smoke records them instead of calling the gateway.
        var submissions = new List<string>();
        _websiteSubmitOverride = submissions.Add;
        var attachmentsBefore = Items(_snapshot, "attachments").Length;
        var cta = await view.InspectAsync("#cta", CancellationToken.None);
        var (ctaX, ctaY) = Center(cta);
        await view.SetOverlayModeAsync("mark");
        await view.DispatchAsync(new WebdesignInput("click", ctaX, ctaY), CancellationToken.None);
        await Task.Delay(300);
        await view.DispatchAsync(new WebdesignInput("type", Text: "Button größer und grün"), CancellationToken.None);
        await view.DispatchAsync(new WebdesignInput("keypress", Key: "Enter"), CancellationToken.None);
        try { await WaitForAsync(() => store.Open(project).Any(item => item.Kind == "mark" && item.Selector == "#cta"), "Markierung kam nicht an"); }
        catch (TimeoutException)
        {
            var probe = await view.InspectAsync("#cta", CancellationToken.None);
            throw new TimeoutException("Markierung kam nicht an: " + string.Join(" | ", view.MessageDiagnostics) + " · open=" + store.Open(project).Count
                + " · cta=" + probe.GetRawText()[..Math.Min(200, probe.GetRawText().Length)] + $" · click={ctaX},{ctaY}");
        }
        var mark = store.Open(project).Single(item => item.Kind == "mark");
        checks["markSourceHint"] = mark.SourceHint;
        checks["markDidNotClick"] = Text(await view.InspectAsync("#cta", CancellationToken.None)) == "Jetzt buchen";
        await WaitForAsync(() => Items(_snapshot, "attachments").Length > attachmentsBefore, "Markierungsbild fehlt im Eingabefeld");
        checks["markAttachment"] = Items(_snapshot, "attachments").Select(item => S(item, "fileName")).LastOrDefault();

        SmokeStep("Frame: drag, describe, Enter.");
        // Frame: drag, describe, Enter.
        await view.SetOverlayModeAsync("region");
        await view.DispatchAsync(new WebdesignInput("drag", 20, 20, 360, 110), CancellationToken.None);
        await Task.Delay(300);
        await view.DispatchAsync(new WebdesignInput("type", Text: "Kopfbereich weniger hoch"), CancellationToken.None);
        await view.DispatchAsync(new WebdesignInput("keypress", Key: "Enter"), CancellationToken.None);
        await WaitForAsync(() => store.Open(project).Any(item => item.Kind == "region"), "Rahmen kam nicht an");

        SmokeStep("Fine-tuning: choose h1, preview a size, hand it over.");
        // Fine-tuning: choose h1, preview a size, hand it over.
        var h1 = await view.InspectAsync("h1", CancellationToken.None);
        var (h1X, h1Y) = Center(h1);
        await view.SetOverlayModeAsync("tune");
        await view.DispatchAsync(new WebdesignInput("click", h1X, h1Y), CancellationToken.None);
        await WaitForAsync(() => view.TuneSelector is not null, "Feintuning-Auswahl kam nicht an");
        // Move by dragging the frame on the page; the overlay row shows the result.
        await Task.Delay(300);
        await view.DispatchAsync(new WebdesignInput("drag", h1X, h1Y, h1X + 40, h1Y), CancellationToken.None);
        await WaitUntilAsync(async () => (await StyleAsync(view, "h1", "transform")).Contains("40", StringComparison.Ordinal), "Verschieben per Griff wirkte nicht");
        checks["tuneMoved"] = view.TuneReadout.Contains("Verschoben 40/0 px", StringComparison.Ordinal);
        checks["tunePreview"] = true;
        // Colour from the wheel: the top of the rim is red.
        view.PickColorForSmoke("color", 66, 6);
        await WaitUntilAsync(async () => (await StyleAsync(view, "h1", "color")).StartsWith("rgb(2", StringComparison.Ordinal), "Farbrad wirkte nicht");
        checks["wheelColor"] = await StyleAsync(view, "h1", "color");
        checks["wheelReadout"] = view.TuneReadout.Contains("Text #", StringComparison.Ordinal);
        report["tune"] = await CaptureWebdesignAsync("webdesign-5-tune.png");
        // Undo takes back one step per click: first the colour, the move stays.
        checks["undoDepth"] = view.UndoDepth;
        await view.UndoAsync();
        await WaitUntilAsync(async () => await StyleAsync(view, "h1", "color") == "rgb(255, 209, 102)", "Rückgängig stellte die Farbe nicht wieder her");
        checks["undoColor"] = (await StyleAsync(view, "h1", "transform")).Contains("40", StringComparison.Ordinal);
        await view.UndoAsync();
        await WaitUntilAsync(async () => await StyleAsync(view, "h1", "transform") == "none", "Rückgängig stellte die Position nicht wieder her");
        checks["undoMove"] = true;
        // Another element takes over the fine-tuning; the first one returns to its original look.
        await view.DispatchAsync(new WebdesignInput("drag", h1X, h1Y, h1X + 40, h1Y), CancellationToken.None);
        await WaitUntilAsync(async () => (await StyleAsync(view, "h1", "transform")).Contains("40", StringComparison.Ordinal), "Zweites Verschieben wirkte nicht");
        var introNow = await view.InspectAsync(".intro", CancellationToken.None);
        var (introX, introY) = Center(introNow);
        await view.DispatchAsync(new WebdesignInput("click", introX, introY), CancellationToken.None);
        await WaitForAsync(() => view.TuneSelector?.EndsWith(".intro", StringComparison.Ordinal) == true, "Feintuning wechselte nicht zum neuen Element");
        checks["retargetResetsOld"] = await StyleAsync(view, "h1", "transform") == "none";
        checks["retargetWithoutError"] = !view.MessageDiagnostics.Any(line => line.StartsWith("error", StringComparison.Ordinal) || line.StartsWith("wheel", StringComparison.Ordinal));
        checks["retargetDiagnostics"] = string.Join(" | ", view.MessageDiagnostics.Where(line => !line.StartsWith("message ", StringComparison.Ordinal)));
        await view.DispatchAsync(new WebdesignInput("click", h1X, h1Y), CancellationToken.None);
        await WaitForAsync(() => view.TuneSelector == "h1", "Feintuning kam nicht zurück zur Überschrift");
        await Task.Delay(300);
        await view.DispatchAsync(new WebdesignInput("drag", h1X, h1Y, h1X + 40, h1Y), CancellationToken.None);
        await WaitUntilAsync(async () => (await StyleAsync(view, "h1", "transform")).Contains("40", StringComparison.Ordinal), "Verschieben nach Wechsel wirkte nicht");
        await view.CommitTuneForSmokeAsync();
        // Switching fine-tuning off hides its row.
        await view.SetOverlayModeAsync("tune");
        await view.DispatchAsync(new WebdesignInput("click", h1X, h1Y), CancellationToken.None);
        await WaitForAsync(() => view.TuneRowVisible, "Feintuning-Zeile erschien nicht");
        await view.SetOverlayModeAsync("none");
        checks["tuneRowHiddenWhenOff"] = !view.TuneRowVisible;
        await WaitForAsync(() => store.Open(project).Any(item => item.Kind == "tune" && item.Styles?.Any(change => change.Property == "transform") == true), "Feintuning-Wunsch fehlt");
        checks["directSubmissions"] = submissions.Count;
        checks["markSentDirectly"] = submissions.Any(text => text.Contains("#1", StringComparison.Ordinal) && text.Contains("Button größer und grün", StringComparison.Ordinal));
        checks["tuneSentDirectly"] = submissions.Any(text => text.Contains("Feintuning: transform none → translate(40px, 0px) rotate(0deg)", StringComparison.Ordinal));
        checks["noSidePanel"] = true;
        checks["tuneReset"] = await StyleAsync(view, "h1", "transform") == "none";
        OpenWebsiteView();
        await view.SetOverlayModeAsync("none");
        checks["wishes"] = store.Open(project).Select(item => $"#{item.Number} {item.Kind}").ToArray();
        checks["promptBlock"] = MissumAiAssistantService.BuildWebdesignPrompt("Prüfen", project, false).Contains("#1 Element", StringComparison.Ordinal);

        SmokeStep("The AI reports #1 as done: the badge turns green.");
        // The AI reports #1 as done: the badge turns green.
        store.Resolve(project, [mark.Number], "Button vergrößert");
        await Task.Delay(1500);
        report["badges"] = await CaptureWebdesignAsync("webdesign-6-badges.png");

        SmokeStep("Computer control directly on the tab: observe, click the ");
        // Computer control directly on the tab: observe, click the button, check the result.
        var approvals = new SmokeApproval();
        using (var broker = new ComputerDesktopBroker(approvals, new SmokeNoDesktop(), webViewFactory: () => new WebViewComputerPlatform(view)))
        {
            var session = Guid.NewGuid();
            broker.Begin(session, "smoke-web");
            SmokeStep("computer step 1");
            var observation = await broker.ObserveAsync(session, "smoke-web", "webview");
            var button = observation.Elements!.Value.EnumerateArray().First(element => element.GetProperty("selector").GetString() == "#cta");
            var x = button.GetProperty("x").GetDouble() + button.GetProperty("width").GetDouble() / 2;
            var y = button.GetProperty("y").GetDouble() + button.GetProperty("height").GetDouble() / 2;
            SmokeStep("computer step 2");
            var result = await broker.ExecuteAsync(session, "smoke-web", new ComputerAction(observation.ObservationId, "click", x, y, CoordinateSpace: "pixels"));
            checks["computerClick"] = result.Status;
            checks["computerEffect"] = Text(await view.InspectAsync("#cta", CancellationToken.None)) == "Gesendet";
            checks["computerElements"] = observation.Elements.Value.GetArrayLength();
            var link = observation.Elements.Value.EnumerateArray().First(element => element.GetProperty("role").GetString() == "link");
            SmokeStep("computer step 3");
            observation = await broker.ObserveAsync(session, "smoke-web", "webview");
            SmokeStep("computer step 4");
            await broker.ExecuteAsync(session, "smoke-web", new ComputerAction(observation.ObservationId, "click",
                link.GetProperty("x").GetDouble() + 4, link.GetProperty("y").GetDouble() + 4, CoordinateSpace: "pixels"));
            checks["externalLinkAsked"] = approvals.Requests == 1 && approvals.LastTarget?.Contains("example.org", StringComparison.Ordinal) == true;
            checks["tabStillLocal"] = view.CurrentUrl?.StartsWith(state.LocalUrl, StringComparison.Ordinal) == true;
            broker.End(session, "smoke-web");
        }

        SmokeStep("Before/after around a change.");
        // Before/after around a change.
        await view.CaptureBeforeAsync();
        SmokeStep("before captured");
        await File.WriteAllTextAsync(Path.Combine(project, "index.html"),
            (await File.ReadAllTextAsync(Path.Combine(project, "index.html"))).Replace("Willkommen auf der Testseite.", "Neu: Termine ab sofort online buchbar.", StringComparison.Ordinal));
        SmokeStep("file written");
        await Task.Delay(1500);
        SmokeStep("after start");
        await view.CaptureAfterAsync();
        SmokeStep("after done");
        await WaitForAsync(() => view.CompareAvailable, "Vorher/Nachher wurde nicht bereit");
        view.ShowCompareForSmoke();
        await Task.Delay(600);
        report["compare"] = await CaptureWebdesignAsync("webdesign-7-compare.png");
        view.HideCompareForSmoke();

        SmokeStep("Light theme.");
        // Light theme.
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Light);
        await Task.Delay(800);
        report["light"] = await CaptureWebdesignAsync("webdesign-8-light.png");
        App.Current.ApplyTheme(Missum.Core.Models.AppTheme.Dark);

        SmokeStep("The chat gets the sidebar back as it was.");
        // The chat gets the sidebar back as it was.
        ShowChatView();
        RenderSessionTabs();
        checks["sidebarBackInChat"] = Inspector.Visibility == inspectorInChat;
        OpenWebsiteView();

        SmokeStep("Switching to Codex hides the tab, switching back restores");
        // Switching to Codex hides the tab, switching back restores it.
        await NavigateAsync("mode.switch", new { chatMode = "coding" });
        await WaitForAsync(() => _mode == "coding", "Wechsel zu Codex kam nicht an");
        await Task.Delay(600);
        checks["codexWithoutTab"] = _websiteTabButton?.Parent is null && BodyGrid.Visibility == Visibility.Visible && view.Visibility != Visibility.Visible;
        await NavigateAsync("mode.switch", new { chatMode = WebdesignMode });
        await WaitForAsync(() => _mode == WebdesignMode && _websiteTabButton?.Parent is not null, "Rückkehr zu Webdesign fehlgeschlagen");
        checks["webdesignBack"] = true;

        _websiteSubmitOverride = null;
        report["checks"] = checks;
        report["handlerDiagnostics"] = view.MessageDiagnostics.Where(line => !line.StartsWith("message ", StringComparison.Ordinal)).ToArray();
        await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-webdesign-validation.json"),
            JsonSerializer.Serialize(report, Missum.App.Services.Meetings.MeetingJson.Indented));
        var failed = checks.Where(pair => pair.Value is false).Select(pair => pair.Key).ToArray();
        if (failed.Length > 0 || checks["lanUrl"] is null)
            throw new InvalidOperationException("Webdesign smoke failed: " + string.Join(", ", failed) + " " + JsonSerializer.Serialize(checks));
    }

    private Windows.Graphics.SizeInt32 SmokeWindowSize(int width, int height)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        return new((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private static void SmokeStep(string step) =>
        File.AppendAllText(Path.Combine(App.Current.DataDirectory, "webdesign-progress.txt"), DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + step + Environment.NewLine);

    private static string Text(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";

    private static (double X, double Y) Center(JsonElement element)
    {
        var view = element.GetProperty("view");
        return (view.GetProperty("x").GetDouble() + view.GetProperty("width").GetDouble() / 2,
            view.GetProperty("y").GetDouble() + view.GetProperty("height").GetDouble() / 2);
    }

    private static async Task<string> StyleAsync(WebdesignPreviewView view, string selector, string property)
    {
        var element = await view.InspectAsync(selector, CancellationToken.None);
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty("styles", out var styles)
            && styles.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(failure);
            await Task.Delay(150);
        }
    }

    /// <summary>Window capture with PrintWindow, so the WebView content is part of the picture.</summary>
    private async Task<object> CaptureWebdesignAsync(string fileName)
    {
        LayoutRoot.UpdateLayout();
        await Task.Delay(250);
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        var screenshots = new DesktopScreenshotService(NullLogger<DesktopScreenshotService>.Instance);
        var target = screenshots.ListTargets().FirstOrDefault(item => item.Id == "window-" + handle.ToString("X", CultureInfo.InvariantCulture));
        if (target is null) return await CaptureMeetingsAsync(fileName);
        var shot = await screenshots.CaptureAsync(target, CancellationToken.None);
        var path = Path.Combine(App.Current.DataDirectory, fileName);
        await File.WriteAllBytesAsync(path, shot.Content);
        return new { path, shot.Width, shot.Height };
    }

    private sealed class SmokeApproval : IComputerActionApproval
    {
        public int Requests { get; private set; }
        public string? LastTarget { get; private set; }
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            LastTarget = request.TargetTitle;
            // The smoke refuses: the external link must not be followed.
            return Task.FromResult(ComputerActionDecision.Cancel);
        }
    }

    private sealed class SmokeNoDesktop : IComputerDesktopPlatform
    {
        public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Kein Desktop im Smoke.");
        public Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}

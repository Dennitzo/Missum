using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Controls;
using Missum.App.Services;
using Missum.App.Services.Webdesign;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

// xUnit calls IAsyncLifetime.DisposeAsync, which stops every server of the test class.
#pragma warning disable CA1001
[Collection(WebdesignTestGroup.Name)]
public sealed class WebdesignModeTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly string _root = Directory.CreateTempSubdirectory("missum-webdesign-tests-").FullName;
    private readonly WebdesignServeService _serve = new(NullLogger<WebdesignServeService>.Instance);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _serve.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Project(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    // ----- Codex tool (former mode) -----------------------------------------------------------

    [Fact]
    public async Task TheLegacyWebdesignModeCreatesCodexSessionsWithTheTool()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var codex = await chats.CreateSessionAsync("Codex", ChatMode.Coding);
        var web = await chats.CreateSessionAsync("Portfolio", ChatMode.Webdesign);
        Assert.Equal(ChatMode.Coding, web.ChatMode);
        Assert.True(web.WebdesignEnabled);
        Assert.Empty(await chats.ListSessionsAsync(ChatMode.Webdesign));
        Assert.Equal([web.Id, codex.Id], (await chats.ListSessionsAsync(ChatMode.Coding)).Select(item => item.Id).OrderBy(id => id == codex.Id));
        await chats.SetCodingWorkspacePathAsync(web.Id, environment.Directory);
        var group = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.Coding));
        Assert.Equal(ChatMode.Coding, group.ChatMode);
        Assert.True((await chats.GetSessionAsync(web.Id))!.WebdesignEnabled);
        Assert.False((await chats.GetSessionAsync(codex.Id))!.WebdesignEnabled);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v62-*.bak"));
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }

    [Theory]
    [InlineData("webdesign")]
    [InlineData("Web-Design")]
    [InlineData("web")]
    public void LegacyWireNamesOpenCodex(string value) =>
        Assert.Equal(ChatMode.Coding, AssistantCoordinator.ParseChatMode(value));

    [Fact]
    public void TheToolRunsAsCodexAndKeepsPageChecksAwayFromDesktopControl()
    {
        var now = DateTimeOffset.UtcNow;
        var webdesign = new ChatSession(Guid.NewGuid(), "Portfolio", now, now, ChatMode.Coding, WebdesignEnabled: true);
        var codex = webdesign with { WebdesignEnabled = false };
        Assert.True(AssistantCoordinator.IsCodingAgentSession(webdesign.ChatMode));
        Assert.True(AssistantCoordinator.IsWebdesignToolActive(webdesign));
        Assert.True(AssistantCoordinator.IsWebdesignToolActive(webdesign with { ChatMode = ChatMode.Webdesign, WebdesignEnabled = false }));
        Assert.False(AssistantCoordinator.IsWebdesignToolActive(codex));
        // The flag means nothing outside Codex (ChatGPT never gets webdesign tools).
        Assert.False(AssistantCoordinator.IsWebdesignToolActive(webdesign with { ChatMode = ChatMode.General }));
        var trigger = AssistantCoordinator.CreateToolMatch("coding", "Klicke auf Kontakt");
        Assert.True(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, trigger, ChatMode.Coding));
        Assert.True(MissumAiAssistantService.ShouldClassifyComputerRoute(PromptTriggerAction.Coding, trigger, codex));
        Assert.False(MissumAiAssistantService.ShouldClassifyComputerRoute(PromptTriggerAction.Coding, trigger, webdesign));
    }

    [Fact]
    public void ThePromptTellsTheModelToCaptureTheTabAndInspectItBeforeClaimingAVisualResult()
    {
        var prompt = MissumAiAssistantService.BuildWebdesignPrompt("Mach die Überschrift größer.", Project("vision"), planMode: false);
        Assert.StartsWith("Du arbeitest in Codex mit dem Werkzeug Webdesign", prompt, StringComparison.Ordinal);
        Assert.Contains("webdesign.preview capture nimmt die gerenderte Seite im Tab Webseite auf (nicht den Desktop", prompt, StringComparison.Ordinal);
        Assert.Contains("Rufe danach media.analyze mit genau\ndieser uploadId", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("Nach jeder sichtbaren Änderung nimmst du erneut capture auf", prompt, StringComparison.Ordinal);
        Assert.Contains("Behaupte ein sichtbares Ergebnis nie ohne diese Prüfung", prompt, StringComparison.Ordinal);
        Assert.Contains("Computersteuerung sieht nur den\nWindows-Desktop", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Missum-Modus", prompt, StringComparison.Ordinal);
        Assert.Contains("Im Planmodus prüfst du nur", MissumAiAssistantService.BuildWebdesignPrompt("Prüfen", Project("vision-plan"), planMode: true), StringComparison.Ordinal);
        // The short hint of auto-detected runs names both tools and the same capture → media.analyze check.
        Assert.Contains("webdesign.serve", MissumAiAssistantService.WebdesignToolHint, StringComparison.Ordinal);
        Assert.Contains("webdesign.preview capture", MissumAiAssistantService.WebdesignToolHint, StringComparison.Ordinal);
        Assert.Contains("media.analyze", MissumAiAssistantService.WebdesignToolHint, StringComparison.Ordinal);
        Assert.InRange(MissumAiAssistantService.WebdesignToolHint.Length, 100, 700);
    }

    [Fact]
    public void ThePromptNamesServerToolsAndOpenWishes()
    {
        var workspace = Project("prompt");
        WebdesignAnnotationStore.Shared.Add(workspace, new WebdesignAnnotation
        {
            Kind = "mark", Page = "/", Device = "smartphone", ViewportWidth = 390, Selector = "#cta", Tag = "button",
            Text = "Jetzt buchen", Comment = "größer", SourceHint = "index.html:12",
        });
        var prompt = MissumAiAssistantService.BuildWebdesignPrompt("Mach den Button auffälliger.", workspace, planMode: false);
        Assert.Contains("webdesign.serve", prompt, StringComparison.Ordinal);
        Assert.Contains("nie mit coding.command", prompt, StringComparison.Ordinal);
        Assert.Contains("#1 Element auf / (smartphone, 390 px breit); Selektor #cta", prompt, StringComparison.Ordinal);
        Assert.EndsWith("Auftrag: Mach den Button auffälliger.", prompt, StringComparison.Ordinal);
    }

    // ----- serving ----------------------------------------------------------------------------

    [Fact]
    public async Task AnEmptyProjectIsServedWithAPlaceholderAndOnlyTheConfigIsWritten()
    {
        var workspace = Project("empty");
        var state = await _serve.EnsureAsync(workspace);
        Assert.Equal("ready", state.Status);
        Assert.True(state.Empty);
        Assert.False(WebdesignPorts.IsReserved(state.Port));
        var html = await Http.GetStringAsync(state.LocalUrl);
        Assert.Contains("Noch keine Seite", html, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(workspace, ".missum")], Directory.EnumerateFileSystemEntries(workspace));
        Assert.Equal(state.Port, WebdesignProjectConfig.Load(workspace).PreferredPort);
    }

    [Fact]
    public async Task TwoProjectsGetDifferentFreePortsAndASecondCallReusesTheServer()
    {
        var first = await _serve.EnsureAsync(Project("one"));
        var second = await _serve.EnsureAsync(Project("two"));
        Assert.NotEqual(first.Port, second.Port);
        Assert.Equal(first.Port, (await _serve.EnsureAsync(Path.Combine(_root, "one") + "\\")).Port);
    }

    [Fact]
    public async Task ATakenPreferredPortFallsBackToAFreeOne()
    {
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var taken = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var workspace = Project("taken");
            new WebdesignProjectConfig { PreferredPort = taken }.Save(workspace);
            var state = await _serve.EnsureAsync(workspace);
            Assert.Equal("ready", state.Status);
            Assert.NotEqual(taken, state.Port);
        }
        finally { blocker.Stop(); }
    }

    [Theory]
    [InlineData(8080, true)]
    [InlineData(8082, true)]
    [InlineData(8095, true)]
    [InlineData(80, true)]
    [InlineData(5173, false)]
    [InlineData(54321, false)]
    public void MissumsOwnPortsAreNeverUsed(int port, bool reserved) => Assert.Equal(reserved, WebdesignPorts.IsReserved(port));

    [Fact]
    public async Task FilesAreServedWithTypesNoCacheAndALiveScriptOnlyInHtml()
    {
        var workspace = Project("site");
        await File.WriteAllTextAsync(Path.Combine(workspace, "index.html"), "<html><body><h1>Hallo</h1></body></html>");
        Directory.CreateDirectory(Path.Combine(workspace, "css"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "css", "site.css"), "h1{color:red}");
        var state = await _serve.EnsureAsync(workspace);
        using var page = await Http.GetAsync(state.LocalUrl);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", page.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains(WebdesignStaticServer.LiveScriptPath, await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var css = await Http.GetAsync(state.LocalUrl + "css/site.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Equal("h1{color:red}", await css.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("..%2Fsecret.txt")]
    [InlineData(".git/config")]
    [InlineData(".missum/webdesign.json")]
    [InlineData("node_modules/x/index.js")]
    [InlineData("C:/Windows/win.ini")]
    public void RequestsCannotLeaveTheProjectOrReadHiddenFolders(string path) =>
        Assert.Null(WebdesignStaticServer.Resolve(Path.Combine(_root, "guarded"), path));

    [Fact]
    public async Task AChangedPageIsReportedWithItsUrl()
    {
        var workspace = Project("watch");
        var state = await _serve.EnsureAsync(workspace);
        var changed = new TaskCompletionSource<WebdesignPageChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serve.PageChanged += change => { if (change.RelativePath == "about/index.html") changed.TrySetResult(change); };
        Directory.CreateDirectory(Path.Combine(workspace, "about"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "about", "index.html"), "<p>Über uns</p>");
        var change = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("html", change.Kind);
        Assert.Equal(state.LocalUrl + "about/", change.Url);
    }

    [Theory]
    [InlineData(".", "index.html", "")]
    [InlineData(".", "blog/post 1.html", "blog/post%201.html")]
    [InlineData(".", "about/index.html", "about/")]
    [InlineData("public", "public/index.html", "")]
    [InlineData("public", "src/app.js", null)]
    [InlineData(".", "node_modules/a.html", null)]
    public void ProjectPathsMapToServerPaths(string root, string path, string? expected) =>
        Assert.Equal(expected, WebdesignServeService.ServedPath(root, path));

    [Theory]
    [InlineData("../outside", ".")]
    [InlineData("C:\\Windows", ".")]
    [InlineData("public/", "public")]
    [InlineData("a/../b", ".")]
    public void ConfiguredRootsStayInsideTheProject(string value, string expected) =>
        Assert.Equal(expected, WebdesignProjectConfig.NormalizeRelative(value, "."));

    [Fact]
    public void DevScriptsAreDetectedWithStrictPorts()
    {
        var workspace = Project("vite");
        File.WriteAllText(Path.Combine(workspace, "package.json"), """{"scripts":{"dev":"vite","build":"vite build"}}""");
        Assert.Equal("npm run dev -- --port {port} --strictPort --host {host}", WebdesignDevServer.DetectCommand(workspace, out var framework));
        Assert.Equal("vite", framework);
        Assert.Null(WebdesignDevServer.DetectCommand(Project("plain"), out _));
    }

    // ----- wishes ------------------------------------------------------------------------------

    [Fact]
    public void WishesAreNumberedPersistedAndResolved()
    {
        var workspace = Project("wishes");
        var store = new WebdesignAnnotationStore();
        var first = store.Add(workspace, new WebdesignAnnotation { Kind = "pin", Comment = "Weniger Abstand" });
        var second = store.Add(workspace, new WebdesignAnnotation
        {
            Kind = "tune", Selector = ".hero", Styles = [new("padding-top", "48px", "32px")],
        });
        Assert.Equal([1, 2], new[] { first.Number, second.Number });
        Assert.True(File.Exists(WebdesignAnnotationStore.PathFor(workspace)));
        Assert.Contains("Feintuning: padding-top 48px → 32px", store.Describe(workspace), StringComparison.Ordinal);
        Assert.Equal([2], store.Resolve(workspace, [2, 7], "angepasst"));
        Assert.Equal([1], store.Open(workspace).Select(item => item.Number));
        // A fresh store reads the same wishes back from the project.
        Assert.Equal("done", new WebdesignAnnotationStore().List(workspace).Single(item => item.Number == 2).Status);
    }

    [Fact]
    public void TheSourceLocatorPointsAtTheMarkup()
    {
        var workspace = Project("source");
        File.WriteAllText(Path.Combine(workspace, "index.html"), "<main>\n  <section class=\"hero\">\n    <a id=\"cta\">Jetzt buchen</a>\n  </section>\n</main>");
        File.WriteAllText(Path.Combine(workspace, "site.css"), ".pricing { gap: 2rem; }");
        Assert.Equal("index.html:3", WebdesignSourceLocator.Find(workspace, ".", "cta", [], null, "a"));
        Assert.Equal("index.html:2", WebdesignSourceLocator.Find(workspace, ".", null, ["hero"], null, "section"));
        Assert.Equal("site.css:1", WebdesignSourceLocator.Find(workspace, ".", null, ["pricing"], null, "div"));
    }

    // ----- QR code --------------------------------------------------------------------------------

    [Fact]
    public void QrFormatBitsMatchTheStandardTable()
    {
        // Level M rows of the format information table (ISO/IEC 18004, Table C.1).
        Assert.Equal(0b101010000010010, WebdesignQrCode.FormatBits(0));
        Assert.Equal(0b101000100100101, WebdesignQrCode.FormatBits(1));
        Assert.Equal(0b100000011001110, WebdesignQrCode.FormatBits(5));
        Assert.Equal(0b100101010100000, WebdesignQrCode.FormatBits(7));
    }

    [Fact]
    public void QrReedSolomonMatchesTheHelloWorldExample()
    {
        // ISO/IEC 18004 worked example "HELLO WORLD", version 1-M.
        byte[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        Assert.Equal(new byte[] { 196, 35, 39, 119, 235, 215, 231, 226, 93, 23 },
            WebdesignQrCode.ReedSolomonRemainder(data, WebdesignQrCode.ReedSolomonDivisor(10)));
    }

    [Fact]
    public void QrLayoutHasFinderPatternsAndFitsALanUrl()
    {
        Assert.Equal([6, 22, 38], WebdesignQrCode.AlignmentPositions(7));
        Assert.Equal([6, 18], WebdesignQrCode.AlignmentPositions(2));
        Assert.Equal(16, WebdesignQrCode.DataCodewords(1));
        var code = WebdesignQrCode.Encode("http://192.168.178.23:54321/");
        Assert.Equal(code.Version * 4 + 17, code.Size);
        Assert.True(code.Version <= 3);
        foreach (var (x, y) in new[] { (0, 0), (code.Size - 7, 0), (0, code.Size - 7) })
        {
            Assert.True(code[x, y] && code[x + 6, y + 6] && code[x + 3, y + 3]);
            Assert.False(code[x + 1, y + 1]);
        }
        var png = code.ToPng(scale: 4);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, png[..4]);
        Assert.Equal((code.Size + 8) * 4, WebdesignPreviewView.PngSize(png).Width);
    }

    [Fact]
    public void TheColorWheelHasRedOnTopGreenRightAndWhiteInTheCentre()
    {
        Assert.Equal((255, 0, 0), ToTuple(WebdesignColorWheel.ColorAt(50, 0, 100)!.Value));
        Assert.Equal((255, 255, 255), ToTuple(WebdesignColorWheel.ColorAt(50, 50, 100)!.Value));
        var right = WebdesignColorWheel.ColorAt(100, 50, 100)!.Value;
        Assert.True(right.G == 255 && right.R is > 120 and < 135 && right.B == 0); // lime green, as in the reference wheel
        Assert.Null(WebdesignColorWheel.ColorAt(2, 2, 100));
        Assert.Equal((0, 0, 0), ToTuple(WebdesignColorWheel.ColorAt(50, 50, 100, brightness: 0)!.Value));
        // A colour found again on the wheel lands where it was picked.
        var picked = WebdesignColorWheel.ColorAt(70, 30, 100, 0.6)!.Value;
        var spot = WebdesignColorWheel.Locate(picked.R, picked.G, picked.B, 100);
        Assert.InRange(spot.X, 68, 72);
        Assert.InRange(spot.Y, 28, 32);
        Assert.InRange(spot.Brightness, 0.58, 0.62);
        Assert.Equal("#1A1A2E", WebdesignColorWheel.Hex(WebdesignColorWheel.ParseHex("1a1a2e")!.Value));
        Assert.Equal("#AABBCC", WebdesignColorWheel.Hex(WebdesignColorWheel.ParseHex("#abc")!.Value));
        Assert.Null(WebdesignColorWheel.ParseHex("blau"));
        Assert.Equal(100 * 100 * 4, WebdesignColorWheel.Bgra(100).Length);
    }

    private static (int, int, int) ToTuple((byte R, byte G, byte B) color) => (color.R, color.G, color.B);

    // ----- website tab and computer control -----------------------------------------------------

    [Theory]
    [InlineData("rgb(26, 26, 46)", "#1A1A2E")]
    [InlineData("rgba(255, 0, 0, 0.5)", "#FF000080")]
    [InlineData("#abc", "#abc")]
    public void ComputedColorsBecomeHex(string value, string expected) => Assert.Equal(expected, WebdesignPreviewView.ToHex(value));

    [Theory]
    [InlineData("Enter", "Enter", 13)]
    [InlineData("a", "KeyA", 65)]
    [InlineData("F5", "F5", 116)]
    [InlineData("ArrowDown", "ArrowDown", 40)]
    public void KeysMapToDevToolsCodes(string name, string code, int virtualKey)
    {
        var info = WebdesignPreviewView.KeyInfo(name);
        Assert.Equal(code, info.Code);
        Assert.Equal(virtualKey, info.VirtualKey);
    }

    [Fact]
    public void PageElementsBecomeAccessibleElementsForInspect()
    {
        var input = JsonDocument.Parse("""{"role":"textbox","tag":"input","text":"E-Mail","password":true,"disabled":false,"selector":"#mail"}""").RootElement;
        var element = WebViewComputerPlatform.ToElement(input);
        Assert.Equal(50004, element.ControlType);
        Assert.True(element.IsPassword);
        Assert.Equal("#mail", element.RuntimeId);
    }

    [Fact]
    public async Task ComputerControlObservesAndActsInTheWebsiteTab()
    {
        var surface = new FakeSurface();
        var approvals = new CountingApproval();
        using var broker = new ComputerDesktopBroker(approvals, new ThrowingDesktop(), webViewFactory: () => new WebViewComputerPlatform(surface));
        var session = Guid.NewGuid();
        broker.Begin(session, "run-1");
        var observation = await broker.ObserveAsync(session, "run-1", "webview");
        Assert.Equal("http://127.0.0.1:5555/", observation.Url);
        Assert.Equal(780, observation.Width);
        var result = await broker.ExecuteAsync(session, "run-1",
            new ComputerAction(observation.ObservationId, "click", 390, 200, CoordinateSpace: "pixels"));
        Assert.Equal("completed", result.Status);
        // Image pixels are twice the CSS pixels of the page: the click lands at 195/100.
        var click = Assert.Single(surface.Inputs);
        Assert.Equal(("click", 195d, 100d), (click.Kind, click.X, click.Y));
        Assert.Equal(0, approvals.Requests);

        // A changed page invalidates the observation.
        observation = await broker.ObserveAsync(session, "run-1", "webview");
        surface.Url = "http://127.0.0.1:5555/kontakt.html";
        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.ExecuteAsync(session, "run-1",
            new ComputerAction(observation.ObservationId, "click", 10, 10, CoordinateSpace: "pixels")));

        // Computer-Use acts directly: a link to another site is followed without an approval card.
        surface.Url = "http://127.0.0.1:5555/";
        surface.Href = "https://example.org/";
        observation = await broker.ObserveAsync(session, "run-1", "webview");
        var external = await broker.ExecuteAsync(session, "run-1", new ComputerAction(observation.ObservationId, "click", 10, 10, CoordinateSpace: "pixels"));
        Assert.Equal("completed", external.Status);
        Assert.Equal(0, approvals.Requests);
    }

    private sealed class FakeSurface : IWebdesignPreviewSurface
    {
        public string Url { get; set; } = "http://127.0.0.1:5555/";
        public string Href { get; set; } = "";
        public List<WebdesignInput> Inputs { get; } = [];
        public string? Workspace => null;
        public string? CurrentUrl => Url;
        public string Device => "free";
        public Task ShowAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateAsync(string url, CancellationToken cancellationToken) { Url = url; return Task.CompletedTask; }
        public Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken)
        {
            var png = WebdesignQrCode.Encode("x").ToPng();
            var bytes = new byte[png.Length];
            png.CopyTo(bytes, 0);
            // Pretend a 780×1200 image of a 390×600 CSS viewport.
            bytes[16] = 0; bytes[17] = 0; bytes[18] = 0x03; bytes[19] = 0x0C;
            bytes[20] = 0; bytes[21] = 0; bytes[22] = 0x04; bytes[23] = 0xB0;
            return Task.FromResult(new WebdesignCapture(bytes, 780, 1200, 390, 600, 0, 0, Url, "free",
                JsonDocument.Parse("""[{"role":"button","name":"Senden","selector":"#send","x":10,"y":20,"width":80,"height":30}]""").RootElement));
        }
        public Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { tag = "a", role = "link", text = "Link", href = Href, selector = "a" }));
        public Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken) => Task.FromResult((0d, 0d));
        public Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken) { Inputs.Add(input); return Task.CompletedTask; }
    }

    private sealed class CountingApproval : IComputerActionApproval
    {
        public int Requests { get; private set; }
        public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(ComputerActionDecision.Execute);
        }
    }

    private sealed class ThrowingDesktop : IComputerDesktopPlatform
    {
        public Task<ComputerFrame> CaptureAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Desktop darf nicht benutzt werden.");
        public Task ExecuteAsync(ComputerFrame frame, ComputerAction action, bool approved, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<ComputerElement> InspectAsync(ComputerFrame frame, ComputerAction action, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}

using System.Text.Json;
using Missum.App.Services;
using Missum.App.Services.Webdesign;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class WebViewBrowserAgentTests
{
    private const string Server = "http://127.0.0.1:5555/";

    // ----- action parsing ----------------------------------------------------------------------

    [Fact]
    public void ParsesPlainFencedAndChattyAnswers()
    {
        var (plain, _) = WebViewBrowserAgent.ParseAction("""{"thought":"t","next_goal":"g","action":{"type":"click","index":3}}""");
        Assert.Equal(("click", 3, "g"), (plain!.Type, plain.Index, plain.NextGoal));

        var (fenced, _) = WebViewBrowserAgent.ParseAction("Hier:\n```json\n{\"action\":{\"type\":\"input\",\"index\":\"2\",\"text\":\"Ada\"}}\n```\nFertig.");
        Assert.Equal(("input", 2, "Ada"), (fenced!.Type, fenced.Index, fenced.Text));

        var (chatty, _) = WebViewBrowserAgent.ParseAction("<think>{\"action\":{\"type\":\"back\"}}</think>Ich scrolle {\"action\":{\"type\":\"scroll\",\"direction\":\"up\"}} und dann {weiter}");
        Assert.Equal(("scroll", "up"), (chatty!.Type, chatty.Direction));

        // Flattened answers and browser-use names are accepted too.
        Assert.Equal("navigate", WebViewBrowserAgent.ParseAction("""{"action":"go_to_url","url":"/kontakt.html"}""").Action!.Type);
        Assert.Equal("key", WebViewBrowserAgent.ParseAction("""{"type":"send_keys","keys":"Enter"}""").Action!.Type);
        var done = WebViewBrowserAgent.ParseAction("""{"action":{"type":"done","success":false,"result":"Kein Formular"}}""").Action!;
        Assert.False(done.Success);
        Assert.Equal("Kein Formular", done.Result);
        Assert.Equal("Erledigt.", WebViewBrowserAgent.ParseAction("""{"action":{"type":"done"}}""").Action!.Result);
        // Braces inside strings do not end the object.
        Assert.Equal("a}b{", WebViewBrowserAgent.ParseAction("""{"action":{"type":"input","index":1,"text":"a}b{"}}""").Action!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ich klicke jetzt auf den Button.")]
    [InlineData("""{"thought":"nur Gedanken"}""")]
    [InlineData("""{"action":{"type":"fly"}}""")]
    [InlineData("""{"action":{"type":"click"}}""")]
    [InlineData("""{"action":{"type":"input","index":1}}""")]
    [InlineData("""{"action":{"type":"navigate"}}""")]
    [InlineData("""{"action":{"type":"click","index":2""")]
    public void RejectsInvalidAnswersWithAReason(string reply)
    {
        var (action, error) = WebViewBrowserAgent.ParseAction(reply);
        Assert.Null(action);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ----- element list ------------------------------------------------------------------------

    [Fact]
    public void ElementsAreNumberedWithTheirDetailsAndCentres()
    {
        var list = WebViewBrowserAgent.FormatElements(Json("""
            [{"role":"link","tag":"a","name":"Kontakt","href":"/kontakt.html","x":10,"y":20,"width":80,"height":30},
             {"role":"textbox","tag":"input","inputType":"email","name":"E-Mail","value":"a@b.de","x":0,"y":100,"width":200,"height":40},
             {"role":"button","tag":"button","name":"Senden","disabled":true,"x":0,"y":0,"width":10,"height":10},
             {"role":"checkbox","tag":"input","inputType":"checkbox","name":"AGB","checked":true,"x":0,"y":0,"width":10,"height":10}]
            """));
        var lines = list.Text.Split('\n');
        Assert.Equal("[1] <a role=link> \"Kontakt\" href=\"/kontakt.html\"", lines[0].TrimEnd());
        Assert.Equal("[2] <input role=textbox type=email> \"E-Mail\" value=\"a@b.de\"", lines[1].TrimEnd());
        Assert.Contains("(deaktiviert)", lines[2], StringComparison.Ordinal);
        Assert.Contains("(angehakt)", lines[3], StringComparison.Ordinal);
        Assert.Equal((50d, 35d), (list.Targets[1].CenterX, list.Targets[1].CenterY));
        Assert.False(list.Targets[3].Enabled);
        Assert.Empty(WebViewBrowserAgent.FormatElements(default).Targets);
    }

    [Fact]
    public void ElementListStaysWithinCountAndSizeCaps()
    {
        var many = JsonSerializer.SerializeToElement(Enumerable.Range(0, 300)
            .Select(i => new { role = "button", tag = "button", name = "Knopf " + i, x = 0, y = 0, width = 10, height = 10 }));
        var counted = WebViewBrowserAgent.FormatElements(many);
        Assert.Equal(WebViewBrowserAgent.MaxElements, counted.Targets.Count);
        Assert.Contains("180 weitere Elemente", counted.Text, StringComparison.Ordinal);
        Assert.True(counted.Text.Length <= WebViewBrowserAgent.MaxElementCharacters + 200);

        var wide = JsonSerializer.SerializeToElement(Enumerable.Range(0, 120)
            .Select(i => new { role = "link", tag = "a", name = new string('x', 200), href = "/" + new string('p', 300), x = 0, y = 0, width = 10, height = 10 }));
        var sized = WebViewBrowserAgent.FormatElements(wide);
        Assert.True(sized.Text.Length <= WebViewBrowserAgent.MaxElementCharacters + 200, sized.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(sized.Targets.Count, 1, 119);
    }

    // ----- address guard -----------------------------------------------------------------------

    [Theory]
    [InlineData("/kontakt.html", "http://127.0.0.1:5555/kontakt.html")]
    [InlineData("impressum.html", "http://127.0.0.1:5555/sub/impressum.html")]
    [InlineData("http://localhost:5555/a", "http://localhost:5555/a")]
    [InlineData("#top", "http://127.0.0.1:5555/sub/page.html#top")]
    public void NavigationStaysOnTheProjectServer(string url, string expected)
    {
        var (target, error) = WebViewBrowserAgent.ResolveNavigation(url, "http://127.0.0.1:5555/sub/page.html", Server);
        Assert.Null(error);
        Assert.Equal(expected, target!.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://127.0.0.1:3000/")]
    [InlineData("https://127.0.0.1:5555/")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/x")]
    [InlineData("")]
    public void NavigationToOtherOriginsIsRefused(string url)
    {
        var (target, error) = WebViewBrowserAgent.ResolveNavigation(url, Server, Server);
        Assert.Null(target);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(WebViewBrowserAgent.IsSameOrigin(url, Server));
    }

    // ----- dispatch ----------------------------------------------------------------------------

    [Fact]
    public void DispatchUsesTheTabOnlyWhenWebdesignIsActive()
    {
        var now = DateTimeOffset.UtcNow;
        var web = new ChatSession(Guid.NewGuid(), "Portfolio", now, now, ChatMode.Coding, WebdesignEnabled: true);
        var surface = new FakeSurface();
        Assert.True(BrowserUseDispatch.UseWebView(web, "C:\\p", surface, null));
        Assert.True(BrowserUseDispatch.UseWebView(web, "C:\\p", surface, "http://localhost:5555/kontakt.html"));
        Assert.False(BrowserUseDispatch.UseWebView(web with { WebdesignEnabled = false }, "C:\\p", surface, null));
        Assert.False(BrowserUseDispatch.UseWebView(web with { ChatMode = ChatMode.General }, "C:\\p", surface, null));
        Assert.False(BrowserUseDispatch.UseWebView(web, null, surface, null));
        Assert.False(BrowserUseDispatch.UseWebView(web, "C:\\p", null, null));
        Assert.False(BrowserUseDispatch.UseWebView(web, "C:\\p", new FakeSurface { Ready = false }, null));
        // An external start address belongs to the real browser.
        Assert.False(BrowserUseDispatch.UseWebView(web, "C:\\p", surface, "https://example.com/"));
        Assert.False(BrowserUseDispatch.StartUrlMayBeLocal("https://example.com/"));
        Assert.True(BrowserUseDispatch.StartUrlMayBeLocal(null));
        Assert.True(BrowserUseDispatch.StartUrlMayBeLocal("http://localhost:5555/"));
    }

    [Fact]
    public void StepsFollowTheRequestWithinTheHardCap()
    {
        Assert.Equal(20, WebViewBrowserAgent.StepsFor(BrowserUseRequest.Parse(Json("""{"task":"x"}"""))));
        Assert.Equal(7, WebViewBrowserAgent.StepsFor(BrowserUseRequest.Parse(Json("""{"task":"x","maxSteps":7}"""))));
        Assert.Equal(40, WebViewBrowserAgent.StepsFor(BrowserUseRequest.Parse(Json("""{"task":"x","maxSteps":60}"""))));
    }

    // ----- model call --------------------------------------------------------------------------

    [Fact]
    public void ModelRequestCarriesTheScreenshotAsDataUrl()
    {
        var body = OpenAiBrowserAgentModel.Body("coding/m", [new("system", "S"), new("user", "U", [1, 2, 3], "image/jpeg")]);
        Assert.Equal("coding/m", body["model"]!.GetValue<string>());
        Assert.True(body["temperature"]!.GetValue<double>() <= 0.2);
        var messages = body["messages"]!.AsArray();
        Assert.Equal("S", messages[0]!["content"]!.GetValue<string>());
        var parts = messages[1]!["content"]!.AsArray();
        Assert.Equal("U", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", parts[1]!["image_url"]!["url"]!.GetValue<string>());
    }

    // ----- loop --------------------------------------------------------------------------------

    [Fact]
    public async Task LoopActsThroughTheTabAndStopsOnDone()
    {
        var surface = new FakeSurface();
        var model = new FakeModel(
            """{"thought":"","next_goal":"Namen eintragen","action":{"type":"input","index":2,"text":"Ada"}}""",
            """{"action":{"type":"click","index":3}}""",
            """{"action":{"type":"scroll","direction":"down"}}""",
            """{"action":{"type":"navigate","url":"/danke.html"}}""",
            """{"action":{"type":"done","success":true,"result":"Formular gesendet"}}""");
        var outcome = await Agent(surface, model).RunAsync(Request("Formular ausfüllen"), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal("completed", outcome.Status);
        Assert.Equal("Formular gesendet", outcome.Result);
        Assert.Equal(5, outcome.Steps);
        Assert.Equal("webview", outcome.Mode);
        Assert.Equal("Webseite", outcome.Browser);
        Assert.Equal("http://127.0.0.1:5555/danke.html", outcome.FinalUrl);
        Assert.Contains("http://127.0.0.1:5555/danke.html", outcome.Urls);
        Assert.Equal(5, outcome.Actions!.Count);
        Assert.True(surface.Shown);
        // input = click + select all + insertText at the element centre (CSS pixels).
        Assert.Equal(("click", 100d, 120d), (surface.Inputs[0].Kind, surface.Inputs[0].X, surface.Inputs[0].Y));
        Assert.Equal(("keypress", "ctrl+a"), (surface.Inputs[1].Kind, surface.Inputs[1].Key));
        Assert.Equal(("type", "Ada"), (surface.Inputs[2].Kind, surface.Inputs[2].Text));
        Assert.Equal("click", surface.Inputs[3].Kind);
        Assert.Equal(("scroll", 480d), (surface.Inputs[4].Kind, surface.Inputs[4].ScrollY));
        // Each step shows the model the screenshot, the elements and the history.
        var last = model.Calls[^1][^1];
        Assert.Contains("[2] <input", last.Text, StringComparison.Ordinal);
        Assert.Contains("Schritt 4: navigate /danke.html", last.Text, StringComparison.Ordinal);
        Assert.Contains("Ziel: Namen eintragen", last.Text, StringComparison.Ordinal);
        Assert.NotNull(last.Image);
        Assert.Contains(Server.TrimEnd('/'), model.Calls[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidAnswersGetOneCorrectionAndRefusedActionsAreReported()
    {
        var surface = new FakeSurface();
        var model = new FakeModel(
            "Ich würde klicken.",                                                // invalid …
            """{"action":{"type":"navigate","url":"https://example.com/"}}""", // … corrected, but off-site
            "noch immer kein JSON", "wieder nicht",                             // failed step
            """{"action":{"type":"click","index":1}}""",                       // external link: refused
            """{"action":{"type":"click","index":99}}""",                      // unknown element
            """{"action":{"type":"done","result":"ok"}}""");
        var outcome = await Agent(surface, model).RunAsync(Request("x"), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(5, outcome.Steps);
        Assert.Empty(surface.Navigations);
        Assert.Empty(surface.Inputs);
        Assert.Contains("ungültig", model.Calls[1][^1].Text, StringComparison.Ordinal);
        Assert.Equal("assistant", model.Calls[1][^2].Role);
        var history = model.Calls[^1][^1].Text;
        Assert.Contains("ist nicht erlaubt", history, StringComparison.Ordinal);
        Assert.Contains("ungültige Antwort", history, StringComparison.Ordinal);
        Assert.Contains("[99] gibt es", history, StringComparison.Ordinal);
        Assert.Contains(outcome.Actions!, item => item.Contains("fehlgeschlagen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoopStopsAtMaxSteps()
    {
        var surface = new FakeSurface();
        var model = new FakeModel(Enumerable.Repeat("""{"action":{"type":"scroll"}}""", 10).ToArray());
        var outcome = await Agent(surface, model, maxSteps: 3).RunAsync(Request("x"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal("max_steps", outcome.Status);
        Assert.Equal(3, outcome.Steps);
        Assert.Equal(3, surface.Inputs.Count);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Error));
    }

    [Fact]
    public async Task RepeatedInvalidAnswersEndTheRun()
    {
        var model = new FakeModel(Enumerable.Repeat("kein JSON", 20).ToArray());
        var outcome = await Agent(new FakeSurface(), model).RunAsync(Request("x"), CancellationToken.None);
        Assert.False(outcome.Success);
        Assert.Equal(3, outcome.Steps);
        Assert.Equal(6, model.Calls.Count);
    }

    [Fact]
    public async Task CancellationStopsTheLoopAndTimeoutIsReported()
    {
        using var cancel = new CancellationTokenSource();
        var model = new FakeModel(Enumerable.Repeat("""{"action":{"type":"scroll"}}""", 10).ToArray()) { OnCall = count => { if (count == 2) cancel.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Agent(new FakeSurface(), model).RunAsync(Request("x"), cancel.Token));
        Assert.Equal(2, model.Calls.Count);

        var slow = new FakeModel("""{"action":{"type":"wait","seconds":5}}""");
        var agent = new WebViewBrowserAgent(new FakeSurface(), slow, Options(5) with { Timeout = TimeSpan.FromMilliseconds(200) });
        var timedOut = await agent.RunAsync(Request("x"), CancellationToken.None);
        Assert.False(timedOut.Success);
        Assert.Equal("timeout", timedOut.Status);
    }

    [Fact]
    public async Task StartUrlOutsideTheServerIsNotOpened()
    {
        var surface = new FakeSurface();
        var model = new FakeModel("""{"action":{"type":"done","result":"ok"}}""");
        await Agent(surface, model).RunAsync(Request("x", "http://127.0.0.1:9999/"), CancellationToken.None);
        Assert.Empty(surface.Navigations);
        Assert.Contains("nicht auf dem Projektserver", model.Calls[0][^1].Text, StringComparison.Ordinal);

        var local = new FakeSurface();
        await Agent(local, new FakeModel("""{"action":{"type":"done","result":"ok"}}""")).RunAsync(Request("x", "http://localhost:5555/kontakt.html"), CancellationToken.None);
        Assert.Equal(["http://localhost:5555/kontakt.html"], local.Navigations);
    }

    // ----- fakes -------------------------------------------------------------------------------

    private static BrowserUseRequest Request(string task, string? startUrl = null) => new(task, startUrl, 20);

    private static WebViewBrowserAgentOptions Options(int maxSteps) => new()
    {
        MaxSteps = maxSteps,
        SettleDelay = TimeSpan.Zero,
        SettlePoll = TimeSpan.Zero,
        EncodeImage = static (png, _, _) => Task.FromResult((png, "image/png")),
    };

    private static WebViewBrowserAgent Agent(FakeSurface surface, FakeModel model, int maxSteps = 20) => new(surface, model, Options(maxSteps));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeModel(params string[] replies) : IBrowserAgentModel
    {
        private int _next;
        public List<IReadOnlyList<BrowserAgentMessage>> Calls { get; } = [];
        public Action<int>? OnCall { get; init; }
        public string ModelId => "coding/fake";
        public bool SupportsVision => true;

        public Task<string> CompleteAsync(IReadOnlyList<BrowserAgentMessage> messages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add([.. messages]);
            OnCall?.Invoke(Calls.Count);
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private sealed class FakeSurface : IWebdesignPreviewSurface
    {
        public string Url { get; set; } = Server;
        public bool Ready { get; init; } = true;
        public bool Shown { get; private set; }
        public List<WebdesignInput> Inputs { get; } = [];
        public List<string> Navigations { get; } = [];
        public string? Workspace => null;
        public string? CurrentUrl => Url;
        public string Device => "free";
        public bool IsReady => Ready;
        public Task ShowAsync(CancellationToken cancellationToken) { Shown = true; return Task.CompletedTask; }
        public Task NavigateAsync(string url, CancellationToken cancellationToken) { Navigations.Add(url); Url = url; return Task.CompletedTask; }
        public Task<WebdesignCapture> CaptureAsync(string? device, CancellationToken cancellationToken) =>
            Task.FromResult(new WebdesignCapture([0x89, 0x50, 0x4E, 0x47], 800, 600, 800, 600, 0, 0, Url, "free", Json("""
                [{"role":"link","tag":"a","name":"Extern","href":"https://example.org/","x":0,"y":0,"width":20,"height":20},
                 {"role":"textbox","tag":"input","inputType":"text","name":"Name","x":50,"y":100,"width":100,"height":40},
                 {"role":"button","tag":"button","name":"Senden","x":10,"y":200,"width":80,"height":30}]
                """), "Kontakt"));
        public Task<IReadOnlyList<string>> ConsoleAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<JsonElement> InspectAsync(string selector, CancellationToken cancellationToken) => Task.FromResult(default(JsonElement));
        public Task<JsonElement> ElementAtAsync(double cssX, double cssY, CancellationToken cancellationToken) => Task.FromResult(default(JsonElement));
        public Task<(double X, double Y)> ScrollAsync(CancellationToken cancellationToken) => Task.FromResult((0d, 0d));
        public Task DispatchAsync(WebdesignInput input, CancellationToken cancellationToken) { Inputs.Add(input); return Task.CompletedTask; }
        public Task GoBackAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

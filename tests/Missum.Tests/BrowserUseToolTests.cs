using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Extensions;

namespace Missum.Tests;

public sealed class BrowserUseToolTests
{
    [Fact]
    public void BrowserUseIsASelectableResearchToolInEveryMode()
    {
        var action = Assert.Single(BuiltInExtensionCatalog.Actions, item => item.ActionId == BuiltInActionIds.BrowserUse);
        Assert.Equal("Browser-Use", action.DisplayName);
        Assert.Equal(ExtensionActionKind.SelectableTool, action.ActionKind);
        Assert.Equal(BuiltInExtensionCatalog.GroupResearch, action.GroupId);
        Assert.Equal("browser", action.IconKey);
        // General, Codex, Claude Science, Claude Meetings (the legacy Webdesign mode offers no actions).
        Assert.Equal(4, action.SupportedChatModes.Length);
        Assert.DoesNotContain("Missum", action.Description, StringComparison.OrdinalIgnoreCase);
        // Listed directly after the web search in the research group.
        var research = BuiltInExtensionCatalog.Actions.Where(item => item.GroupId == BuiltInExtensionCatalog.GroupResearch)
            .OrderBy(item => item.ItemOrder).Select(item => item.ActionId).ToArray();
        Assert.Equal([BuiltInActionIds.WebSearch, BuiltInActionIds.BrowserUse], research[..2]);
    }

    [Fact]
    public void BrowserUseModelToolHasAStrictGatewaySchema()
    {
        var tool = Assert.Single(BuiltInExtensionCatalog.Tools, item => item.ActionId == BuiltInActionIds.BrowserUse);
        Assert.Equal("browser.use", tool.ModelToolName);
        Assert.Equal(ExtensionToolRiskClass.Process, tool.RiskClass);
        Assert.InRange(tool.Description.Length, 1, 300);
        Assert.Equal(tool.Description.Trim(), tool.Description);
        Assert.InRange(tool.TimeoutSeconds, 300, 7200);
        var schema = tool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["task"], schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["task", "startUrl", "maxSteps"], schema.GetProperty("properties").EnumerateObject().Select(item => item.Name));
    }

    [Fact]
    public void RequestParsingNormalizesAndValidatesArguments()
    {
        var request = BrowserUseRequest.Parse(Json("""{"task":"  Nenne den Seitentitel  ","startUrl":"https://example.com","maxSteps":8}"""));
        Assert.Equal("Nenne den Seitentitel", request.Task);
        Assert.Equal("https://example.com/", request.StartUrl);
        Assert.Equal(8, request.MaxSteps);
        Assert.Equal(BrowserUseRequest.DefaultMaxSteps, BrowserUseRequest.Parse(Json("""{"task":"x"}""")).MaxSteps);
        Assert.Null(BrowserUseRequest.Parse(Json("""{"task":"x","startUrl":"  "}""")).StartUrl);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"task":"   "}""")]
    [InlineData("""{"task":"x","startUrl":"file:///C:/Windows/win.ini"}""")]
    [InlineData("""{"task":"x","startUrl":"example.com"}""")]
    [InlineData("""{"task":"x","maxSteps":0}""")]
    [InlineData("""{"task":"x","maxSteps":61}""")]
    public void RequestParsingRejectsUnsafeOrInvalidArguments(string json) =>
        Assert.Throws<InvalidDataException>(() => BrowserUseRequest.Parse(Json(json)));

    [Fact]
    public void ModelDetectionUsesTheLoadedRouterModelAndPrefersTheSelectedOne()
    {
        var listing = Json("""
            {"data":[
              {"id":"coding/qwen3-coder~a1","status":{"value":"unloaded"}},
              {"id":"embedding/bge~e1","status":{"value":"loaded"}},
              {"id":"coding/gpt-oss-120b~b2","status":{"value":"loaded"}},
              {"id":"vision/gemma-3~c3","status":{"value":"loaded"}}
            ]}
            """);
        var preferred = BrowserUseTool.SelectModel(listing, "gpt-oss-120b", "http://127.0.0.1:8081/v1");
        Assert.NotNull(preferred);
        Assert.Equal("coding/gpt-oss-120b~b2", preferred.ModelId);
        Assert.Equal("http://127.0.0.1:8081/v1", preferred.BaseUrl);
        Assert.False(preferred.SupportsVision);

        // Without a matching preference the first loaded chat model wins; embeddings are never used.
        var fallback = BrowserUseTool.SelectModel(listing, "not-loaded", "http://127.0.0.1:8081/v1");
        Assert.Equal("coding/gpt-oss-120b~b2", fallback!.ModelId);
        var vision = BrowserUseTool.SelectModel(listing, "gemma", "http://127.0.0.1:8081/v1");
        Assert.True(vision!.SupportsVision);
    }

    [Fact]
    public void ModelDetectionRecognizesTheRuntimeVisionProjectorTag()
    {
        var listing = Json("""
            {"data":[{"id":"coding/Qwen3.8-Flash-Next-Q8_0~bdd851ce8c42","tags":["missum-reasoning-default:xhigh","missum-vision:projector"],
              "object":"model","owned_by":"llamacpp","status":{"value":"loaded","args":["llama-server.exe"]}}]}
            """);
        var model = BrowserUseTool.SelectModel(listing, "coding/Qwen3.8-Flash-Next-Q8_0~bdd851ce8c42", "http://127.0.0.1:8081/v1");
        Assert.Equal("coding/Qwen3.8-Flash-Next-Q8_0~bdd851ce8c42", model!.ModelId);
        Assert.True(model.SupportsVision);
    }

    [Fact]
    public void ModelDetectionHandlesPlainListingsAndNothingLoaded()
    {
        Assert.Equal("local", BrowserUseTool.SelectModel(Json("""{"data":[{"id":"local"}]}"""), null, "u")!.ModelId);
        Assert.Equal("m", BrowserUseTool.SelectModel(Json("""{"data":[{"id":"m","status":"loaded"}]}"""), null, "u")!.ModelId);
        Assert.Null(BrowserUseTool.SelectModel(Json("""{"data":[{"id":"m","status":{"value":"unloaded"}}]}"""), null, "u"));
        Assert.Null(BrowserUseTool.SelectModel(Json("""{"models":[]}"""), null, "u"));
    }

    [Fact]
    public void BrowserDiscoveryPrefersChromeAndFallsBackToEdge()
    {
        var chrome = BrowserUseTool.FindBrowser(path => path.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("chrome.exe", chrome);
        var edge = BrowserUseTool.FindBrowser(path => path.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("msedge.exe", edge);
        Assert.Null(BrowserUseTool.FindBrowser(_ => false));
    }

    [Fact]
    public void PythonSelectionNeedsAtLeast311AndPrefersStableReleases()
    {
        Assert.Equal("py311", BrowserUseTool.SelectPython(new Dictionary<string, string> { ["3.11"] = "py311", ["3.14"] = "py314" }));
        Assert.Equal("py313", BrowserUseTool.SelectPython(new Dictionary<string, string> { ["3.13"] = "py313", ["3.11"] = "py311" }));
        Assert.Equal("py315", BrowserUseTool.SelectPython(new Dictionary<string, string> { ["3.15"] = "py315", ["3.10"] = "py310" }));
        Assert.Null(BrowserUseTool.SelectPython(new Dictionary<string, string> { ["3.10"] = "py310", ["2.7"] = "py27" }));
    }

    [Fact]
    public void RunnerResultsBecomeABoundedOutcome()
    {
        var success = BrowserUseTool.ParseOutcome(Json("""
            {"success":true,"status":"completed","finalResult":"Example Domain","urls":["https://example.com/"],"steps":3}
            """), "coding/gpt-oss-120b~b2", "chrome");
        Assert.True(success.Success);
        Assert.Equal("Example Domain", success.Result);
        Assert.Null(success.Error);
        Assert.Equal(["https://example.com/"], success.Urls);
        Assert.Equal(3, success.Steps);

        var failure = BrowserUseTool.ParseOutcome(Json("""{"success":false,"status":"max_steps","finalResult":null}"""), "m", "msedge");
        Assert.False(failure.Success);
        Assert.Equal("max_steps", failure.Status);
        Assert.False(string.IsNullOrWhiteSpace(failure.Error));

        var huge = BrowserUseTool.ParseOutcome(JsonSerializer.SerializeToElement(new { success = true, finalResult = new string('x', 30_000) }), "m", "chrome");
        Assert.True(huge.Result!.Length < 20_100);
    }

    [Fact]
    public void BrokerAcceptsBrowserUseOnlyWithTheCatalogContract()
    {
        var tool = BuiltInExtensionCatalog.Tools.Single(item => item.ModelToolName == "browser.use");
        LocalToolBroker.ValidateProposal(Proposal("""{"task":"Titel von example.com","startUrl":"https://example.com"}""", ToolRiskClass.Process), extensionTool: tool);
        Assert.Throws<InvalidDataException>(() =>
            LocalToolBroker.ValidateProposal(Proposal("""{"task":"x"}""", ToolRiskClass.ReadOnly), extensionTool: tool));
        Assert.ThrowsAny<Exception>(() =>
            LocalToolBroker.ValidateProposal(Proposal("""{"task":"x","script":"alert(1)"}""", ToolRiskClass.Process), extensionTool: tool));
        Assert.ThrowsAny<Exception>(() =>
            LocalToolBroker.ValidateProposal(Proposal("""{"startUrl":"https://example.com"}""", ToolRiskClass.Process), extensionTool: tool));
    }

    [Fact]
    public void RunnerScriptShipsWithTheApp()
    {
        var root = AppContext.BaseDirectory;
        var script = Path.Combine(root, "Assets", "Scripts", "browser-use", "missum_browser_use.py");
        Assert.True(File.Exists(script), script);
        var text = File.ReadAllText(script);
        // Local only: explicit local LLM endpoint and local browser, no cloud defaults.
        Assert.Contains("ChatOpenAI(request[\"model\"], base_url=request[\"baseUrl\"]", text, StringComparison.Ordinal);
        Assert.Contains("Browser(\"chromium\"", text, StringComparison.Ordinal);
    }

    private static ToolProposal Proposal(string arguments, ToolRiskClass risk) =>
        new("proposal-browser", "run-browser", "browser.use", Json(arguments), risk, "Browser-Use ausführen", DateTimeOffset.UtcNow.AddMinutes(5));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

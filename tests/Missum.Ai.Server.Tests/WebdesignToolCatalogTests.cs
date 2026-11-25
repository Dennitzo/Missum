using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class WebdesignToolCatalogTests
{
    private static RunRequest Coding(params string[] capabilities) => new(
        MissumAiProtocol.Version, RunMode.Coding,
        [new RunMessage("user", [new ContentPart("text", "Baue eine Startseite.")])],
        ClientCapabilities: capabilities);

    [Fact]
    public void WebdesignToolsComeOnlyWithTheWebdesignCapability()
    {
        var catalog = new AgentToolCatalog();
        var webdesign = catalog.GetAvailableTools(Coding("coding", "workspace", "coding.process", "webdesign"));
        Assert.Contains(webdesign, static tool => tool.Name == WorkspaceTools.WebdesignServe && !tool.ServerSide);
        Assert.Contains(webdesign, static tool => tool.Name == WorkspaceTools.WebdesignPreview && !tool.ServerSide);
        // Webdesign stays a Codex run: the work plan tool is still offered.
        Assert.Contains(webdesign, static tool => tool.Name == CodingWorkingStateTools.PlanTool);

        var codex = catalog.GetAvailableTools(Coding("coding", "workspace", "coding.process"));
        Assert.DoesNotContain(codex, static tool => WorkspaceTools.IsWebdesign(tool.Name));
    }

    [Fact]
    public void TheValidatorAcceptsTheWebdesignCapability()
    {
        var request = Coding("coding", "coding.evidence", "workspace", "webdesign") with
        {
            CodingOptions = new CodingRunOptions(WorkspacePath: "C:/project"),
        };
        RunRequestValidator.Validate(request);
    }

    [Theory]
    [InlineData("""{"operation":"status"}""")]
    [InlineData("""{"operation":"configure","root":"public","startPage":"index.html","kind":"command","command":"npm run dev -- --port {port}"}""")]
    public void ServeArgumentsAreValidated(string json) =>
        WorkspaceTools.Validate(WorkspaceTools.WebdesignServe, JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("""{"operation":"open","path":"about.html"}""")]
    [InlineData("""{"operation":"capture","device":"smartphone"}""")]
    [InlineData("""{"operation":"inspect","selector":".hero h1"}""")]
    [InlineData("""{"operation":"resolve","ids":[1,3],"note":"Abstand verkleinert"}""")]
    public void PreviewArgumentsAreValidated(string json) =>
        WorkspaceTools.Validate(WorkspaceTools.WebdesignPreview, JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData(WorkspaceTools.WebdesignServe, """{"operation":"delete"}""")]
    [InlineData(WorkspaceTools.WebdesignServe, """{"operation":"configure","kind":"php"}""")]
    [InlineData(WorkspaceTools.WebdesignPreview, """{"operation":"open"}""")]
    [InlineData(WorkspaceTools.WebdesignPreview, """{"operation":"capture","device":"watch"}""")]
    [InlineData(WorkspaceTools.WebdesignPreview, """{"operation":"resolve"}""")]
    [InlineData(WorkspaceTools.WebdesignPreview, """{"operation":"resolve","ids":["1"]}""")]
    [InlineData(WorkspaceTools.WebdesignPreview, """{"operation":"open","path":"a.html","script":"x"}""")]
    public void InvalidWebdesignArgumentsAreRejected(string tool, string json) =>
        Assert.Throws<ArgumentException>(() => WorkspaceTools.Validate(tool, JsonDocument.Parse(json).RootElement));
}

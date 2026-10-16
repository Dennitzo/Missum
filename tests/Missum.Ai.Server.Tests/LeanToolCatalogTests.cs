using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class LeanToolCatalogTests
{
    [Fact]
    public void GeneralProjectOffersLeanAndPinsTheSessionProjectInBothTransportSchemas()
    {
        var id = Guid.NewGuid();
        var request = Request() with { ClientCapabilities = ["math.lean"], SessionId = id.ToString("D") };
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        var proof = Assert.Single(tools, t => t.Name == ClientToolNames.MathFormalProof);
        Assert.Contains("research-" + id.ToString("N"), proof.Schema.GetProperty("properties").GetProperty("projectId").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain(tools, t => t.Name == ClientToolNames.CodingCommand);
        var catalog = new AgentToolCatalog();
        catalog.Validate(proof, JsonSerializer.SerializeToElement(new { projectId = "research-" + id.ToString("N"), operation = "initialize" }));
        catalog.Validate(proof, JsonSerializer.SerializeToElement(new { projectId = "research-" + id.ToString("N"), operation = "check", path = "lean/Lemma.lean", useMathlib = true }));
        foreach (var model in new[] { "coding/DeepSeek", "coding/Qwen3.8-27B-UD-Q8_K_XL" })
        {
            var transport = ModelRuntimeClient.PrepareTransportTools(model, [proof.ToLmDefinition()]);
            Assert.Contains("initialize", transport.GetRawText(), StringComparison.Ordinal);
            Assert.Contains("research-" + id.ToString("N"), transport.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ScienceOffersLeanAutomaticallyAndExplainsItsVerificationScope()
    {
        var request = Request() with { ClientCapabilities = ["math.lean"] };
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        Assert.Contains(tools, t => t.Name == ClientToolNames.MathFormalProof);
        foreach (var profile in new string?[] { null, ToolContextProfiles.Current })
        {
            var messages = RunProcessor.CreateInitialMessages(request, "general", tools.Select(t => t.Name).ToArray(), profile);
            Assert.Contains("axiomAudit", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("physikalische Modellannahmen", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("Mathematikvorlesung", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("keine Lean-Syntax", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("Zwischenschritten", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("Geltungsbereich nicht", messages[0].Content, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScienceDelegationRetainsTheCanonicalProjectAndExactLeanSchema(bool explicitProject)
    {
        const string parentSession = "2bbc0c28-0df2-4f26-b176-0a004ffb8c31";
        const string childSession = "73c9aecf-20a1-4e57-bce8-98cc98d763ae";
        const string expectedProject = "research-2bbc0c280df24f26b1760a004ffb8c31";
        var parent = Request() with
        {
            SessionId = parentSession, DeepResearch = true,
            ClientCapabilities = ["math.lean", "subagents"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: explicitProject ? expectedProject : null),
        };
        var child = parent with
        {
            SessionId = childSession, DeepResearch = false,
            Subagent = new("parent-run", "child-agent", "Beweise die Teilidentität", parentSession),
        };
        var catalog = new AgentToolCatalog();
        var parentProof = Assert.Single(catalog.GetAvailableTools(parent, subagentAvailable: true), tool => tool.Name == ClientToolNames.MathFormalProof);
        var childProof = Assert.Single(catalog.GetAvailableTools(child, subagentAvailable: true), tool => tool.Name == ClientToolNames.MathFormalProof);

        Assert.Equal(expectedProject, Assert.Single(childProof.Schema.GetProperty("properties").GetProperty("projectId").GetProperty("enum").EnumerateArray()).GetString());
        Assert.Equal(parentProof.Schema.GetRawText(), childProof.Schema.GetRawText());
        Assert.Equal(ModelRuntimeClient.PrepareTransportTools("coding/Qwen3.8-27B-UD-Q8_K_XL", [parentProof.ToLmDefinition()]).GetRawText(),
            ModelRuntimeClient.PrepareTransportTools("coding/Qwen3.8-27B-UD-Q8_K_XL", [childProof.ToLmDefinition()]).GetRawText());
    }

    [Theory]
    [InlineData(RunMode.General, false)]
    [InlineData(RunMode.General, true)]
    [InlineData(RunMode.Coding, false)]
    [InlineData(RunMode.Coding, true)]
    public void OrdinaryLeanToolsBindToTheActualLocalSessionOwner(RunMode mode, bool delegated)
    {
        const string ownSession = "73c9aecf-20a1-4e57-bce8-98cc98d763ae";
        const string parentSession = "2bbc0c28-0df2-4f26-b176-0a004ffb8c31";
        var request = Request() with
        {
            Mode = mode, SessionId = ownSession, ClientCapabilities = ["math.lean"],
            // Noncanonical research options do not redirect an ordinary Lean tool.
            ResearchOptions = new(ProtocolVersion: 1, ProjectId: "research-unrelated"),
            Subagent = delegated ? new("parent-run", "child-agent", "Beweise die Teilidentität", parentSession) : null,
        };
        var proof = Assert.Single(new AgentToolCatalog().GetAvailableTools(request), tool => tool.Name == ClientToolNames.MathFormalProof);
        var expected = "research-" + Guid.Parse(delegated ? parentSession : ownSession).ToString("N");

        Assert.Equal(expected, Assert.Single(proof.Schema.GetProperty("properties").GetProperty("projectId").GetProperty("enum").EnumerateArray()).GetString());
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Prove a mathematical identity")])], AllowedServerTools: []);
}

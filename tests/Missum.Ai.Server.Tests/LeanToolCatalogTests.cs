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
        var request = Request() with { ClientCapabilities = ["research.sandbox"] };
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        Assert.Contains(tools, t => t.Name == ClientToolNames.MathFormalProof);
        foreach (var profile in new string?[] { null, ToolContextProfiles.Current })
        {
            var messages = RunProcessor.CreateInitialMessages(request, "general", tools.Select(t => t.Name).ToArray(), profile);
            Assert.Contains("axiomAudit", messages[0].Content, StringComparison.Ordinal);
            Assert.Contains("physikalische Modellannahmen", messages[0].Content, StringComparison.Ordinal);
        }
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Prove a mathematical identity")])], AllowedServerTools: []);
}

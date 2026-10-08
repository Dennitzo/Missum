using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificMediaToolCatalogTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CanonicalScienceAdvertisesImageInputAndMediaAnalysisWithCompleteSchemas(bool compact, bool child)
    {
        var request = Request() with { ContextProfileVersion = compact ? ToolContextProfiles.Current : null };
        RunRequestValidator.Validate(request);
        if (child) request = request with { DeepResearch = false, Subagent = new("parent", "child", "Abbildung prüfen", request.SessionId) };
        var catalog = new AgentToolCatalog();
        var available = catalog.GetAvailableTools(request, subagentAvailable: true);
        var input = catalog.Resolve(WorkspaceTools.ImageInput, available);
        var media = catalog.Resolve("media.analyze", available);

        Assert.False(input.ServerSide);
        Assert.True(media.ServerSide);
        catalog.Validate(input, JsonSerializer.SerializeToElement(new { operation = "file", path = ".assistant/research/figure.png" }));
        catalog.Validate(media, JsonSerializer.SerializeToElement(new
        {
            uploadId = "upload-0123456789abcdef0123456789abcdef",
            prompt = "Prüfe Achsen, Einheiten und erkennbare Artefakte in dieser Abbildung.",
        }));
        var tools = RunProcessor.CreateModelToolDefinitions(available, selectedToolName: null, directTools: true,
            request.ContextProfileVersion);
        var transport = ModelRuntimeClient.PrepareTransportTools(child ? "coding/Qwen3.8@subagent" : "coding/Qwen3.8", tools);
        var projected = transport.EnumerateArray().Select(static item => item.GetProperty("function")).ToArray();
        var imageSchema = projected.Single(item => item.GetProperty("name").GetString()
            == ModelRuntimeClient.ToTransportToolName(WorkspaceTools.ImageInput)).GetProperty("parameters");
        var analysisSchema = projected.Single(item => item.GetProperty("name").GetString()
            == ModelRuntimeClient.ToTransportToolName("media.analyze")).GetProperty("parameters");
        Assert.Equal(input.Schema.GetProperty("required").GetRawText(), imageSchema.GetProperty("required").GetRawText());
        Assert.True(imageSchema.GetProperty("properties").TryGetProperty("path", out _));
        Assert.Equal(media.Schema.GetProperty("required").GetRawText(), analysisSchema.GetProperty("required").GetRawText());
        Assert.True(analysisSchema.GetProperty("properties").TryGetProperty("prompt", out _));
        Assert.False(analysisSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void ScienceModeNeverGrantsMediaAnalysisOutsideItsExplicitServerPermissions()
    {
        var catalog = new AgentToolCatalog();
        var request = Request() with { AllowedServerTools = ["web.search", "web.fetch"] };
        foreach (var child in new[] { false, true })
        {
            var available = catalog.GetAvailableTools(child
                ? request with { DeepResearch = false, Subagent = new("parent", "child", "Aufgabe", request.SessionId) }
                : request, subagentAvailable: true);
            Assert.Contains(available, static tool => tool.Name == WorkspaceTools.ImageInput);
            Assert.DoesNotContain(available, static tool => tool.Name == "media.analyze");
            Assert.Throws<InvalidOperationException>(() => catalog.Resolve("media.analyze", available));
        }
    }

    [Fact]
    public void CompactScienceAllowsFreshImageInputReceiptsWithoutAUserAttachment()
    {
        var request = Request();
        var names = new AgentToolCatalog().GetAvailableTools(request).Select(static tool => tool.Name).ToArray();
        var policy = CompactAgentContextPolicy.Build(request, names);
        Assert.Contains("image.input-Ergebnissen dieses Laufs", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Upload-IDs nur aus der neuesten Nutzernachricht", policy, StringComparison.Ordinal);
        Assert.Contains("historische Dateien mit image.input neu laden", policy, StringComparison.Ordinal);
        Assert.Contains("keine neue Sichtprüfung behaupten", policy, StringComparison.Ordinal);
        Assert.Equal(policy, CompactAgentContextPolicy.Build(request with
        {
            DeepResearch = false,
            Subagent = new("parent", "child", "Abbildung prüfen", request.SessionId),
        }, names));
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Prüfe die erzeugte Abbildung des Erdmagnetfeldes.")])],
        SessionId: "science-session", WorkspacePath: "C:/science-workspace", DeepResearch: true,
        ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2),
        ClientCapabilities: ["workspace", "visual-tools", "research.deliverables", "subagents"],
        AllowedServerTools: ["web.search", "web.fetch", "media.inspect", "media.analyze"]);
}

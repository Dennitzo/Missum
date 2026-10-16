using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class OpenProjectWorkflowTests
{
    [Fact]
    public void RetiredCheckpointPolicyAndSchemasAreUpgradedOnceWithoutChangingChronologicalEvidence()
    {
        var request = Request();
        var data = new Missum.Ai.Server.Core.Models.LmChatMessage("assistant", "Bereits belegtes Ergebnis.");
        var user = new Missum.Ai.Server.Core.Models.LmChatMessage("user", "Fortsetzen");
        var checkpoint = new AgentRunCheckpoint(
            [new("system", "Kanonische wissenschaftliche Arbeit (section-delta-v1): research.update"), data, user],
            4, 3, 100, 30, CanonicalStateReadRequired: true);
        var tools = new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true);
        var migrated = OpenProjectWorkflow.UpgradeLegacyCheckpoint(checkpoint, request, "coding", "fixture-model", tools);
        Assert.Same(data, migrated.Messages[1]);
        Assert.Same(user, migrated.Messages[2]);
        Assert.Equal(checkpoint.RoundCount, migrated.RoundCount);
        Assert.DoesNotContain("research.update", migrated.Messages[0].Content, StringComparison.Ordinal);
        Assert.DoesNotContain(migrated.ContextTools!, static tool => tool.Name.StartsWith("research.", StringComparison.Ordinal));
        Assert.Same(migrated, OpenProjectWorkflow.UpgradeLegacyCheckpoint(migrated, request, "coding", "fixture-model", tools));
        Assert.Contains("research.update", checkpoint.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyScienceRequestUsesOrdinaryEditableProjectToolsWithoutResearchWorkflow()
    {
        var legacy = Request() with
        {
            Mode = RunMode.General,
            PreferredGeneralModelId = "chosen-model",
            DeepResearch = true,
            CodingOptions = null,
            ResearchOptions = new(ProjectId: "previous-project", ProtocolVersion: 2),
            ClientCapabilities = ["workspace", "research.sandbox", "research.deliverables", "subagents", "math.lean"],
        };
        var request = OpenProjectWorkflow.NormalizeLegacyRequest(legacy);
        Assert.Equal(RunMode.Coding, request.Mode);
        Assert.Equal("chosen-model", request.PreferredCodingModelId);
        Assert.Equal(legacy.WorkspacePath, request.CodingOptions!.WorkspacePath);
        Assert.False(request.DeepResearch);
        Assert.Null(request.ResearchOptions);
        Assert.DoesNotContain(request.ClientCapabilities!, static capability => capability.StartsWith("research.", StringComparison.Ordinal));
        var catalog = new AgentToolCatalog();
        var tools = catalog.GetAvailableTools(request, subagentAvailable: true);
        Assert.Contains(tools, static tool => tool.Name == ClientToolNames.CodingWrite);
        Assert.Contains(tools, static tool => tool.Name == ClientToolNames.CodingEdit);
        Assert.Contains(tools, static tool => tool.Name == ClientToolNames.CodingCommand);
        Assert.Contains(tools, static tool => tool.Name == ClientToolNames.MathFormalProof);
        Assert.Contains(tools, static tool => tool.Name == SubagentToolNames.Spawn);
        Assert.Contains(tools, static tool => tool.Name == "web.search");
        Assert.DoesNotContain(tools, static tool => tool.Name.StartsWith("research.", StringComparison.Ordinal));
    }

    [Fact]
    public void BothProjectModesHaveExactlyTheSameToolsForTheSameCapabilities()
    {
        var request = Request();
        var catalog = new AgentToolCatalog();
        var coding = catalog.GetAvailableTools(request, subagentAvailable: true);
        var general = catalog.GetAvailableTools(request with { Mode = RunMode.General }, subagentAvailable: true);
        Assert.Equal(coding.Select(static tool => tool.Name), general.Select(static tool => tool.Name));
        Assert.DoesNotContain(coding, static tool => tool.Name.StartsWith("research.", StringComparison.Ordinal));
    }

    [Fact]
    public void CompactPolicyDoesNotContainScienceStateOrMandatorySimulationInstructions()
    {
        var request = Request();
        var tools = new AgentToolCatalog().GetAvailableTools(request, subagentAvailable: true);
        var policy = CompactAgentContextPolicy.Build(request, tools.Select(static tool => tool.Name).ToArray());
        Assert.DoesNotContain("research.read", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("research.update", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("research.deliverables", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("quantitativeValidation", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("genau einem frühen spawn", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Simulationen", policy, StringComparison.Ordinal);
        Assert.Contains("bei Bedarf subagent.spawn", policy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("research.read")]
    [InlineData("research.update")]
    [InlineData("research.deliverables.verify")]
    [InlineData("research.code.execute")]
    public void RetiredResearchToolsCannotBeReintroducedAsDynamicClientTools(string name)
    {
        Assert.True(AgentToolCatalog.IsReservedToolName(name));
        var request = Request() with
        {
            ClientTools = [new(name, "Veraltetes Werkzeug", ToolRiskClass.ReadOnly,
                JsonSerializer.SerializeToElement(new { type = "object" }), 30, 4096)],
        };
        Assert.Throws<ArgumentException>(() => new AgentToolCatalog().GetAvailableTools(request));
    }

    [Fact]
    public void HistoricReviewFlagDoesNotReintroduceReviewGateAfterSubagentCompletion()
    {
        var messages = new List<Missum.Ai.Server.Core.Models.LmChatMessage>();
        Assert.True(RunProcessor.AppendSubagentResultAcceptanceInstruction(messages,
            JsonSerializer.SerializeToElement(new { status = "completed", runId = "child", scientificReviewRequired = true }), Request()));
        var text = Assert.Single(messages).Content!;
        Assert.DoesNotContain("research.deliverables", text, StringComparison.Ordinal);
        Assert.DoesNotContain("quantitative", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MISSUM_SUBAGENT_RESULT_ACCEPTED", text, StringComparison.Ordinal);
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.Coding,
        [new("user", [new("text", "Bearbeite die Publikation im Projekt.")])],
        SessionId: "2bbc0c28-0df2-4f26-b176-0a004ffb8c31", WorkspacePath: "C:/project",
        CodingOptions: new(WorkspacePath: "C:/project"),
        AllowedServerTools: ["web.search", "web.fetch", "media.analyze"],
        ClientCapabilities: ["coding", "workspace", "coding.process", "coding.evidence", "math.lean", "subagents"]);
}

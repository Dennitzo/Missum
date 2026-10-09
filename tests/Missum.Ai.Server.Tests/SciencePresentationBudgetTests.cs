using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class SciencePresentationBudgetTests
{
    [Fact]
    public void ScientificRunRetainsToolsAndBudgetForPythonAfterLargestLiteraturePipeline()
    {
        var options = new MissumAiServerOptions();
        var request = Request(sandbox: true);
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        var continuationTools = StagedWebResearchPipeline.RemoveFromMainAgentTools(tools);
        Assert.Contains(continuationTools, tool => tool.Name == ClientToolNames.ResearchCodeWrite);
        Assert.Contains(continuationTools, tool => tool.Name == ClientToolNames.ResearchCodeExecute);
        Assert.Equal(0, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
        Assert.Equal(0, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
    }

    [Fact]
    public void ResearchRemainsUnboundedWhileOrdinaryChatKeepsItsStepBudget()
    {
        var options = new MissumAiServerOptions { MaximumModelRounds = 12, MaximumToolCalls = 30 };
        foreach (var request in new[]
        {
            Request(sandbox: false),
            Request(sandbox: true) with { DeepResearch = false },
            Request(sandbox: true) with { ResearchOptions = new(AutonomyLevel: ResearchAutonomyLevel.ReadOnlyResearch) },
        })
        {
            var tools = new AgentToolCatalog().GetAvailableTools(request);
            Assert.Equal(request.DeepResearch ? 0 : 12, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
            Assert.Equal(request.DeepResearch ? 0 : 30, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
        }
    }

    [Fact]
    public void ConfiguredChatBudgetsDoNotTerminateLongResearch()
    {
        var request = Request(sandbox: true);
        var options = new MissumAiServerOptions { MaximumModelRounds = 80, MaximumToolCalls = 100 };
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        Assert.Equal(0, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
        Assert.Equal(0, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
    }

    [Fact]
    public void CanonicalScienceBoundsAutomaticOutputPerTurnWhileResearchRemainsUnbounded()
    {
        var request = Request(sandbox: true) with
        {
            ClientCapabilities = ["research.sandbox", "research.deliverables"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-fixture"),
            Limits = new(MaximumContextTokens: 1_048_576),
        };
        var options = new MissumAiServerOptions();
        var tools = new AgentToolCatalog().GetAvailableTools(request);

        Assert.Equal(32_768, RunProcessor.ResolveMaximumOutputTokensPerTurn(request));
        Assert.Equal(0, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
        Assert.Equal(0, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
        Assert.Equal(1_048_576, request.Limits.MaximumContextTokens);
    }

    [Theory]
    [InlineData(8_192)]
    [InlineData(32_768)]
    [InlineData(65_536)]
    public void ExplicitCanonicalOutputSelectionSurvivesTheAutomaticTurnDefault(int requested)
    {
        var request = Request(sandbox: true) with
        {
            ClientCapabilities = ["research.deliverables"],
            ResearchOptions = new(ProtocolVersion: 2, ProjectId: "research-fixture"),
            Limits = new(MaximumOutputTokens: requested),
        };

        Assert.Equal(requested, RunProcessor.ResolveMaximumOutputTokensPerTurn(request));
    }

    [Fact]
    public void LegacyResearchAndOrdinaryChatRetainTheirExistingAutomaticOutputPolicy()
    {
        var legacy = Request(sandbox: true);
        Assert.Null(RunProcessor.ResolveMaximumOutputTokensPerTurn(legacy));
        Assert.Null(RunProcessor.ResolveMaximumOutputTokensPerTurn(legacy with { DeepResearch = false }));
        Assert.Equal(16_384, RunProcessor.ResolveMaximumOutputTokensPerTurn(legacy with { Limits = new(MaximumOutputTokens: 16_384) }));
    }

    private static RunRequest Request(bool sandbox) => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Untersuche die Frage mit Python.")])], [],
        ClientCapabilities: sandbox ? ["research.sandbox"] : [], DeepResearch: true,
        ResearchOptions: new(ProjectId: "research-fixture", AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));
}

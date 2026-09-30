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
        Assert.True(RunProcessor.ResolveMaximumModelRounds(request, options, tools) - CodingDeepResearchPipeline.MaximumModelCalls >= 12);
        Assert.True(RunProcessor.ResolveMaximumToolCalls(request, options, tools) - CodingDeepResearchPipeline.MaximumToolCalls >= 8);
    }

    [Fact]
    public void OrdinaryResearchAndUnavailableSandboxKeepTheirExistingBudgets()
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
            Assert.Equal(12, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
            Assert.Equal(30, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
        }
    }

    [Fact]
    public void HigherConfiguredScientificBudgetsArePreserved()
    {
        var request = Request(sandbox: true);
        var options = new MissumAiServerOptions { MaximumModelRounds = 80, MaximumToolCalls = 100 };
        var tools = new AgentToolCatalog().GetAvailableTools(request);
        Assert.Equal(80, RunProcessor.ResolveMaximumModelRounds(request, options, tools));
        Assert.Equal(100, RunProcessor.ResolveMaximumToolCalls(request, options, tools));
    }

    private static RunRequest Request(bool sandbox) => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", Text: "Untersuche die Frage mit Python.")])], [],
        ClientCapabilities: sandbox ? ["research.sandbox"] : [], DeepResearch: true,
        ResearchOptions: new(ProjectId: "research-fixture", AutonomyLevel: ResearchAutonomyLevel.SandboxResearch));
}

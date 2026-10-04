using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceWebResearchPolicyTests
{
    private const string LegacyWrappedTask = "[MISSUM_WEB_RESEARCH_REQUEST]\n"
        + "Missum bereitet die SearXNG-Recherche in isolierten SDK-Schritten auf.\n\nRechercheauftrag:\n"
        + "Untersuche die ursprüngliche wissenschaftliche Frage.";
    private static readonly string[] WebTools = ["web.search", "web.fetch"];
    private static readonly string[] CanonicalCapabilities = ["research.deliverables"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanonicalScienceUsesDirectResearchDespiteHistoricWrapperAndWithoutSecondGpu(bool deepResearch)
    {
        var request = Request(canonical: true) with
        {
            DeepResearch = deepResearch,
            Messages = [.. Request(canonical: true).Messages, new("user", [new("text", "Weitermachen")])],
        };

        AssertDirect(GeneralAgentPolicies.ForConversation("general", request, WebTools));
        Assert.Equal(LegacyWrappedTask, request.Messages[0].Content[0].Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualAgentManagedDecisionOverridesLegacyTransportInstruction(bool earlyResearch)
    {
        var request = Request(canonical: false) with { DeepResearch = earlyResearch };

        AssertDirect(GeneralAgentPolicies.ForConversation("general", request, WebTools,
            researchManagedByAgent: true));
        var initial = RunProcessor.CreateInitialMessages(request, "general", WebTools,
            researchManagedByAgent: true);
        AssertDirect(initial[0].Content!);
        Assert.Equal(LegacyWrappedTask, initial[1].Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneralStagedResearchIsPreservedWhenNoAgentOwnsIt(bool deepResearch)
    {
        var request = Request(canonical: false) with { DeepResearch = deepResearch };

        var policy = GeneralAgentPolicies.ForConversation("general", request, WebTools,
            researchManagedByAgent: false);

        Assert.Contains("Gestufte Webrecherche dieses Laufs:", policy, StringComparison.Ordinal);
        Assert.Contains("MISSUM_WEB_RESEARCH_DOSSIER statt der Web-Werkzeugschemas", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Dieser Forschungsagent nutzt", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SubagentUsesDirectWebToolsEvenWhenInheritedHistoryContainsLegacyWrapper()
    {
        var request = Request(canonical: false) with
        {
            Subagent = new("parent-run", "child-agent", "Prüfe eine unabhängige Herleitung.", "science-session"),
        };

        AssertDirect(GeneralAgentPolicies.ForConversation("general", request, WebTools));
    }

    [Fact]
    public void OrdinaryGeneralConversationRetainsAutonomousResearch()
    {
        var request = Request(canonical: false) with
        {
            Messages = [new("user", [new("text", "Vergleiche die aktuellen Originalquellen.")])],
        };

        AssertDirect(GeneralAgentPolicies.ForConversation("general", request, WebTools));
    }

    [Fact]
    public void NewScienceContinuationUsesNewPolicyWithoutRewritingOldCheckpointOrHistory()
    {
        var previousRequest = Request(canonical: true);
        const string response = "Zwischenstand: Eine offene Annahme ist zu prüfen.";
        const string previousPolicy = "Historische Systempolicy: Gestufte Webrecherche dieses Laufs:";
        var previousCheckpoint = new AgentRunCheckpoint(
            [new("system", previousPolicy), new("user", LegacyWrappedTask), new("assistant", response)],
            1, 0, 0, 0, PreserveSessionPromptPrefix: true);
        var previous = new GeneralSessionContextSnapshot(previousCheckpoint, previousRequest, response);
        var request = previousRequest with
        {
            Messages = [.. previousRequest.Messages, new("assistant", [new("text", response)]),
                new("user", [new("text", "Setze den ursprünglichen Auftrag fort.")])],
        };
        var initial = RunProcessor.CreateInitialMessages(request, "general", WebTools,
            researchManagedByAgent: true);

        Assert.True(GeneralSessionContext.TryContinue(previous, request, initial, out var continued));
        AssertDirect(continued[0].Content!);
        Assert.Equal(previousPolicy, previousCheckpoint.Messages[0].Content);
        Assert.Equal(LegacyWrappedTask, previousCheckpoint.Messages[1].Content);
        Assert.Equal(LegacyWrappedTask, continued[1].Content);
        Assert.Equal(response, continued[2].Content);
        Assert.Equal("Setze den ursprünglichen Auftrag fort.", continued[^1].Content);
    }

    private static void AssertDirect(string policy)
    {
        Assert.Contains("Eigenständige Webrecherche:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("Gestufte Webrecherche dieses Laufs:", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("MISSUM_WEB_RESEARCH_DOSSIER statt der Web-Werkzeugschemas", policy, StringComparison.Ordinal);
    }

    private static RunRequest Request(bool canonical) => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", LegacyWrappedTask)])],
        SessionId: "science-session",
        ClientCapabilities: canonical ? CanonicalCapabilities : [],
        AllowedServerTools: WebTools,
        ResearchOptions: canonical ? new(ProjectId: "science-project", ProtocolVersion: 2) : null);
}

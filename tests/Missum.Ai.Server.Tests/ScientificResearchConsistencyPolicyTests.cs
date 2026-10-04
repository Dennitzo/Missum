using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificResearchConsistencyPolicyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SciencePoliciesDistinguishExecutionEvidenceFromScientificConsistency(bool compact)
    {
        var policy = compact
            ? CompactAgentContextPolicy.Build(Request(), [ClientToolNames.ResearchRead, ClientToolNames.ResearchUpdate])
            : ScientificStateAgentPolicy.Instructions;

        foreach (var concept in new[] { "Radius-/Flächenkonventionen", "Geltungsbereich", "Rücksubstitution",
            "Dimensionsvergleich", "Rückumrechnung übereinstimmen", "ProcessSucceeded bestätigt nur die",
            "Originalquelle", "vorhandene passende", "ungeprüft", "widersprüchliche Faktoren",
            "research.deliverables.verify ersetzen diese Prüfung nicht", "Keine pauschale Wahrheitsgarantie" })
            Assert.Contains(concept, policy, StringComparison.OrdinalIgnoreCase);

        Assert.Contains(compact ? "keine allgemeine Python-/Bildpflicht" : "keine allgemeine Pflicht", policy, StringComparison.Ordinal);
        Assert.Contains(compact ? "keine manuelle Child-Nachprüfung" : "prüfe deren Arbeit nicht erneut", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneralPolicyDoesNotReceiveTheSpecificScienceConsistencyGuide()
    {
        var policy = CompactAgentContextPolicy.Build(Request(), []);
        Assert.DoesNotContain("Radius-/Flächenkonventionen", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessSucceeded", policy, StringComparison.Ordinal);
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Untersuche den wissenschaftlichen Auftrag.")])],
        ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}

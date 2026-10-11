using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificResearchPromptContractTests
{
    [Fact]
    public void CompactToolDescriptionRetainsMetadataPatchAndSiblingStatusContract()
    {
        var catalog = new AgentToolCatalog();
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", "Prüfe den vorhandenen Forschungsstand.")])],
            ClientCapabilities: ["research.deliverables"], AllowedServerTools: [],
            ResearchOptions: new(ProtocolVersion: 2));
        var tool = catalog.Resolve(ClientToolNames.ResearchUpdate, catalog.GetAvailableTools(request));
        var transported = ModelRuntimeClient.PrepareTransportTools("coding/DeepSeek",
            [tool.ToLmDefinition() with { ContextProfileVersion = ToolContextProfiles.Current }]);
        var description = transported[0].GetProperty("function").GetProperty("description").GetString()!;

        Assert.Contains("genau eines von data oder patch", description, StringComparison.Ordinal);
        Assert.Contains("Vorhanden: patch mit geänderten Feldern", description, StringComparison.Ordinal);
        Assert.Contains("data.status bzw. patch.status, nie review.status", description, StringComparison.Ordinal);
        Assert.Contains("review.itemRevision=expectedRevision+1", description, StringComparison.Ordinal);
        Assert.Contains("verified braucht echte Prüfnachweise", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ContinuedLegacySessionGetsCurrentSiblingStatusRuleWithoutRewritingEvaluatedHistory()
    {
        List<LmChatMessage> messages =
        [
            new("system", "[MISSUM_SCIENCE_PUBLICATION_REVIEW:v1]\nJede Änderung benötigt data. Dokumentiere review im Objekt."),
            new("user", "Bisheriger Auftrag"),
            new("assistant", "Gespeicherter fachlicher Text"),
            new("user", "Fortsetzen"),
        ];
        var evaluatedPrefix = messages.Take(3).ToArray();

        ScientificStateAgentPolicy.EnsurePublicationWorkflowAtNewRunBoundary(messages);

        Assert.Equal(evaluatedPrefix, messages.Take(3));
        Assert.Equal("Fortsetzen", messages[^1].Content);
        var addedInstruction = messages[^2].Content!;
        Assert.Contains("changes[].patch.status neben changes[].patch.review", addedInstruction, StringComparison.Ordinal);
        Assert.Contains("changes[].data.status neben changes[].data.review", addedInstruction, StringComparison.Ordinal);
        Assert.Contains("Nie review.status; review enthält keinen status", addedInstruction, StringComparison.Ordinal);
        Assert.Contains("tatsächlich erreichten fachlichen Status", addedInstruction, StringComparison.Ordinal);
        Assert.Contains("Ein Review ohne Belege bestätigt keine", addedInstruction, StringComparison.Ordinal);

        // Native runtime normalization converts late system instructions to user messages.
        // Adopting the same contract again must still preserve the evaluated prefix.
        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var before = normalized.ToArray();
        ScientificStateAgentPolicy.EnsurePublicationWorkflowAtNewRunBoundary(normalized);
        Assert.Equal(before, normalized);
    }
}

using Missum.Ai.Contracts;
using Missum.App.Services;
using System.Text.Json;
using Xunit;

namespace Missum.Tests;

public sealed class CodingCapabilityNegotiationTests
{
    private static CapabilitySnapshot Snapshot(string[] server, string[] client) => new(MissumAiProtocol.Version, "test", [], server,
        client, new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize);

    [Fact]
    public void WorkingStateAndMaximumReasoningAreAlwaysRequested()
    {
        var result = MissumAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence]));
        Assert.True(result.Options!.UseWorkingState);
        Assert.Equal("maximum", result.Options.ReasoningPolicy);
        Assert.Contains("coding.evidence", result.Capabilities);
    }

    [Fact]
    public void LegacyGatewayReceivesNoNewFieldsOrCapabilityNames()
    {
        var negotiated = MissumAiAssistantService.NegotiateCodingOptions(Snapshot([], []));
        Assert.Equal("coding", Assert.Single(negotiated.Capabilities));
        Assert.Null(negotiated.Options);
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [], ClientCapabilities: negotiated.Capabilities,
            CodingOptions: negotiated.Options);
        Assert.False(JsonSerializer.SerializeToElement(request, MissumAiProtocol.CreateJsonOptions()).TryGetProperty("codingOptions", out _));
    }

    [Fact]
    public void PartialUpgradeNeverAdvertisesIncompleteEvidenceProtocol()
    {
        var negotiated = MissumAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput]));
        Assert.Equal("coding", Assert.Single(negotiated.Capabilities));
        Assert.Equal("maximum", negotiated.Options?.ReasoningPolicy);
    }

    [Fact]
    public void CompleteGatewayUsesFixedCodingPolicy()
    {
        var negotiated = MissumAiAssistantService.NegotiateCodingOptions(Snapshot(["coding.updatePlan"],
            [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence]));
        Assert.Contains("coding.evidence", negotiated.Capabilities);
        Assert.Equal("maximum", negotiated.Options?.ReasoningPolicy);
        Assert.True(negotiated.Options?.UseWorkingState);
    }
}

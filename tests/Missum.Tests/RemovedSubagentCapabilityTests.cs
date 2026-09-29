using Missum.Ai.Contracts;
using Missum.App.Services;
using System.Text.Json;

namespace Missum.Tests;

public sealed class RemovedSubagentCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodingNegotiationNeverReintroducesRemovedCapabilities(bool upgradedGateway)
    {
        var snapshot = new CapabilitySnapshot(MissumAiProtocol.Version, "test", [],
            upgradedGateway ? ["coding.updatePlan"] : [],
            upgradedGateway ? [ClientToolNames.CodingReadOutput, ClientToolNames.CodingSearchRunEvidence] : [],
            new Dictionary<string, long>(), [], true, MissumAiProtocol.UploadChunkSize);
        var negotiated = MissumAiAssistantService.NegotiateCodingOptions(snapshot);
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding, [],
            ClientCapabilities: negotiated.Capabilities
                .Concat(MissumAiAssistantService.WorkspaceClientCapabilities).Distinct().ToArray(),
            CodingOptions: negotiated.Options);

        var serialized = JsonSerializer.SerializeToElement(request, MissumAiProtocol.CreateJsonOptions());
        var sentCapabilities = serialized.GetProperty("clientCapabilities")
            .EnumerateArray().Select(static item => item.GetString()!).ToArray();

        Assert.Contains("coding", sentCapabilities);
        Assert.Contains("documentIo", sentCapabilities);
        Assert.DoesNotContain(sentCapabilities, IsRemovedCapability);
        Assert.Equal(upgradedGateway, sentCapabilities.Contains("coding.evidence", StringComparer.Ordinal));
    }

    private static bool IsRemovedCapability(string capability) =>
        capability.Equals("document-agent", StringComparison.OrdinalIgnoreCase)
        || capability.Contains("subagent", StringComparison.OrdinalIgnoreCase)
        || capability.Contains("parallel", StringComparison.OrdinalIgnoreCase);
}

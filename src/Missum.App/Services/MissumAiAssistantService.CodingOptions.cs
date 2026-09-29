using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal static (IReadOnlyList<string> Capabilities, CodingRunOptions? Options) NegotiateCodingOptions(
        CapabilitySnapshot server)
    {
        var supportsOptions = server.ServerTools.Contains("coding.updatePlan", StringComparer.Ordinal);
        List<string> capabilities = ["coding"];
        if (supportsOptions && server.ClientTools.Contains(ClientToolNames.CodingReadOutput, StringComparer.Ordinal)
            && server.ClientTools.Contains(ClientToolNames.CodingSearchRunEvidence, StringComparer.Ordinal)) capabilities.Add("coding.evidence");
        return (capabilities, supportsOptions ? new CodingRunOptions(UseWorkingState: true, ReasoningPolicy: "maximum") : null);
    }
}

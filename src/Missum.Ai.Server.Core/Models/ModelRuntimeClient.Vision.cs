using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal const int VisionDiagnosticOutputTokens = 2048;
    private static readonly string[] ScientificPlotKeywords =
    [
        "MISSUM_SCIENCE_PLOT_REVIEW", "plot", "diagramm", "chart", "achsen", "axis", "axes",
        "trajektor", "trajectory", "trajectories", "histogram", "streudiagramm",
    ];

    internal static string VisionSystemInstructions(string prompt)
    {
        var instructions = CodingAgentPolicy.ReasoningLanguagePrompt + "\n\n" + Policies.GeneralAgentPolicies.VisionAnalysisSystemPrompt;
        if (IsScientificPlotRequest(prompt)) instructions += "\n\n" + Policies.GeneralAgentPolicies.ScientificPlotVisionInstructions;
        return instructions;
    }

    internal static bool IsScientificPlotRequest(string prompt) =>
        ScientificPlotKeywords.Any(term => prompt.Contains(term, StringComparison.OrdinalIgnoreCase));

    internal async Task<string?> ResolveIntegratedVisionAsync(string? selectedModel, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(selectedModel)) return null;
        var status = await GetStatusAsync(token).ConfigureAwait(false);
        if (!status.ProviderReachable)
            throw new HttpRequestException("Die Vision-Fähigkeit des ausgewählten Modells konnte nicht geprüft werden. Kein anderes Modell wurde ausgewählt.");
        return SelectIntegratedVision(status.Models, selectedModel);
    }

    // Exact instance identity matters: changing coding/ to vision/ would unload
    // the text model and lose the benefit of its already loaded projector.
    internal static string? SelectIntegratedVision(IReadOnlyList<ModelRuntimeStatus> models, string selectedModel)
    {
        var exact = models.FirstOrDefault(model => model.Downloaded && model.SupportsVision
            && model.Id.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
        if (exact is null || exact.Loaded) return exact?.Id;
        return exact.Id;
    }
}

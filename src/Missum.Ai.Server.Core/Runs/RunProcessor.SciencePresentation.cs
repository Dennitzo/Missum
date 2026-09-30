using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    // The staged literature pipeline consumes its own rounds before the normal
    // agent can write inputs/scripts, execute them and explain the real result.
    // Reserve a bounded continuation instead of letting the default General
    // budget expire immediately after that pipeline returns its dossier.
    internal const int ScientificPresentationModelReserve = 12;
    internal const int ScientificPresentationToolReserve = 8;

    private static bool HasScientificPresentationWork(RunRequest request, IReadOnlyList<AgentToolSpec> tools) =>
        request.Mode != RunMode.Coding && request.DeepResearch
        && request.ResearchOptions?.AutonomyLevel == ResearchAutonomyLevel.SandboxResearch
        && request.ClientCapabilities?.Contains("research.sandbox", StringComparer.OrdinalIgnoreCase) == true
        && tools.Any(tool => tool.Name == ClientToolNames.ResearchCodeWrite)
        && tools.Any(tool => tool.Name == ClientToolNames.ResearchCodeExecute);
}

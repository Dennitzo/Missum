using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Reads older saved requests without reviving their retired research workflow.</summary>
internal static class OpenProjectWorkflow
{
    internal static AgentRunCheckpoint UpgradeLegacyCheckpoint(AgentRunCheckpoint checkpoint, RunRequest request,
        string role, string modelId, IReadOnlyList<AgentToolSpec> tools)
    {
        if (!checkpoint.CanonicalStateReadRequired && !checkpoint.EarlySubagentDelegationPending
            && checkpoint.ContextTools?.Any(static tool => tool.Name.StartsWith("research.", StringComparison.Ordinal)) != true
            && !checkpoint.Messages.Any(static message => message.Role == "system" && IsRetiredInstruction(message.Content)))
            return checkpoint;
        var policy = RunProcessor.CreateInitialMessages(request, role, tools.Select(static tool => tool.Name).ToArray(),
            checkpoint.ContextProfileVersion)[0];
        var replacedPolicy = false;
        var messages = new List<LmChatMessage>();
        foreach (var message in checkpoint.Messages)
        {
            if (!replacedPolicy && message.Role == "system")
            {
                messages.Add(policy);
                replacedPolicy = true;
                continue;
            }
            if (message.Role == "system" && IsRetiredInstruction(message.Content)
                || message.Role == "user" && message.Content?.StartsWith("Missum-Laufanweisung:\n", StringComparison.Ordinal) == true
                    && IsRetiredInstruction(message.Content)) continue;
            messages.Add(message);
        }
        if (!replacedPolicy) messages.Insert(0, policy);
        var definitions = RunProcessor.CreateModelToolDefinitions(tools, null, directTools: true, checkpoint.ContextProfileVersion);
        return checkpoint with
        {
            Messages = messages,
            ContextTools = definitions,
            ToolCatalogSignature = ToolContextProfiles.Signature(definitions, modelId),
            CanonicalStateReadRequired = false,
            EarlySubagentDelegationPending = false,
            EarlySubagentDelegationRetryCount = 0,
            ScientificStateProgress = null,
            ResearchStateStamp = null,
            ResearchManagedByAgent = request.Subagent is not null,
        };
    }

    private static bool IsRetiredInstruction(string? content) => content is not null
        && (content.Contains("[MISSUM_SCIENCE_", StringComparison.Ordinal)
            || content.Contains("Kanonische wissenschaftliche Arbeit (section-delta-v1)", StringComparison.Ordinal)
            || content.Contains("Science: Dauerhafter Stand über research.read/update", StringComparison.Ordinal)
            || content.Contains("Frühe Arbeitsteilung für den aktuellen Forschungsauftrag:", StringComparison.Ordinal));

    internal static RunRequest NormalizeLegacyRequest(RunRequest request)
    {
        var capabilities = request.ClientCapabilities;
        if (capabilities?.Any(static capability => capability.StartsWith("research.", StringComparison.OrdinalIgnoreCase)) != true)
            return request;
        var workspace = request.CodingOptions?.WorkspacePath ?? request.WorkspacePath;
        var current = capabilities.Where(static capability => !capability.StartsWith("research.", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            current.Add("coding");
            current.Add("workspace");
            if (capabilities.Contains("research.sandbox", StringComparer.OrdinalIgnoreCase)) current.Add("coding.process");
        }
        return request with
        {
            Mode = string.IsNullOrWhiteSpace(workspace) ? request.Mode : RunMode.Coding,
            PreferredCodingModelId = string.IsNullOrWhiteSpace(workspace) ? request.PreferredCodingModelId
                : request.PreferredCodingModelId ?? request.PreferredGeneralModelId,
            DeepResearch = false,
            ResearchOptions = null,
            CodingOptions = string.IsNullOrWhiteSpace(workspace) ? request.CodingOptions
                : (request.CodingOptions ?? new CodingRunOptions()) with { WorkspacePath = workspace },
            ClientCapabilities = current.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }
}

using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal static string ResolveScientificResearchPrompt(ChatMode mode, string originalPrompt,
        PromptTriggerMatch? trigger, string transformedPrompt)
    {
        // Science owns its research directly, including fresh continuation attempts.
        // The synthetic WebSearch trigger need not carry DeepResearch even though
        // the resulting Science request does. Do not promise a separate SDK dossier.
        // Only newly constructed requests are affected; saved history stays intact.
        if (mode != ChatMode.ClaudeScience || trigger?.Trigger.Action != PromptTriggerAction.WebSearch)
            return transformedPrompt;

        return string.IsNullOrWhiteSpace(trigger.RemainingPrompt) ? originalPrompt : trigger.RemainingPrompt;
    }
}

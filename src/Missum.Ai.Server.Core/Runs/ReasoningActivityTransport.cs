using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>
/// Projects private provider reasoning to a phase transition only. The native
/// parser and its caller retain the exact text for context and interruption
/// recovery; the client journal never receives per-fragment thought text.
/// </summary>
internal sealed class ReasoningActivityTransport
{
    private bool _active;

    internal ModelGenerationEvent? Observe(ModelRuntimeProgress progress)
    {
        if (progress.State == "reasoningDelta" && !string.IsNullOrEmpty(progress.ReasoningDelta))
        {
            if (_active) return null;
            _active = true;
            return new("reasoningStarted");
        }
        return progress.State is "toolCallGeneration" or "toolSelected" or "generationRetry" or "reasoningGuardStopped"
            || progress.State == "contentDelta" && !string.IsNullOrEmpty(progress.ContentDelta)
            ? Complete() : null;
    }

    internal ModelGenerationEvent? Complete(string? failureKind = null)
    {
        if (!_active) return null;
        _active = false;
        return new("reasoningCompleted", FailureKind: failureKind);
    }
}

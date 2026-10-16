using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Calibrates deliberate compaction from the native chat template's exact count.</summary>
internal static class ScientificNativeContextRecovery
{
    internal const int MaximumRetries = 2;

    internal static ContextPlan Prepare(IReadOnlyList<LmChatMessage> prompt,
        NativeContextBudgetException overflow, int? maximumOutputTokens)
    {
        var estimate = ContextPlanner.EstimateTokens(prompt);
        var target = ContextPlanner.ComputeInputTokenBudget(overflow.ContextLength, maximumOutputTokens);
        // Account for tokenizer/template/tool overhead without changing the actual
        // model window. Leave room to grow rather than recompact each next turn.
        var calibrated = (int)Math.Min(estimate * 0.85,
            (long)estimate * target / Math.Max(1, overflow.InputTokens) * 0.9);
        if (calibrated < 1024) throw overflow;
        ContextPlan plan;
        try
        {
            plan = ScientificRunContextPlanner.Prepare(prompt, overflow.ContextLength,
                maximumOutputTokens, preserveConversationPrefix: true, maximumInputTokens: calibrated);
        }
        catch (ContextBudgetException)
        {
            // Protected scientific evidence must not be cut to force a retry.
            throw overflow;
        }
        if (!plan.WasCompacted || plan.EstimatedInputTokens >= estimate) throw overflow;
        return plan;
    }
}

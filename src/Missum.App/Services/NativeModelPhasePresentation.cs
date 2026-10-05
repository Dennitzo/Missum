using System.Globalization;

namespace Missum.App.Services;

/// <summary>Measured prompt evaluation is separate from generated reasoning, for every model.</summary>
internal sealed record NativeModelPhasePresentation(
    string? Phase = null, int? ProcessedPromptTokens = null, int? TotalPromptTokens = null,
    double? PromptProgress = null, DateTimeOffset? UpdatedAt = null)
{
    internal bool IsProcessing => Phase == "promptProcessing";

    internal NativeModelPhasePresentation Observe(string? phase, int? processed, int? total,
        double? progress, DateTimeOffset? updatedAt)
    {
        if (updatedAt.HasValue && UpdatedAt.HasValue && updatedAt < UpdatedAt) return this;
        // Heartbeats and internal fragments cannot relabel real prompt evaluation.
        if (phase is null or "codingWaiting" or "reasoningDelta" or "contentDelta") return this;
        var samePhase = phase == Phase;
        var hasNewCounts = PositiveCount(processed).HasValue || PositiveCount(total).HasValue;
        return new(phase, PositiveCount(processed) ?? (samePhase ? ProcessedPromptTokens : null),
            PositiveCount(total) ?? (samePhase ? TotalPromptTokens : null),
            ValidProgress(progress) ?? (samePhase && !hasNewCounts ? PromptProgress : null), updatedAt ?? UpdatedAt);
    }

    internal NativeModelPhasePresentation ObserveReasoning(DateTimeOffset updatedAt) =>
        UpdatedAt.HasValue && updatedAt < UpdatedAt ? this : this with { Phase = "reasoning", UpdatedAt = updatedAt };

    internal NativeModelPhasePresentation ObserveContent(DateTimeOffset updatedAt) =>
        UpdatedAt.HasValue && updatedAt < UpdatedAt ? this : this with { Phase = "content", UpdatedAt = updatedAt };

    internal string ProcessingLabel
    {
        get
        {
            var fraction = PromptProgress ?? (TotalPromptTokens is > 0 && ProcessedPromptTokens is { } processed
                ? Math.Clamp((double)processed / TotalPromptTokens.Value, 0, 1) : (double?)null);
            var text = "Kontext wird verarbeitet";
            if (fraction is { } progress) text += " · " + progress.ToString("P0", CultureInfo.CurrentCulture);
            if (ProcessedPromptTokens is { } count) text += $" · {count:N0} Eingangstoken";
            return text;
        }
    }

    internal string ProcessingExplanation => (ProcessedPromptTokens is { } count
        ? TotalPromptTokens is { } total ? $"{count:N0} von {total:N0} Eingangstoken verarbeitet. "
            : $"{count:N0} Eingangstoken verarbeitet. " : "")
        + "Der Denkprozess erscheint, sobald das Modell mit der Generierung beginnt.";

    private static int? PositiveCount(int? count) => count is >= 0 ? count : null;
    private static double? ValidProgress(double? progress) => progress.HasValue && double.IsFinite(progress.Value)
        ? Math.Clamp(progress.Value, 0, 1) : null;
}

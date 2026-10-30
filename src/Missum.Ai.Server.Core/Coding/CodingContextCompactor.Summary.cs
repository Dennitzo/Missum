using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Coding;

internal static partial class CodingContextCompactor
{
    internal const int MaximumSummaryBudgetRetries = 2;
    private const int MinimumSummaryArchiveCharacters = 256;

    /// <summary>
    /// The archive is a deliberately bounded summary input. Native token counts
    /// can differ substantially from character estimates; retry only that archive,
    /// never the original task, instructions, working state or retained exchanges.
    /// </summary>
    internal static async Task<LmChatResult> CompleteSummaryAsync(CodingCompactionPlan plan, int contextLength,
        int? maximumOutputTokens,
        Func<IReadOnlyList<LmChatMessage>, int, CancellationToken, Task<LmChatResult>> complete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(complete);
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2048);
        if (maximumOutputTokens is { } outputLimit) ArgumentOutOfRangeException.ThrowIfLessThan(outputLimit, 1);
        var header = "Ursprünglicher Auftrag:\n" + plan.CurrentRequest.Content + "\n\nBisherige abgeschlossene Arbeit:\n";
        if (plan.SummaryRequest.Count < 2 || plan.SummaryRequest[1].Role != "user"
            || plan.SummaryRequest[1].Content is not { } source || !source.StartsWith(header, StringComparison.Ordinal))
            throw new InvalidOperationException("Die Zusammenfassungsanfrage enthält keinen eindeutig gebundenen Originalauftrag und Archivtext.");
        var originalArchive = source[header.Length..];
        var maximumArchive = originalArchive.Length;
        var request = plan.SummaryRequest.ToArray();
        var output = SummaryOutputLimit(contextLength, maximumOutputTokens);
        var target = SummaryInputBudget(contextLength, output);
        var initialEstimate = ContextPlanner.EstimateTokens(request);
        if (initialEstimate > target)
        {
            maximumArchive = ArchiveCharactersForEstimate(plan, header, target);
            if (maximumArchive < MinimumSummaryArchiveCharacters) throw new ContextBudgetException(initialEstimate, target);
            maximumArchive = Math.Min(maximumArchive, originalArchive.Length);
            request = WithSummaryArchive(plan, header, originalArchive, maximumArchive);
        }
        for (var retry = 0; ; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await complete(request, output, cancellationToken).ConfigureAwait(false); }
            catch (NativeContextBudgetException overflow) when (retry < MaximumSummaryBudgetRetries)
            {
                contextLength = Math.Min(contextLength, overflow.ContextLength);
                output = SummaryOutputLimit(contextLength, maximumOutputTokens);
                target = Math.Min(SummaryInputBudget(contextLength, output), overflow.BudgetTokens);
                var estimate = ContextPlanner.EstimateTokens(request);
                // Calibrate the complete estimated input from the exact native
                // count, then allocate only the remainder to archived text.
                var calibrated = (int)Math.Min(int.MaxValue,
                    (long)estimate * target / Math.Max(1, overflow.InputTokens) * 85 / 100);
                var archiveBudget = ArchiveCharactersForEstimate(plan, header, calibrated);
                var nextMaximum = Math.Min(maximumArchive * 3 / 4, archiveBudget);
                if (nextMaximum < MinimumSummaryArchiveCharacters || nextMaximum >= maximumArchive) throw;
                maximumArchive = nextMaximum;
                request = WithSummaryArchive(plan, header, originalArchive, maximumArchive);
            }
        }
    }

    private static int SummaryOutputLimit(int contextLength, int? requested) =>
        Math.Min(requested ?? 4096, Math.Min(4096, Math.Max(256, contextLength / 2)));

    private static int SummaryInputBudget(int contextLength, int output) =>
        Math.Min(ContextPlanner.ComputeInputTokenBudget(contextLength, output), contextLength - output - 1);

    internal static void ValidateSummaryTokenBudget(ModelRuntimeProgress progress, int contextLength, int outputLimit)
    {
        if (progress.State != "tokenCounting" || progress.PromptTokens is not { } exactInput) return;
        var inputBudget = SummaryInputBudget(contextLength, outputLimit);
        if (exactInput > inputBudget) throw new NativeContextBudgetException(exactInput, contextLength, inputBudget);
    }

    private static int ArchiveCharactersForEstimate(CodingCompactionPlan plan, string header, int target)
    {
        var fixedRequest = plan.SummaryRequest.ToArray();
        fixedRequest[1] = fixedRequest[1] with { Content = header };
        var fixedTokens = ContextPlanner.EstimateTokens(fixedRequest);
        return (int)Math.Clamp(((long)target - fixedTokens) * 3 - 128, 0, int.MaxValue);
    }

    private static LmChatMessage[] WithSummaryArchive(CodingCompactionPlan plan, string header, string archive, int maximum)
    {
        var result = plan.SummaryRequest.ToArray();
        result[1] = result[1] with { Content = header + Bound(archive, maximum) };
        return result;
    }
}

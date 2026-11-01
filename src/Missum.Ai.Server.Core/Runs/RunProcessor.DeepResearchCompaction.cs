using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    private async Task<IReadOnlyList<LmChatMessage>> CompactResearchContextAsync(string runId,
        StagedWebResearchModelRequest request, ResearchModelSession session, int contextLength,
        string? runtimeInstanceId, CancellationToken cancellationToken)
    {
        var stepId = "context-" + ++session.CompactionSequence;
        var before = ContextPlanner.EstimateTokens(session.Conversation.Messages);
        await session.Reporter.ActivityAsync(new("compaction", "running", "Kontext wird komprimiert", stepId,
            "context.compact", Detail: "Älterer Arbeitskontext wird verdichtet. Die aktuelle Recherchephase wird danach fortgesetzt."),
            cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = session.Conversation.PlanCompaction(contextLength, session.WorkingState)
                ?? throw new InvalidOperationException("Der geschützte Rechercheauftrag und seine Quellen passen nicht in das Modellfenster; es ist kein älterer Kontext mehr zum Verdichten vorhanden.");
            var effort = _modelRuntime.ResolveCompactionReasoningEffort(request.ModelId, request.ModelRole);
            // The caller already owns the scheduler lease. Model turn ownership
            // serializes summary and retry on the same instance and session KV.
            var summary = await CodingContextCompactor.CompleteSummaryAsync(plan, contextLength,
                request.MaximumOutputTokens, async (messages, outputLimit, token) =>
                {
                    var result = await _modelRuntime.CompleteChatWithCacheOwnershipAsync(request.ModelId, messages,
                        session.Conversation.Tools, session.OwnerId, session.ParentOwnerId, outputLimit,
                        modelRole: request.ModelRole, reasoningEffort: effort, requiredContextLength: contextLength,
                        sessionCacheKey: session.CacheKey, runtimeInstanceId: runtimeInstanceId,
                        suppressToolCalls: true, deferSessionCacheCheckpoint: true,
                        nativeProgress: (progress, _) =>
                        {
                            CodingContextCompactor.ValidateSummaryTokenBudget(progress, contextLength, outputLimit);
                            return ValueTask.CompletedTask;
                        }, cancellationToken: token).ConfigureAwait(false);
                    session.CompactionModelCalls++;
                    session.CompactionInputTokens += result.InputTokens;
                    session.CompactionOutputTokens += result.OutputTokens;
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            var compacted = session.Conversation.CompleteCompaction(plan, summary.Content);
            var after = ContextPlanner.EstimateTokens(compacted);
            await session.Reporter.ActivityAsync(new("compaction", "completed", "Kontext komprimiert", stepId,
                "context.compact", Detail: $"{plan.ArchivedMessages} ältere Abschnitte verdichtet ({before:N0} → {after:N0} geschätzte Tokens). Die Recherche wird fortgesetzt.",
                ModelProgress: new ModelGenerationEvent("contextCompacted", PromptTokens: after, CurrentTokens: after)),
                cancellationToken).ConfigureAwait(false);
            return compacted;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var status = error is RunSteeringBoundaryException ? "interrupted"
                : error is OperationCanceledException ? "cancelled" : "failed";
            await session.Reporter.ActivityAsync(new("compaction", status,
                status is "cancelled" or "interrupted" ? "Kontextkomprimierung unterbrochen" : "Kontextkomprimierung fehlgeschlagen",
                stepId, "context.compact", Detail: error.Message), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    private readonly HashSet<string> _compactingSessions = new(StringComparer.Ordinal);

    public async Task<SessionContextCompactionResponse> CompactSessionContextAsync(
        SessionContextCompactionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 128
            || request.PreferredModelId?.Length > 512
            || request.MaximumContextTokens is < 2048)
            throw new ArgumentException("Eine gültige Sitzung, Modellauswahl und ein gültiges Kontextlimit sind erforderlich.");
        lock (_activeGate)
            if (!_compactingSessions.Add(request.SessionId))
                throw new InvalidOperationException("Der Kontext dieser Sitzung wird bereits komprimiert.");
        try
        {
            var source = await _repository.GetIdleSessionCompactionSourceAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
            if (source is null)
                return new(null, false, 0, 0, 0, "Es ist noch kein gespeicherter Modellkontext für diese Sitzung vorhanden.");
            var saved = source.Checkpoint;
            var before = ContextPlanner.EstimateTokens(saved.Messages);
            // The normal GPU lane and native inference gate also protect this
            // explicit summary from evicting a concurrently serving model.
            await using var lease = await _scheduler.AcquireAsync("session-context-compaction", source.RunId, cancellationToken).ConfigureAwait(false);
            var current = await _repository.GetIdleSessionCompactionSourceAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
            if (current is null || current.RunId != source.RunId || current.StoredCheckpoint != source.StoredCheckpoint)
                throw new InvalidOperationException("Der Sitzungskontext hat sich während der GPU-Wartezeit geändert. Bitte den aktuellen Stand erneut komprimieren.");
            var selectedRequest = source.Request;
            if (!string.IsNullOrWhiteSpace(request.PreferredModelId))
                selectedRequest = selectedRequest with { PreferredCodingModelId = request.PreferredModelId,
                    PreferredGeneralModelId = request.PreferredModelId };
            else if (saved.SelectedModelId is { } selectedModel)
                selectedRequest = selectedRequest with { PreferredCodingModelId = selectedModel, PreferredGeneralModelId = selectedModel };
            if (request.MaximumContextTokens is { } maximum)
                selectedRequest = selectedRequest with { Limits = (selectedRequest.Limits ?? new RunLimits()) with { MaximumContextTokens = maximum } };
            var selection = await _router.SelectAsync(selectedRequest, cancellationToken).ConfigureAwait(false);
            var contextLength = Math.Min(selection.ContextLength, selectedRequest.Limits?.MaximumContextTokens ?? selection.ContextLength);
            var plan = CodingContextCompactor.Plan(saved.Messages, contextLength, saved.WorkingState,
                includeWorkingStateInBudget: !saved.WorkingStatePromptIncluded, force: true);
            if (plan is null)
                return new(source.RunId, false, before, before, saved.CompactionCount,
                    "Der aktuelle Auftrag und seine neueste Antwort bleiben erhalten; es gibt noch keinen älteren Kontext zum Komprimieren.");
            var condensed = await _modelRuntime.CompleteChatAsync(selection.ModelId, plan.SummaryRequest, [],
                maximumOutputTokens: 4096, modelRole: selection.Role,
                reasoningEffort: _modelRuntime.ResolveCompactionReasoningEffort(selection.ModelId, selection.Role),
                requiredContextLength: contextLength, cancellationToken: cancellationToken).ConfigureAwait(false);
            var compacted = CodingContextCompactor.Complete(plan, condensed.Content);
            var after = ContextPlanner.EstimateTokens(compacted);
            if (after >= before)
                return new(source.RunId, false, before, before, saved.CompactionCount,
                    "Der vorhandene Kontext ist bereits kompakt; eine längere Zusammenfassung wurde nicht übernommen.");
            var replacement = saved with { Messages = compacted, CompactionCount = saved.CompactionCount + 1,
                WorkingStatePromptIncluded = false };
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _repository.TrySaveCompactedSessionAsync(source, replacement, cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("Der Sitzungskontext wurde während der Komprimierung geändert oder ein AI-Lauf gestartet. Der neuere Arbeitsstand bleibt erhalten; bitte nach Laufende erneut komprimieren.");
            return new(source.RunId, true, before, after, replacement.CompactionCount,
                $"Kontext komprimiert: {plan.ArchivedMessages} ältere Modellnachrichten zusammengefasst. Auftrag, Arbeitsplan und offene Werkzeugaufrufe bleiben erhalten; der vollständige Chatverlauf bleibt unverändert.");
        }
        finally
        {
            lock (_activeGate) _compactingSessions.Remove(request.SessionId);
        }
    }
}

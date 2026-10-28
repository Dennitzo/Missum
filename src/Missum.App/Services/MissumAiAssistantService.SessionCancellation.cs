using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal async Task CancelPersistedSessionRunsAsync(Guid sessionId, CancellationToken token = default)
    {
        var targets = (await runs.ListPendingForSessionAsync(sessionId, token).ConfigureAwait(false))
            .Where(run => run.SessionId == sessionId).ToArray();
        if (targets.Length == 0) return;

        // Cancellation intent must survive an offline gateway and a client
        // restart before these rows disappear from the resumable-run query.
        foreach (var run in targets)
            if (!string.IsNullOrWhiteSpace(run.ServerRunId))
                await SetPendingRunCancellationAsync(run.ServerRunId, pending: true).ConfigureAwait(false);
            else
                await SetPendingRunKeyCancellationAsync(run.SessionId, run.IdempotencyKey).ConfigureAwait(false);
        foreach (var run in targets)
        {
            var message = await chats.GetMessageAsync(run.AssistantMessageId, CancellationToken.None).ConfigureAwait(false);
            if (message is not null)
                await chats.UpdateMessageAsync(message.Id, message.Content, MessageStatus.Cancelled,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            await runs.UpdateAsync(run.Id, run.ServerRunId, run.LastEventId, "cancelled", run.SelectedModel,
                "client.agent_profile_removed", CancellationToken.None).ConfigureAwait(false);
        }

        // A reattach may have taken its snapshot before the durable stop above.
        // Capture only this session; another session can already own the gate.
        await CancelCurrentAndWaitAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        foreach (var run in targets)
            await FinishPersistedSessionCancellationAsync(run).ConfigureAwait(false);

        var ids = targets.Where(run => !string.IsNullOrWhiteSpace(run.ServerRunId))
            .Select(run => run.ServerRunId!).Distinct(StringComparer.Ordinal).ToArray();
        var remaining = await CancelPersistedServerRunsAsync(ids, "agent profile removal", CancellationToken.None).ConfigureAwait(false);
        foreach (var id in ids.Except(remaining, StringComparer.Ordinal))
            await SetPendingRunCancellationAsync(id, pending: false).ConfigureAwait(false);
        await RetryPendingRunKeyCancellationsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FinishPersistedSessionCancellationAsync(MissumAiRunRecord run)
    {
        var message = await chats.GetMessageAsync(run.AssistantMessageId, CancellationToken.None).ConfigureAwait(false);
        if (message is null) return;
        foreach (var child in SubagentChatState.Read([message], settings.DataDirectory)
            .Where(child => child.SessionId == run.SessionId && child.IsRunning && child.IsAuthorizedConsumer(run.ServerRunId!)))
        {
            var previous = message.ToolSteps?.FirstOrDefault(step => step.Id == child.ReceiptId);
            if (previous is null) continue;
            var at = NextToolStepUpdate(previous);
            var stopped = child with
            {
                Status = "cancelled", RunStatus = "Gestoppt", RunDetail = null, GenerationState = "cancelled",
                GenerationUpdatedAt = at, ProjectionRevision = child.ProjectionRevision + 1,
                TranscriptRevision = child.TranscriptRevision + 1,
                AssistantMessage = SubagentChatState.Finish(child.AssistantMessage, MessageStatus.Cancelled, at),
            };
            await chats.SaveToolStepAsync(message.Id, previous with
            {
                Status = "cancelled", OutputJson = await stopped.PersistAsync(settings.DataDirectory).ConfigureAwait(false),
                CompletedAt = at, UpdatedAt = at,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        message = await chats.GetMessageAsync(message.Id, CancellationToken.None).ConfigureAwait(false) ?? message;
        foreach (var step in (message.ToolSteps ?? []).Where(step => step.Status == "running"))
            await chats.SaveToolStepAsync(message.Id, CompleteOpenToolStep(step, "cancelled", null), CancellationToken.None).ConfigureAwait(false);
        await chats.UpdateMessageAsync(message.Id, message.Content, MessageStatus.Cancelled,
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        await runs.UpdateAsync(run.Id, run.ServerRunId, run.LastEventId, "cancelled", run.SelectedModel,
            "client.agent_profile_removed", CancellationToken.None).ConfigureAwait(false);
        await EndResearchProgressAsync(run, "cancelled").ConfigureAwait(false);
    }
}

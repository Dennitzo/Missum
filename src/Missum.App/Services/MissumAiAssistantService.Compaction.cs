using Missum.Ai.Client;
using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal const string NoCompactionDetailPrefix = "Keine Kontextkomprimierung erforderlich.";

    public Task<ChatMessage> CompactSessionContextAsync(Guid sessionId,
        Func<MissumAiAssistantUpdate, Task> update, CancellationToken cancellationToken = default) =>
        CompactSessionContextAsync(sessionId, update, connection.CreateClientAsync, cancellationToken);

    internal async Task<ChatMessage> CompactSessionContextAsync(Guid sessionId,
        Func<MissumAiAssistantUpdate, Task> update,
        Func<CancellationToken, Task<MissumAiClient>> createClient, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var submittedSettings = CurrentSettings;
        var model = submittedSettings.SelectedModel;
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Beende den laufenden AI-Auftrag, bevor du den Kontext komprimierst.");
        var completion = BeginActiveRun(sessionId, null, cancellationToken);
        ChatMessage? message = null;
        AssistantToolStep? receipt = null;
        try
        {
            var session = await chats.GetSessionAsync(sessionId, _activeCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Die Sitzung wurde nicht gefunden.");
            var contextLimit = string.IsNullOrWhiteSpace(model) ? (int?)null : ModelContextSelection.Resolve(submittedSettings, model,
                ModelContextProfiles.ResolveMaximum(model, session.ChatMode is ChatMode.Coding or ChatMode.ClaudeScience ? "coding" : "general"));
            message = await chats.AddMessageAsync(sessionId, ChatRole.Assistant, "", MessageStatus.Streaming,
                cancellationToken: _activeCancellation.Token).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            receipt = new("manual-compaction:" + message.Id.ToString("N"), "context.compact", "running",
                "Der gespeicherte Kontext dieser Sitzung wird komprimiert. Der Chatverlauf bleibt erhalten.",
                ContentOffset: 0, StartedAt: now, UpdatedAt: now);
            await SaveAsync("running", receipt.Detail!, MessageStatus.Streaming, MissumAiAssistantUpdateKind.Started).ConfigureAwait(false);
            using var client = await createClient(_activeCancellation.Token).ConfigureAwait(false);
            await connection.WaitForGatewayAsync(client, _activeCancellation.Token).ConfigureAwait(false);
            var result = await client.CompactSessionContextAsync(new(sessionId.ToString("D"), model, contextLimit),
                _activeCancellation.Token).ConfigureAwait(false);
            _activeCancellation.Token.ThrowIfCancellationRequested();
            await SaveAsync("completed", result.Changed ? result.Detail : NoCompactionDetailPrefix + " " + result.Detail,
                MessageStatus.Completed, MissumAiAssistantUpdateKind.Completed,
                result.SourceRunId is null ? null : result.AfterTokenEstimate, result.Changed).ConfigureAwait(false);
            return message;
        }
        catch (Exception exception) when (message is not null && receipt is not null && exception is not OutOfMemoryException)
        {
            var cancelled = exception is OperationCanceledException;
            var detail = exception is MissumAiApiException { StatusCode: System.Net.HttpStatusCode.NotFound }
                ? "Der verbundene Missum-Gateway unterstützt die manuelle Kontextkomprimierung noch nicht (HTTP 404). "
                    + "Beende Missum und starte es nach der Gateway-Aktualisierung erneut. Der bisherige Sitzungskontext bleibt unverändert."
                : exception.Message;
            await SaveAsync(cancelled ? "cancelled" : "failed", cancelled
                ? "Die Kontextkomprimierung wurde abgebrochen. Der Chatverlauf bleibt erhalten."
                : detail, cancelled ? MessageStatus.Cancelled : MessageStatus.Failed,
                cancelled ? MissumAiAssistantUpdateKind.Cancelled : MissumAiAssistantUpdateKind.Failed).ConfigureAwait(false);
            return message;
        }
        finally { await FinishActiveRunAsync(completion).ConfigureAwait(false); }

        async Task SaveAsync(string status, string detail, MessageStatus messageStatus,
            MissumAiAssistantUpdateKind kind, int? contextUsed = null, bool compactionPerformed = false)
        {
            var now = DateTimeOffset.UtcNow;
            receipt = await chats.SaveToolStepDeltaAsync(message!.Id, receipt! with
            { Status = status, Detail = detail,
                Explanation = status == "completed" && !compactionPerformed ? NoCompactionDetailPrefix : null,
                UpdatedAt = now, CompletedAt = status == "running" ? null : now }, CancellationToken.None).ConfigureAwait(false);
            await chats.UpdateMessageAsync(message.Id, "", messageStatus,
                errorMessage: messageStatus == MessageStatus.Failed ? detail : null, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            message = message with { Status = messageStatus, ToolSteps = [receipt], UpdatedAt = now,
                Error = messageStatus == MessageStatus.Failed ? detail : null };
            var notification = new MissumAiAssistantUpdate(kind, message, Status: status == "running" ? "Kontext wird komprimiert"
                : status == "completed" ? compactionPerformed ? "Kontextkomprimierung abgeschlossen" : "Keine Kontextkomprimierung erforderlich"
                : status == "cancelled" ? "Kontextkomprimierung abgebrochen" : "Kontextkomprimierung fehlgeschlagen",
                Detail: detail, Error: message.Error, ContextUsed: contextUsed, ContextWasCompacted: compactionPerformed,
                ToolStep: receipt);
            if (kind != MissumAiAssistantUpdateKind.Started)
                await update(notification with { Kind = MissumAiAssistantUpdateKind.Status }).ConfigureAwait(false);
            await update(notification).ConfigureAwait(false);
        }
    }
}

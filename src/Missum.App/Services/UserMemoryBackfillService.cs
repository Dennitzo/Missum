using Missum.Core.Chat;
using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;
using Missum.Ai.Contracts;

namespace Missum.App.Services;

/// <summary>
/// Reviews existing chats in small, resumable batches. The cheap part runs on the CPU: walking
/// the sessions, screening every user message and remembering the position. Only the messages
/// that the screening flagged are handed to the model, and only a few of them per batch, so an
/// old archive never blocks a live conversation. A confirmed statement is written and the profile
/// is rebuilt immediately, which makes it visible in the very next request of any other chat.
/// </summary>
public sealed class UserMemoryBackfillService(
    IChatRepository chats,
    IUserMemoryStore memories,
    UserMemoryRecognitionService recognition,
    MissumAiConnectionService connection)
{
    public const int MaximumMessagesPerBatch = 40;
    public const int MaximumModelCallsPerBatch = 3;
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(45);

    public sealed record Report(
        int ScannedMessages,
        int ModelReviews,
        int Stored,
        int Forgotten,
        bool Finished,
        string? Error = null);

    /// <summary>Runs one bounded batch. Safe to call again after a restart or a cancel.</summary>
    public async Task<Report> RunBatchAsync(string? modelId, CancellationToken cancellationToken, int? contextLimit = null)
    {
        if (UserMemoryService.Disabled) return new Report(0, 0, 0, 0, true);
        var cursor = await memories.GetBackfillCursorAsync(cancellationToken).ConfigureAwait(false);

        var sessions = (await chats.ListSessionsAsync(search: null, cancellationToken).ConfigureAwait(false))
            .OrderBy(session => session.CreatedAt)
            .ThenBy(session => session.Id)
            .ToList();

        Missum.Ai.Client.MissumAiClient? client = null;
        try
        {
            var scanned = 0;
            var reviews = 0;
            var stored = 0;
            var forgotten = 0;
            var processed = cursor?.ProcessedMessages ?? 0;
            var finished = true;
            string? error = null;

            foreach (var session in sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cursor is not null && CompareSession(session, cursor) < 0) continue;

                var messages = (await chats.ListMessagesAsync(session.Id, cancellationToken).ConfigureAwait(false))
                    .Where(message => message.Role == ChatRole.User)
                    .OrderBy(message => message.CreatedAt)
                    .ThenBy(message => message.Id)
                    .ToList();

                foreach (var message in messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = message.Id.ToString("D");
                    if (cursor is not null && session.Id.ToString("D") == cursor.LastSessionId
                        && !IsAfterMessage(message, cursor))
                    {
                        continue;
                    }

                    if (scanned >= MaximumMessagesPerBatch)
                    {
                        finished = false;
                        break;
                    }

                    scanned++;
                    if (await memories.IsMessageProcessedAsync(key, cancellationToken).ConfigureAwait(false))
                    {
                        cursor = Advance(session, message, processed);
                        continue;
                    }

                    var intent = UserMemoryIntentScanner.Scan(message.Content);
                    if (!intent.WorthReviewing || (intent.AsksForStoredProfile
                            && !intent.ExplicitPermanence && !intent.ForgetRequest))
                    {
                        var decision = intent.AsksForStoredProfile ? "profile-query" : "no-signal";
                        await memories.MarkMessageProcessedAsync(key, session.Id.ToString("D"), decision, null,
                            cancellationToken).ConfigureAwait(false);
                        processed++;
                        cursor = Advance(session, message, processed);
                        continue;
                    }

                    if (reviews >= MaximumModelCallsPerBatch || string.IsNullOrWhiteSpace(modelId))
                    {
                        finished = false;
                        break;
                    }

                    try
                    {
                        client ??= await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
                    {
                        error = exception.Message;
                        finished = false;
                        break;
                    }
                    reviews++;
                    var outcome = await recognition.ObserveAsync(
                        client, session, message, intent, "backfill", modelId, cancellationToken, contextLimit)
                        .ConfigureAwait(false);
                    if (outcome.Error is not null)
                    {
                        error = outcome.Error;
                        finished = false;
                        break;
                    }

                    await recognition.RecordProcessedAsync(message, outcome, cancellationToken).ConfigureAwait(false);
                    processed++;
                    stored += outcome.Stored.Count;
                    forgotten += outcome.Forgotten.Count;
                    cursor = Advance(session, message, processed);
                }

                if (!finished) break;
            }

            if (cursor is not null)
            {
                await memories.SaveBackfillCursorAsync(cursor, cancellationToken).ConfigureAwait(false);
            }

            return new Report(scanned, reviews, stored, forgotten, finished, error);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Works through the archive in batches with a pause in between. Runs until the history is
    /// complete or the application shuts down; the saved position makes the next start continue.
    /// </summary>
    public async Task<Report> RunUntilDoneAsync(
        Func<AppSettings> readSettings,
        IAssistantRunScheduler scheduler,
        CancellationToken cancellationToken)
    {
        if (UserMemoryService.Disabled) return new Report(0, 0, 0, 0, true);
        var total = new Report(0, 0, 0, 0, false);
        await memories.MigrateLegacyEntriesAsync(cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            Report? batch = null;
            using (var idleBatch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                void InterruptForChat(object? sender, AssistantRunScheduleChangedEventArgs args)
                {
                    if (args.Snapshot.IsIdle) return;
                    try { idleBatch.Cancel(); }
                    catch (ObjectDisposedException) { /* The batch completed while the event was being published. */ }
                }

                scheduler.Changed += InterruptForChat;
                try
                {
                    if (scheduler.Snapshot.IsIdle)
                    {
                        var settings = readSettings();
                        var modelId = settings.SelectedModel?.Trim();
                        var limit = modelId is null ? (int?)null : ModelContextSelection.Resolve(settings, modelId,
                            ModelContextProfiles.ResolveMaximum(modelId, "general"));
                        batch = await RunBatchAsync(modelId, idleBatch.Token, limit).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && idleBatch.IsCancellationRequested)
                {
                    // A foreground prompt takes precedence over a backfill review.
                }
                finally
                {
                    scheduler.Changed -= InterruptForChat;
                }
            }

            if (batch is not null)
            {
                total = total with
                {
                    ScannedMessages = total.ScannedMessages + batch.ScannedMessages,
                    ModelReviews = total.ModelReviews + batch.ModelReviews,
                    Stored = total.Stored + batch.Stored,
                    Forgotten = total.Forgotten + batch.Forgotten,
                    Finished = batch.Finished,
                    Error = batch.Error,
                };
                if (batch.Finished) break;
            }
            await Task.Delay(IdleDelay, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static int CompareSession(ChatSession session, UserMemoryBackfillCursor cursor)
    {
        if (!Guid.TryParse(cursor.LastSessionId, out var cursorSessionId)) return 1;
        var byTime = session.CreatedAt.CompareTo(cursor.LastSessionCreatedAt);
        return byTime == 0 ? session.Id.CompareTo(cursorSessionId) : byTime;
    }

    private static bool IsAfterMessage(ChatMessage message, UserMemoryBackfillCursor cursor)
    {
        if (!Guid.TryParse(cursor.LastMessageId, out var cursorMessageId)) return true;
        var byTime = message.CreatedAt.CompareTo(cursor.LastMessageCreatedAt);
        return byTime > 0 || (byTime == 0 && message.Id.CompareTo(cursorMessageId) > 0);
    }

    private static UserMemoryBackfillCursor Advance(
        ChatSession session,
        ChatMessage message,
        long processed) => new(
            session.Id.ToString("D"),
            session.CreatedAt,
            message.Id.ToString("D"),
            message.CreatedAt,
            processed,
            DateTimeOffset.UtcNow);
}

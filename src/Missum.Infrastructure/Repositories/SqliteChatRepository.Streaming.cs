using Microsoft.Data.Sqlite;
using Missum.Core.Chat;
using Missum.Core.Models;
using Missum.Infrastructure.Storage;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteChatRepository
{
    public Task NormalizeToolStepsAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        database.WriteAsync((connection, transaction, token) =>
            ConversationToolStepStorage.EnsureNormalizedAsync(connection, transaction, messageId, token), cancellationToken);

    public Task<AssistantToolStep> SaveToolStepDeltaAsync(Guid messageId, AssistantToolStep toolStep,
        CancellationToken cancellationToken = default)
    {
        ValidateToolStep(toolStep);
        return database.WriteAsync((connection, transaction, token) =>
            SaveToolStepCoreAsync(connection, transaction, messageId, toolStep, token), cancellationToken);
    }

    private static async Task<AssistantToolStep> SaveToolStepCoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, AssistantToolStep toolStep, CancellationToken token)
    {
        await ConversationToolStepStorage.EnsureNormalizedAsync(connection, transaction, messageId, token).ConfigureAwait(false);
        var result = await ConversationToolStepStorage.UpsertChangeAsync(connection, transaction, messageId, toolStep, token).ConfigureAwait(false);
        if (!result.Changed) return result.Step;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Keep the session ordering and conversation revision synchronized, without rewriting its transcript blob.
        command.CommandText = """
            UPDATE chat_messages SET revision=revision+1,updated_at=$now WHERE id=$message;
            UPDATE chat_sessions SET conversation_revision=conversation_revision+1,updated_at=$now
                WHERE id=(SELECT session_id FROM chat_messages WHERE id=$message);
            DELETE FROM chat_message_ui_headers WHERE message_id=$message;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return result.Step;
    }

    public Task CommitStreamAsync(ConversationStreamBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ValidateStepBatch(batch.Content, batch.ToolSteps);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.ServerRunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batch.RunState);
        ArgumentOutOfRangeException.ThrowIfNegative(batch.LastEventId);
        return database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT last_event_id FROM missum_ai_runs
                WHERE id=$run AND assistant_message_id=$message AND server_run_id=$server;
                """;
            command.Parameters.AddWithValue("$run", batch.LocalRunId.ToString("D"));
            command.Parameters.AddWithValue("$message", batch.MessageId.ToString("D"));
            command.Parameters.AddWithValue("$server", batch.ServerRunId);
            var current = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (current is not long cursor) throw new InvalidOperationException("Der Streaming-Checkpoint gehört nicht zum gespeicherten AI-Lauf.");
            if (batch.LastEventId < cursor) return; // An older replay cannot replace newer visible state.
            await ConversationToolStepStorage.EnsureNormalizedAsync(connection, transaction, batch.MessageId, token).ConfigureAwait(false);
            var stepsChanged = false;
            foreach (var step in batch.ToolSteps)
                stepsChanged |= (await ConversationToolStepStorage.UpsertChangeAsync(connection, transaction, batch.MessageId, step, token).ConfigureAwait(false)).Changed;
            await UpdateStreamMessageAsync(connection, transaction, batch.MessageId, batch.Content, batch.Status, stepsChanged,
                batch.SourceUpdatedAt, token).ConfigureAwait(false);
            command.CommandText = """
                UPDATE missum_ai_runs SET last_event_id=$event,state=$state,selected_model=COALESCE($model,selected_model),
                    error_code=CASE WHEN state=$state AND $state IN ('completed','failed','cancelled','interrupted')
                        THEN COALESCE($error,error_code) ELSE $error END,updated_at=$now WHERE id=$run;
                """;
            command.Parameters.AddWithValue("$event", batch.LastEventId);
            command.Parameters.AddWithValue("$state", batch.RunState);
            command.Parameters.AddWithValue("$model", (object?)batch.SelectedModel ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)batch.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToDb());
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken);
    }

    private static async Task UpdateStreamMessageAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, string content, MessageStatus status, bool stepsChanged, DateTimeOffset? sourceUpdatedAt, CancellationToken token)
    {
        var visible = ChatContentSanitizer.Sanitize(content);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chat_tool_steps WHERE message_id=$message AND content_offset>$length);";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$length", visible.Length);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Die Werkzeugchronologie passt nicht zum neuen Nachrichtentext.");
        command.CommandText = """
            UPDATE chat_messages SET content=$content,status=$status,revision=revision+1,updated_at=$now
            WHERE id=$message AND (content<>$content OR status<>$status OR $steps=1) RETURNING session_id;
            """;
        command.Parameters.AddWithValue("$content", visible);
        command.Parameters.AddWithValue("$status", SqliteMapping.EnumName(status));
        // The timer can commit an acknowledged prefix after the next live text
        // fragment was already shown. Its clock must describe that prefix, not
        // the later disk write; otherwise a snapshot can roll the live UI back.
        command.Parameters.AddWithValue("$now", (sourceUpdatedAt ?? DateTimeOffset.UtcNow).ToDb());
        command.Parameters.AddWithValue("$steps", stepsChanged ? 1 : 0);
        if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not string session) return;
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$sessionNow", DateTimeOffset.UtcNow.ToDb());
        command.CommandText = """
            UPDATE chat_sessions SET conversation_revision=conversation_revision+1,updated_at=$sessionNow WHERE id=$session;
            DELETE FROM chat_message_ui_headers WHERE message_id=$message AND $steps=1;
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void ValidateStepBatch(string content, IReadOnlyList<AssistantToolStep> steps)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Select(step => step.Id).Distinct(StringComparer.Ordinal).Count() != steps.Count)
            throw new ArgumentException("Ungültige Werkzeugchronologie.", nameof(steps));
        var length = ChatContentSanitizer.Sanitize(content).Length;
        foreach (var step in steps)
        {
            ValidateToolStep(step);
            if (step.ContentOffset > length) throw new ArgumentException("Werkzeugposition liegt außerhalb des Nachrichtentextes.", nameof(steps));
        }
    }
}

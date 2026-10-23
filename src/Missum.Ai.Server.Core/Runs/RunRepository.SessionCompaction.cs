using Missum.Ai.Contracts;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

internal sealed record SessionCompactionSource(string RunId, RunRequest Request,
    AgentRunCheckpoint Checkpoint, string StoredCheckpoint);

public sealed partial class RunRepository
{
    private const string SessionActivePredicate = """
        EXISTS (SELECT 1 FROM runs active WHERE active.state IN ('Queued','Running','WaitingForClient')
            AND (json_extract(active.request_json, '$.sessionId') = $session
                OR json_extract(active.request_json, '$.subagent.parentSessionId') = $session))
        """;

    internal async Task<SessionCompactionSource?> GetIdleSessionCompactionSourceAsync(string sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$session", sessionId);
        command.CommandText = "SELECT " + SessionActivePredicate + ";";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Die Sitzung hat einen aktiven AI-Lauf. Bitte den Lauf zuerst beenden und danach den Kontext komprimieren.");
        command.CommandText = """
            SELECT r.run_id, r.request_json, COALESCE(c.checkpoint_json, live.checkpoint_json), r.state
            FROM runs r LEFT JOIN coding_session_contexts c ON c.run_id = r.run_id
            LEFT JOIN run_checkpoints live ON live.run_id = r.run_id
            WHERE json_extract(r.request_json, '$.sessionId') = $session
                AND json_extract(r.request_json, '$.subagent') IS NULL
                AND r.state IN ('Completed','Failed','Cancelled','Interrupted')
                AND COALESCE(c.checkpoint_json, live.checkpoint_json) IS NOT NULL
            ORDER BY r.created_at DESC, r.run_id DESC LIMIT 1;
            """;
        string runId;
        string stored;
        string state;
        RunRequest request;
        AgentRunCheckpoint checkpoint;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            runId = reader.GetString(0);
            request = DeserializeRequest(reader.GetString(1), _database.JsonOptions)
                ?? throw new InvalidOperationException("Der gespeicherte Sitzungsauftrag konnte nicht geladen werden.");
            stored = reader.GetString(2);
            checkpoint = DeserializeSessionCheckpoint(stored)
                ?? throw new InvalidOperationException("Der gespeicherte Sitzungskontext konnte nicht geladen werden.");
            state = reader.GetString(3);
        }
        if (state != "Completed")
            checkpoint = RestoreInterruptedVisibleTail(checkpoint,
                await GetContinuationEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false));
        return new(runId, request, checkpoint, stored);
    }

    internal async Task<bool> TrySaveCompactedSessionAsync(SessionCompactionSource source,
        AgentRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", source.RunId);
        command.Parameters.AddWithValue("$session", source.Request.SessionId!);
        command.Parameters.AddWithValue("$old", source.StoredCheckpoint);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(checkpoint, _database.JsonOptions));
        // The summary is only committed against the exact idle checkpoint it
        // read. A concurrent prompt, continuation, or second compression wins
        // instead of being overwritten. Neither journal nor request is changed.
        command.CommandText = """
            INSERT INTO coding_session_contexts(run_id, checkpoint_json)
            SELECT r.run_id, $json FROM runs r
            LEFT JOIN coding_session_contexts c ON c.run_id = r.run_id
            LEFT JOIN run_checkpoints live ON live.run_id = r.run_id
            WHERE r.run_id = $run AND r.state IN ('Completed','Failed','Cancelled','Interrupted')
                AND COALESCE(c.checkpoint_json, live.checkpoint_json) = $old
                AND NOT
            """ + "\n" + SessionActivePredicate + "\n" + """

                AND NOT EXISTS (SELECT 1 FROM runs newer
                    LEFT JOIN coding_session_contexts nc ON nc.run_id = newer.run_id
                    LEFT JOIN run_checkpoints nl ON nl.run_id = newer.run_id
                    WHERE json_extract(newer.request_json, '$.sessionId') = $session
                        AND json_extract(newer.request_json, '$.subagent') IS NULL
                        AND COALESCE(nc.checkpoint_json, nl.checkpoint_json) IS NOT NULL
                        AND (newer.created_at > r.created_at OR newer.created_at = r.created_at AND newer.run_id > r.run_id))
            ON CONFLICT(run_id) DO UPDATE SET checkpoint_json = excluded.checkpoint_json
            WHERE coding_session_contexts.checkpoint_json = $old;
            """;
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
        command.CommandText = "UPDATE run_checkpoints SET checkpoint_json = $json WHERE run_id = $run AND checkpoint_json = $old;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

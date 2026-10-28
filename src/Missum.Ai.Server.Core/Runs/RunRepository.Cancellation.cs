using System.Globalization;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    private async Task<(RunSnapshot Snapshot, bool Created)> CreateOrRestoreByKeyAsync(
        RunRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey;
        if (key?.Length > 128) throw new ArgumentException("Idempotency-Key may contain at most 128 characters.", nameof(idempotencyKey));
        var now = DateTimeOffset.UtcNow;
        var timestamp = MissumAiDatabase.FormatTimestamp(now);
        var requestJson = JsonSerializer.Serialize(request, _database.JsonOptions);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Cancellation and creation take this same immediate writer lock. The
        // tombstone cannot be missed regardless of which HTTP request arrives first.
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", (object?)key ?? DBNull.Value);
        if (key is not null)
        {
            command.CommandText = """
                SELECT r.run_id,r.state,r.mode,r.selected_model,r.session_title,COALESCE(MAX(e.id),0),
                    r.created_at,r.updated_at,r.error_code,r.request_json
                FROM runs r LEFT JOIN run_events e ON e.run_id=r.run_id
                WHERE r.idempotency_key=$key GROUP BY r.run_id;
                """;
            RunSnapshot? existing = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    existing = ReadSnapshot(reader);
                    var storedRequest = DeserializeRequest(reader.GetString(9), _database.JsonOptions)
                        ?? throw new InvalidDataException("Idempotent run request disappeared from storage.");
                    if (JsonSerializer.Serialize(storedRequest, _database.JsonOptions) != requestJson)
                        throw new InvalidOperationException("Idempotency-Key is already bound to a different run request.");
                }
            }
            if (existing is not null)
            {
                if (existing.State != RunState.Interrupted) return (existing, false);
                command.CommandText = "UPDATE runs SET state='Queued',error_code=NULL,updated_at=$now WHERE run_id=$run;";
                command.Parameters.AddWithValue("$now", timestamp);
                command.Parameters.AddWithValue("$run", existing.RunId);
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                _notifier.Notify(existing.RunId);
                return (existing with { State = RunState.Queued, ErrorCode = null, UpdatedAt = now }, true);
            }
        }
        command.CommandText = "SELECT session_id FROM run_cancel_keys WHERE idempotency_key=$key;";
        var cancelledSession = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (cancelledSession is not null && !string.Equals(cancelledSession, request.SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Idempotency-Key is already bound to a different session.");
        var cancelled = cancelledSession is not null;
        var runId = "run-" + Guid.NewGuid().ToString("N");
        command.CommandText = """
            INSERT INTO runs(run_id,idempotency_key,state,mode,request_json,error_code,created_at,updated_at)
            VALUES($run,$key,$state,$mode,$request,$error,$now,$now);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$state", cancelled ? "Cancelled" : "Queued");
        command.Parameters.AddWithValue("$mode", request.Mode.ToString());
        command.Parameters.AddWithValue("$request", requestJson);
        command.Parameters.AddWithValue("$error", cancelled ? "run.cancelled" : DBNull.Value);
        command.Parameters.AddWithValue("$now", timestamp);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        long lastEvent = 0;
        if (cancelled)
        {
            command.CommandText = """
                INSERT INTO run_events(run_id,event_type,data_json,created_at)
                VALUES($run,'run.cancelled','{"reason":"client","scope":"idempotencyKey"}',$now);
                SELECT last_insert_rowid();
                """;
            lastEvent = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return (new(runId, cancelled ? RunState.Cancelled : RunState.Queued, request.Mode, null, null, lastEvent,
            now, now, cancelled ? "run.cancelled" : null), true);
    }

    /// <summary>Persists Stop even when the Create acknowledgment and server run ID were lost.</summary>
    public async Task<string?> CancelByIdempotencyKeyAsync(RunCancelByKeyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SessionId) || request.SessionId.Length > 256 || request.SessionId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 128 || request.IdempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Abbruch benötigt sessionId und idempotencyKey mit höchstens 256 beziehungsweise 128 Zeichen.", nameof(request));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", request.IdempotencyKey);
        command.Parameters.AddWithValue("$session", request.SessionId);
        command.CommandText = "SELECT run_id,json_extract(request_json,'$.sessionId') FROM runs WHERE idempotency_key=$key;";
        string? runId = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1) || !string.Equals(reader.GetString(1), request.SessionId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Der abzubrechende Auftrag gehört nicht zu dieser Sitzung.");
                runId = reader.GetString(0);
            }
        }
        command.CommandText = "SELECT session_id FROM run_cancel_keys WHERE idempotency_key=$key;";
        var priorSession = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (priorSession is not null && !string.Equals(priorSession, request.SessionId, StringComparison.Ordinal))
            throw new InvalidOperationException("Der Abbruchschlüssel gehört bereits zu einer anderen Sitzung.");
        var timestamp = MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("$now", timestamp);
        command.CommandText = "INSERT OR IGNORE INTO run_cancel_keys(idempotency_key,session_id,cancelled_at) VALUES($key,$session,$now);";
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        if (runId is not null)
        {
            command.Parameters.AddWithValue("$run", runId);
            command.CommandText = """
                UPDATE runs SET state='Cancelled',error_code='run.cancelled',updated_at=$now
                WHERE run_id=$run AND state IN ('Queued','Running','WaitingForClient','Interrupted');
                """;
            changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            if (changed)
            {
                command.CommandText = """
                    INSERT INTO run_events(run_id,event_type,data_json,created_at)
                    VALUES($run,'run.cancelled','{"reason":"client","scope":"idempotencyKey"}',$now);
                    DELETE FROM run_provider_retries WHERE run_id=$run;
                    UPDATE client_tool_proposals SET expires_at=$now WHERE run_id=$run;
                    """;
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (changed) _notifier.Notify(runId!);
        return runId;
    }
}

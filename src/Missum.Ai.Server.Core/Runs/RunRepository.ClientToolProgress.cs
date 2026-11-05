using System.Globalization;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using Microsoft.Data.Sqlite;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    public const int MaximumCommandProgressCharacters = 16_384;

    public async Task<ClientToolProgress?> GetClientToolProgressAsync(string runId, string proposalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT progress_json FROM client_tool_progress WHERE run_id=$run AND proposal_id=$proposal;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<ClientToolProgress>(json, _database.JsonOptions) : null;
    }

    public async Task<ClientToolProgressResponse> SaveClientToolProgressAsync(string runId, ClientToolProgress progress,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandProgress(progress);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await ValidatePendingCommandAsync(connection, transaction, runId, progress.ProposalId, cancellationToken).ConfigureAwait(false);
        await using (var prior = connection.CreateCommand())
        {
            prior.Transaction = transaction;
            prior.CommandText = "SELECT revision,progress_json FROM client_tool_progress WHERE proposal_id=$proposal AND run_id=$run;";
            prior.Parameters.AddWithValue("$proposal", progress.ProposalId);
            prior.Parameters.AddWithValue("$run", runId);
            await using var reader = await prior.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var previousRevision = reader.GetInt64(0);
                var previousJson = reader.GetString(1);
                if (previousRevision == progress.Revision
                    && reader.GetString(1) != JsonSerializer.Serialize(progress, _database.JsonOptions))
                    throw new ArgumentException("Dieselbe Fortschrittsrevision darf keinen anderen Inhalt enthalten.");
                if (previousRevision >= progress.Revision)
                {
                    // Replayed and older observations cannot replace newer process state.
                    await reader.DisposeAsync().ConfigureAwait(false);
                    var previousResponse = await ReadCommandStopAsync(connection, transaction, runId, progress.ProposalId, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return previousResponse;
                }
                var previous = JsonSerializer.Deserialize<ClientToolProgress>(previousJson, _database.JsonOptions)!;
                if (progress.ElapsedMilliseconds < previous.ElapsedMilliseconds
                    || progress.ProcessId is { } pid && previous.ProcessId is { } priorPid && pid != priorPid)
                    throw new ArgumentException("Prozessfortschritt darf weder Zeit zurücksetzen noch seine Prozesskennung wechseln.");
            }
        }
        await using (var save = connection.CreateCommand())
        {
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO client_tool_progress(proposal_id,run_id,revision,progress_json,updated_at)
                VALUES($proposal,$run,$revision,$json,$now)
                ON CONFLICT(proposal_id) DO UPDATE SET revision=excluded.revision,
                    progress_json=excluded.progress_json,updated_at=excluded.updated_at;
                """;
            save.Parameters.AddWithValue("$proposal", progress.ProposalId);
            save.Parameters.AddWithValue("$run", runId);
            save.Parameters.AddWithValue("$revision", progress.Revision);
            save.Parameters.AddWithValue("$json", JsonSerializer.Serialize(progress, _database.JsonOptions));
            save.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var response = await ReadCommandStopAsync(connection, transaction, runId, progress.ProposalId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(runId);
        return response;
    }

    public async Task<bool> RequestClientToolStopAsync(string runId, string proposalId, string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 2000) throw new ArgumentException("Ein Prozessstopp benötigt eine kurze konkrete Begründung.");
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText = "SELECT COUNT(*) FROM client_tool_proposals WHERE run_id=$run AND proposal_id=$proposal AND name=$name;";
            ownership.Parameters.AddWithValue("$run", runId);
            ownership.Parameters.AddWithValue("$proposal", proposalId);
            ownership.Parameters.AddWithValue("$name", ClientToolNames.CodingCommand);
            if (Convert.ToInt64(await ownership.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Der angeforderte Prozess gehört nicht zu diesem Lauf.");
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE client_tool_progress SET stop_requested=1, stop_reason=$reason
            WHERE run_id=$run AND proposal_id=$proposal AND stop_requested=0
              AND EXISTS(SELECT 1 FROM runs WHERE run_id=$run AND state IN ('Queued','Running','WaitingForClient'))
              AND NOT EXISTS(SELECT 1 FROM client_tool_results WHERE run_id=$run AND proposal_id=$proposal);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$reason", reason);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    internal async Task<bool> TryQueueClientToolProgressAsync(string runId, string proposalId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET state='Queued',updated_at=$now
            WHERE run_id=$run AND state='WaitingForClient'
              AND EXISTS (SELECT 1 FROM run_checkpoints WHERE run_id=$run
                  AND json_extract(checkpoint_json,'$.pendingProposalId')=$proposal)
              AND EXISTS (SELECT 1 FROM client_tool_progress p JOIN run_checkpoints c ON c.run_id=p.run_id
                  WHERE p.run_id=$run AND p.proposal_id=$proposal
                    AND json_extract(p.progress_json,'$.elapsedMilliseconds')>=30000
                    AND (COALESCE(json_extract(c.checkpoint_json,'$.commandMonitoring.proposalId'),'')<>$proposal
                        OR json_extract(c.checkpoint_json,'$.commandMonitoring.lastAttemptAt') IS NULL
                        OR p.revision>COALESCE(json_extract(c.checkpoint_json,'$.commandMonitoring.lastProgressRevision'),0)
                            AND (julianday($now)-julianday(json_extract(c.checkpoint_json,'$.commandMonitoring.lastAttemptAt')))*86400>=60))
              AND NOT EXISTS (SELECT 1 FROM client_tool_results WHERE run_id=$run AND proposal_id=$proposal);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static void ValidateCommandProgress(ClientToolProgress progress)
    {
        if (string.IsNullOrWhiteSpace(progress.ProposalId) || progress.ProposalId.Length > 200
            || progress.Revision <= 0 || progress.ElapsedMilliseconds < 0
            || progress.Stdout is null || progress.Stderr is null
            || progress.Stdout.Length > MaximumCommandProgressCharacters || progress.Stderr.Length > MaximumCommandProgressCharacters
            || progress.ProcessId is <= 0 || progress.CpuMilliseconds is { } cpu && (!double.IsFinite(cpu) || cpu < 0)
            || progress.WorkingSetBytes is < 0 || progress.LastOutputElapsedMilliseconds is < 0 || progress.OutputCharacters is < 0
            || progress.LastOutputElapsedMilliseconds > progress.ElapsedMilliseconds)
            throw new ArgumentException("Ungültige oder zu große Prozessfortschrittsdaten.");
    }

    private static async Task ValidatePendingCommandAsync(SqliteConnection connection, SqliteTransaction transaction,
        string runId, string proposalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM client_tool_proposals p JOIN runs r ON r.run_id=p.run_id
            WHERE p.run_id=$run AND p.proposal_id=$proposal AND p.name=$name
              AND r.state IN ('Queued','Running','WaitingForClient') AND p.expires_at>$now
              AND NOT EXISTS(SELECT 1 FROM client_tool_results result WHERE result.proposal_id=p.proposal_id);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        command.Parameters.AddWithValue("$name", ClientToolNames.CodingCommand);
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("Fortschritt gehört nicht zu einem offenen eigenen coding.command-Prozess.");
    }

    private static async Task<ClientToolProgressResponse> ReadCommandStopAsync(SqliteConnection connection,
        SqliteTransaction transaction, string runId, string proposalId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT stop_requested,stop_reason FROM client_tool_progress WHERE run_id=$run AND proposal_id=$proposal;";
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$proposal", proposalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetInt64(0) != 0, reader.IsDBNull(1) ? null : reader.GetString(1)) : new(false);
    }
}

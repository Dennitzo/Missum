using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    public async Task<string> GetContextCacheEpochAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cache_epoch FROM runtime_context_state WHERE singleton = 1;";
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing context cache epoch."));
    }

    public async Task<bool> HasActiveRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE state IN ('Queued','Running','WaitingForClient'));";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task<bool> ResetContextCacheEpochAsync(string epoch, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        if (epoch.Length > 128 || !epoch.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ArgumentException("Invalid context cache epoch.", nameof(epoch));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE state IN ('Queued','Running','WaitingForClient'));";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Context caches can only be reset while all runs are idle.");
        command.CommandText = "UPDATE runtime_context_state SET cache_epoch=$epoch WHERE singleton=1 AND cache_epoch != $epoch;";
        command.Parameters.AddWithValue("$epoch", epoch);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0;
        // Epoch selection retires old heads without deleting journals, checkpoints,
        // chat IDs or creating the session deletion tombstones used for true deletion.
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task<(AgentRunCheckpoint Checkpoint, string SourceRunId, bool WasInterrupted)?> GetProjectSessionHeadAsync(
        string currentRunId, RunRequest request, CancellationToken cancellationToken)
    {
        if (request.ProjectContext is null || string.IsNullOrWhiteSpace(request.SessionId)) return null;
        var workspace = request.Mode == RunMode.Coding ? request.CodingOptions?.WorkspacePath : request.WorkspacePath;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id, COALESCE(live.checkpoint_json, archive.checkpoint_json), r.state
            FROM session_context_heads h JOIN runs r ON r.run_id = h.run_id
            LEFT JOIN run_checkpoints live ON live.run_id = h.run_id
            LEFT JOIN coding_session_contexts archive ON archive.run_id = h.run_id
            WHERE h.session_id=$session AND h.workspace_key=$workspace COLLATE NOCASE AND h.role=$role
                AND h.project_key=$project AND h.cache_epoch=(SELECT cache_epoch FROM runtime_context_state WHERE singleton=1)
                AND r.run_id != $run AND r.state IN ('Completed','Failed','Cancelled','Interrupted')
                AND r.created_at < (SELECT created_at FROM runs WHERE run_id=$run)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", request.SessionId);
        command.Parameters.AddWithValue("$workspace", ProjectContextPolicy.NormalizeWorkspace(workspace));
        command.Parameters.AddWithValue("$role", request.Mode == RunMode.Coding ? "coding" : "general");
        command.Parameters.AddWithValue("$project", request.ProjectContext.ProjectKey);
        command.Parameters.AddWithValue("$run", currentRunId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1)) return null;
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(reader.GetString(1), _checkpointJsonOptions);
        return checkpoint is null ? null : (checkpoint, reader.GetString(0), reader.GetString(2) != "Completed");
    }
}

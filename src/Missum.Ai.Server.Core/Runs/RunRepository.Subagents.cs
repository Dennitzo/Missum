using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    internal async Task<bool> IsTerminalStateAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE run_id = $run AND state IN ('Completed', 'Failed', 'Cancelled', 'Interrupted'));";
        command.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    internal Task<RunSnapshot?> FindSubagentByOperationAsync(string operationId, CancellationToken cancellationToken = default) =>
        FindByIdempotencyKeyAsync("subagent:" + operationId, cancellationToken);

    /// <summary>The durable child and its exact context fork commit together.</summary>
    internal async Task<RunSnapshot> CreateSubagentAsync(RunRequest request, AgentRunCheckpoint checkpoint,
        string operationId, CancellationToken cancellationToken = default)
    {
        if (request.Subagent is null) throw new ArgumentException("A subagent relation is required.", nameof(request));
        if (await FindSubagentByOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is { } prior)
            return prior;
        var now = DateTimeOffset.UtcNow;
        var runId = $"run-{Guid.NewGuid():N}";
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runs(run_id, idempotency_key, state, mode, request_json, selected_model, created_at, updated_at)
            VALUES($run, $key, 'Queued', $mode, $request, $model, $now, $now);
            INSERT INTO run_checkpoints(run_id, checkpoint_json, updated_at) VALUES($run, $checkpoint, $now);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$key", "subagent:" + operationId);
        command.Parameters.AddWithValue("$mode", request.Mode.ToString());
        command.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request, _database.JsonOptions));
        command.Parameters.AddWithValue("$checkpoint", JsonSerializer.Serialize(checkpoint, _checkpointJsonOptions));
        command.Parameters.AddWithValue("$model", (object?)checkpoint.SelectedModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(now));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RunSnapshot(runId, RunState.Queued, request.Mode, checkpoint.SelectedModelId, null, 0, now, now);
    }

    internal async Task<IReadOnlyList<(RunSnapshot Snapshot, RunRequest Request)>> GetSubagentRunsAsync(
        string? parentRunId = null, bool activeOnly = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, request_json FROM runs
            WHERE json_extract(request_json, '$.subagent.parentRunId') IS NOT NULL
              AND ($parent IS NULL OR json_extract(request_json, '$.subagent.parentRunId') = $parent)
              AND ($active = 0 OR state IN ('Queued', 'Running', 'WaitingForClient'))
            ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$parent", (object?)parentRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$active", activeOnly ? 1 : 0);
        var requests = new List<(string RunId, RunRequest Request)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (DeserializeRequest(reader.GetString(1), _database.JsonOptions) is { } request)
                    requests.Add((reader.GetString(0), request));
        var results = new List<(RunSnapshot Snapshot, RunRequest Request)>();
        foreach (var (runId, request) in requests)
            if (await GetAsync(runId, cancellationToken).ConfigureAwait(false) is { } snapshot)
                results.Add((snapshot, request));
        return results;
    }

    internal async Task<long> GetSubagentForwardCursorAsync(string parentRunId, string childRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(CAST(json_extract(data_json, '$.event.id') AS INTEGER)), 0)
            FROM run_events WHERE run_id = $parent AND event_type = $type
              AND json_extract(data_json, '$.runId') = $child;
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$child", childRunId);
        command.Parameters.AddWithValue("$type", RunEventTypes.SubagentEvent);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>Replay-safe parent relay preserves the child's original event and run identity.</summary>
    internal async Task<bool> ForwardSubagentEventAsync(SubagentForwardedEvent forwarded,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $parent, $type, $data, $now WHERE NOT EXISTS (
                SELECT 1 FROM run_events WHERE run_id = $parent AND event_type = $type
                  AND json_extract(data_json, '$.runId') = $child
                  AND json_extract(data_json, '$.event.id') = $eventId);
            """;
        command.Parameters.AddWithValue("$parent", forwarded.ParentRunId);
        command.Parameters.AddWithValue("$child", forwarded.RunId);
        command.Parameters.AddWithValue("$eventId", forwarded.Event.Id);
        command.Parameters.AddWithValue("$type", RunEventTypes.SubagentEvent);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(forwarded, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (added) _notifier.Notify(forwarded.ParentRunId);
        return added;
    }

    internal async Task EnsureSubagentLifecycleEventAsync(string eventType, SubagentRunEvent data,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $parent, $type, $data, $now WHERE NOT EXISTS (
                SELECT 1 FROM run_events WHERE run_id = $parent AND event_type = $type
                  AND json_extract(data_json, '$.runId') = $child
                  AND json_extract(data_json, '$.state') = $state);
            """;
        command.Parameters.AddWithValue("$parent", data.ParentRunId);
        command.Parameters.AddWithValue("$child", data.RunId);
        command.Parameters.AddWithValue("$type", eventType);
        command.Parameters.AddWithValue("$state", JsonNamingPolicy.CamelCase.ConvertName(data.State.ToString()));
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(data, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) _notifier.Notify(data.ParentRunId);
    }

    internal async Task<bool> HasConsumedSubagentResultAsync(string parentRunId, string childRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM run_events WHERE run_id = $parent AND event_type = $type
                AND json_extract(data_json, '$.runId') = $child);
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$child", childRunId);
        command.Parameters.AddWithValue("$type", SubagentToolNames.ResultConsumedEvent);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    /// <summary>A result is consumed only after its receipt exists in a committed parent checkpoint.</summary>
    internal async Task<bool> TryConsumeSubagentResultAsync(string parentRunId, string childRunId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT checkpoint.checkpoint_json, child.state, json_extract(child.request_json, '$.subagent.agentId')
            FROM run_checkpoints checkpoint JOIN runs parent ON parent.run_id = checkpoint.run_id
            JOIN runs child ON child.run_id = $child
            WHERE checkpoint.run_id = $parent
              AND parent.state IN ('Queued', 'Running', 'WaitingForClient')
              AND json_extract(child.request_json, '$.subagent.parentRunId') = $parent
              AND child.state IN ('Completed', 'Failed', 'Cancelled', 'Interrupted');
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$child", childRunId);
        string checkpointJson;
        string agentId;
        RunState state;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return false;
            checkpointJson = reader.GetString(0);
            state = Enum.Parse<RunState>(reader.GetString(1));
            agentId = reader.GetString(2);
        }
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(checkpointJson, _checkpointJsonOptions);
        var identity = "\"runId\":" + JsonSerializer.Serialize(childRunId, _database.JsonOptions);
        if (checkpoint?.Messages.Any(message =>
            (message.Role == "tool" || message.Role == "user" && message.Content?.StartsWith(SubagentToolNames.ResultContextMarker, StringComparison.Ordinal) == true)
            && message.Content is { } content && content.Contains(identity, StringComparison.Ordinal)
            && (content.Contains("\"status\":\"completed\"", StringComparison.Ordinal)
                || content.Contains("\"status\":\"failed\"", StringComparison.Ordinal))) != true)
            return false;
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $parent, $type, $data, $now WHERE NOT EXISTS (
                SELECT 1 FROM run_events WHERE run_id = $parent AND event_type = $type
                  AND json_extract(data_json, '$.runId') = $child);
            """;
        command.Parameters.AddWithValue("$type", SubagentToolNames.ResultConsumedEvent);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { runId = childRunId, agentId, state }, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        var added = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (added) _notifier.Notify(parentRunId);
        return true;
    }
}

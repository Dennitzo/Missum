namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    /// <summary>
    /// Lists runs that still occupy the gateway or its shared workers. A planned
    /// self-improvement restart may only proceed once this list, excluding the
    /// applying run and its own subagents, is empty.
    /// </summary>
    internal async Task<IReadOnlyList<(string RunId, string State, string Mode)>> ListActiveRunsAsync(
        string? exceptRunId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, state, mode FROM runs
            WHERE state IN ('Queued', 'Running', 'WaitingForClient')
              AND ($except IS NULL OR (run_id <> $except
                  AND COALESCE(json_extract(request_json, '$.subagent.parentRunId'), '') <> $except))
            ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$except", (object?)exceptRunId ?? DBNull.Value);
        var result = new List<(string, string, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    /// <summary>Reads one bounded SSE replay page without discarding later journal events.</summary>
    public async Task<IReadOnlyList<RunEvent>> GetEventsPageAfterAsync(
        string runId,
        long lastEventId,
        int pageSize = 256,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 1024);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The existing (run_id, id) index supports cursor paging. Never OFFSET:
        // IDs can have gaps and new events may arrive while a replay is drained.
        command.CommandText = """
            SELECT id, event_type, data_json, created_at
            FROM run_events
            WHERE run_id = $run AND id > $after
            ORDER BY id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$after", lastEventId);
        command.Parameters.AddWithValue("$limit", pageSize);
        var events = new List<RunEvent>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            events.Add(new RunEvent(reader.GetInt64(0), runId, reader.GetString(1),
                MissumAiDatabase.ParseTimestamp(reader.GetString(3)), document.RootElement.Clone()));
        }
        return events;
    }

    /// <summary>Reads only narration revisions needed to reconstruct the authoritative visible text.</summary>
    public async Task<IReadOnlyList<RunEvent>> GetVisibleTextEventsAsync(
        string runId,
        long lastEventId = 0,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Token counters, heartbeats, reasoning and tool payloads can accumulate
        // for days. They must not be parsed or allocated just to project text.
        // Keep every text revision: limiting this query would truncate narration.
        command.CommandText = """
            SELECT id, event_type, data_json, created_at
            FROM run_events
            WHERE run_id = $run AND id > $after AND event_type = $type
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$after", lastEventId);
        command.Parameters.AddWithValue("$type", RunEventTypes.TextDelta);
        var events = new List<RunEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            events.Add(new RunEvent(reader.GetInt64(0), runId, reader.GetString(1),
                MissumAiDatabase.ParseTimestamp(reader.GetString(3)), document.RootElement.Clone()));
        }
        return events;
    }

    /// <summary>Checks whether a recovered turn published reasoning without loading its text.</summary>
    public async Task<bool> HasReasoningDeltaAfterAsync(
        string runId,
        long lastEventId,
        int round,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM run_events
                WHERE run_id = $run AND id > $after AND event_type = $type
                  AND json_extract(data_json, '$.round') = $round
            );
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$after", lastEventId);
        command.Parameters.AddWithValue("$type", RunEventTypes.ReasoningDelta);
        command.Parameters.AddWithValue("$round", round);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    internal Task<IReadOnlyList<RunEvent>> GetContinuationEventsAsync(
        string runId,
        long lastEventId = 0,
        CancellationToken cancellationToken = default) =>
        ReadEventsByTypesAsync(runId, lastEventId,
            [RunEventTypes.TextDelta, RunProcessor.InterruptedTurnEventType, RunSteeringEventTypes.Applied], cancellationToken);

    internal Task<IReadOnlyList<RunEvent>> GetServerToolCompletedEventsAsync(
        string runId,
        long lastEventId = 0,
        CancellationToken cancellationToken = default) =>
        ReadEventsByTypesAsync(runId, lastEventId, [RunEventTypes.ServerToolCompleted], cancellationToken);

    private async Task<IReadOnlyList<RunEvent>> ReadEventsByTypesAsync(
        string runId,
        long lastEventId,
        string[] eventTypes,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var typeParameters = eventTypes.Select((_, index) => "$type" + index).ToArray();
        command.CommandText = $"""
            SELECT id, event_type, data_json, created_at
            FROM run_events
            WHERE run_id = $run AND id > $after AND event_type IN ({string.Join(",", typeParameters)})
            ORDER BY id ASC;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$after", lastEventId);
        for (var index = 0; index < eventTypes.Length; index++)
            command.Parameters.AddWithValue(typeParameters[index], eventTypes[index]);
        var events = new List<RunEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            events.Add(new RunEvent(reader.GetInt64(0), runId, reader.GetString(1),
                MissumAiDatabase.ParseTimestamp(reader.GetString(3)), document.RootElement.Clone()));
        }
        return events;
    }
}

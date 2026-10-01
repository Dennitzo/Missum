using Missum.Ai.Contracts;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    public async Task<RunEvent> RequestModelSelectionAsync(string runId, RunModelSelectionRequest selection, CancellationToken token = default)
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(selection, _database.JsonOptions);
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Accepting a selection and checking the run lifetime is one SQLite statement.
        // A concurrent completion cannot leave an orphaned accepted request.
        command.CommandText = """
            INSERT INTO run_events(run_id,event_type,data_json,created_at)
            SELECT run_id,$type,$data,$created FROM runs
            WHERE run_id=$run AND state IN ('Queued','Running','WaitingForClient')
                AND json_extract(request_json,'$.sessionId')=$session
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$type", RunModelSelectionEvents.Requested);
        command.Parameters.AddWithValue("$data", json); command.Parameters.AddWithValue("$created", Missum.Ai.Server.Core.Data.MissumAiDatabase.FormatTimestamp(now));
        command.Parameters.AddWithValue("$session", selection.SessionId);
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (value is null) throw new InvalidOperationException("Der Lauf ist bereits abgeschlossen oder gehört zu einer anderen Sitzung.");
        using var data = JsonDocument.Parse(json);
        _notifier.Notify(runId);
        return new(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture), runId, RunModelSelectionEvents.Requested, now, data.RootElement.Clone());
    }

    internal async Task<(long EventId, RunModelSelectionRequest Selection)?> GetLatestModelSelectionAsync(string runId, long after, CancellationToken token)
    {
        await using var connection = await _database.OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,data_json FROM run_events WHERE run_id=$run AND event_type=$type AND id>$after ORDER BY id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$run", runId); command.Parameters.AddWithValue("$type", RunModelSelectionEvents.Requested); command.Parameters.AddWithValue("$after", after);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var selection = JsonSerializer.Deserialize<RunModelSelectionRequest>(reader.GetString(1), _database.JsonOptions);
        return selection is null ? null : (reader.GetInt64(0), selection);
    }
}

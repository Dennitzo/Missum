using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    internal async Task<DeepResearchProgressEvent?> GetCompletedDeepResearchAsync(string runId, string operationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // One fixed General preflight operation per run. A crash after its
        // durable result but before checkpointing must not start it again.
        command.CommandText = """
            SELECT data_json FROM run_events
            WHERE run_id=$run AND event_type=$type
              AND json_extract(data_json,'$.operationId')=$operation
              AND json_extract(data_json,'$.phase')='operation'
              AND json_extract(data_json,'$.status') IN ('completed','failed')
              AND json_type(data_json,'$.result')='object'
            ORDER BY id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", RunEventTypes.DeepResearchProgress);
        command.Parameters.AddWithValue("$operation", operationId);
        var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return json is null ? null : JsonSerializer.Deserialize<DeepResearchProgressEvent>(json, _database.JsonOptions);
    }

    internal async Task<RunEvent> AppendDeepResearchProgressAsync(DeepResearchProgressEvent progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progress.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(progress.ParentRunId);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Use the durable global event ID as a monotonic operation revision.
        // New operations and retries never scan growing historical JSON payloads.
        command.CommandText = """
            INSERT INTO run_events(run_id,event_type,data_json,created_at)
            VALUES($run,$type,$data,$now) RETURNING id;
            """;
        command.Parameters.AddWithValue("$run", progress.ParentRunId);
        command.Parameters.AddWithValue("$type", RunEventTypes.DeepResearchProgress);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(progress, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(now));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = "UPDATE run_events SET data_json=json_set(data_json,'$.sequence',id) WHERE id=$id RETURNING data_json;";
        command.Parameters.AddWithValue("$id", id);
        var json = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Der Recherchefortschritt konnte nicht gespeichert werden."));
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.Clone();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(progress.ParentRunId);
        return new(id, progress.ParentRunId, RunEventTypes.DeepResearchProgress, now, data);
    }
}

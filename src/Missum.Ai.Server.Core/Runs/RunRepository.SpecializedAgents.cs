using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    internal async Task EnsureSpecializedAgentResultConsumedAsync(string parentRunId, string agentId, string invocationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events(run_id,event_type,data_json,created_at)
            SELECT $parent,$type,$data,$now WHERE EXISTS (
                SELECT 1 FROM run_checkpoints c JOIN runs r ON r.run_id=c.run_id WHERE c.run_id=$parent
                  AND r.state IN ('Queued','Running','WaitingForClient')
                  AND json_extract(c.checkpoint_json,'$.specializedAgent.agentId')=$agent
                  AND json_extract(c.checkpoint_json,'$.specializedAgent.invocationId')=$invocation
                  AND json_extract(c.checkpoint_json,'$.specializedAgent.decision') IS NOT NULL)
              AND NOT EXISTS (SELECT 1 FROM run_events WHERE run_id=$parent AND event_type=$type
                  AND json_extract(data_json,'$.runId')=$invocation);
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$invocation", invocationId);
        command.Parameters.AddWithValue("$type", SubagentToolNames.ResultConsumedEvent);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { parentRunId, agentId, runId = invocationId, state = "completed" }, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) _notifier.Notify(parentRunId);
    }

    /// <summary>Semantic idempotence for virtual-agent transcript boundaries, including a crash before checkpoint save.</summary>
    internal async Task EnsureSpecializedAgentEventAsync(string parentRunId, string agentId, string invocationId,
        string type, object payload, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM run_events WHERE run_id=$parent AND event_type=$forward
              AND json_extract(data_json,'$.runId')=$invocation AND json_extract(data_json,'$.event.type')=$type);
            """;
        command.Parameters.AddWithValue("$parent", parentRunId);
        command.Parameters.AddWithValue("$invocation", invocationId);
        command.Parameters.AddWithValue("$forward", RunEventTypes.SubagentEvent);
        command.Parameters.AddWithValue("$type", type);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0) return;
        command.CommandText = """
            SELECT COALESCE(MAX(CAST(json_extract(data_json,'$.event.id') AS INTEGER)),0)+1 FROM run_events
            WHERE run_id=$parent AND event_type=$forward AND json_extract(data_json,'$.runId')=$invocation;
            """;
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var now = DateTimeOffset.UtcNow;
        var forwarded = new SubagentForwardedEvent(agentId, parentRunId, invocationId,
            new(sequence, invocationId, type, now, JsonSerializer.SerializeToElement(payload, _database.JsonOptions)));
        command.CommandText = "INSERT INTO run_events(run_id,event_type,data_json,created_at) VALUES($parent,$forward,$data,$now);";
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(forwarded, _database.JsonOptions));
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(now));
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _notifier.Notify(parentRunId);
    }
}

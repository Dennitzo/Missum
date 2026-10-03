using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    /// <summary>Enrolls an agent-managed root once, including recovery of older checkpoints without a start receipt.</summary>
    internal async Task EnsureAgentManagedResearchEnrollmentAsync(string runId, JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $run, $type, $data, $now
            WHERE EXISTS (
                SELECT 1 FROM runs run JOIN run_checkpoints checkpoint ON checkpoint.run_id = run.run_id
                WHERE run.run_id = $run AND run.state IN ('Queued', 'Running', 'WaitingForClient')
                  AND (json_extract(run.request_json, '$.deepResearch') = 1
                    OR json_extract(run.request_json, '$.researchOptions.protocolVersion') >= 2)
                  AND json_extract(run.request_json, '$.subagent.parentRunId') IS NULL
                  AND json_extract(checkpoint.checkpoint_json, '$.researchManagedByAgent') = 1)
              AND NOT EXISTS (
                SELECT 1 FROM run_events WHERE run_id = $run AND event_type = $type);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", RunEventTypes.ResearchProblemInterpreted);
        command.Parameters.AddWithValue("$data", metadata.GetRawText());
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) _notifier.Notify(runId);
    }
}

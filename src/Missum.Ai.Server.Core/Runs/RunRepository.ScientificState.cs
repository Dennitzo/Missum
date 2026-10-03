using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Data;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    internal async Task EnsureScientificFirstSectionMetricAsync(string runId, long revision, long publicationRevision,
        long inputTokens, long outputTokens, CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Derive source activity from real tool starts in the durable journal.
        // One insertion also protects retry/replay after the receipt checkpoint.
        command.CommandText = """
            INSERT INTO run_events(run_id, event_type, data_json, created_at)
            SELECT $run, $type, json_object(
                'protocol', 'section-delta-v1', 'firstAcceptedSection', json('true'),
                'revision', $revision, 'publicationRevision', $publication,
                'inputTokens', $input, 'outputTokens', $output,
                'sourceActions', (SELECT COUNT(*) FROM run_events WHERE run_id = $run AND event_type = $started
                    AND json_extract(data_json, '$.tool') IN ('web.search', 'web.fetch')),
                'repeatedSourceActions', (SELECT COALESCE(SUM(n - 1), 0) FROM (
                    SELECT COUNT(*) n FROM run_events WHERE run_id = $run AND event_type = $started
                      AND json_extract(data_json, '$.tool') IN ('web.search', 'web.fetch')
                    GROUP BY json_extract(data_json, '$.tool'), json_extract(data_json, '$.arguments')))), $now
            WHERE NOT EXISTS (SELECT 1 FROM run_events WHERE run_id = $run AND event_type = $type
                AND json_extract(data_json, '$.firstAcceptedSection') = 1);
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", "research.state.metrics");
        command.Parameters.AddWithValue("$started", RunEventTypes.ServerToolStarted);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$publication", publicationRevision);
        command.Parameters.AddWithValue("$input", inputTokens);
        command.Parameters.AddWithValue("$output", outputTokens);
        command.Parameters.AddWithValue("$now", MissumAiDatabase.FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1) _notifier.Notify(runId);
    }
}

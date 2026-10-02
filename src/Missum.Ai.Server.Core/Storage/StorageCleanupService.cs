using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Core.Storage;

public sealed class StorageCleanupService : BackgroundService
{
    private const string ProtectedRunCondition = """
        (run.state IN ('Queued', 'Running', 'WaitingForClient')
         OR (run.state = 'Interrupted' AND COALESCE(run.error_code, '') IN ('run.gateway_stopped', 'run.gateway_restarted')
             AND (run.mode = 'Coding' OR (run.mode IN ('General', 'Auto') AND COALESCE(json_extract(run.request_json, '$.deepResearch'), 0) = 1))))
        """;
    private const string ArtifactIsUnreferenced = $"""
        NOT EXISTS (
            SELECT 1 FROM runs run
            WHERE {ProtectedRunCondition}
              AND (
                EXISTS (SELECT 1 FROM json_each(run.request_json, '$.artifactIds') item WHERE item.value = artifact.artifact_id)
                OR EXISTS (SELECT 1 FROM json_tree(run.request_json) item WHERE item.key = 'artifactId' AND item.value = artifact.artifact_id)
                OR json_extract(artifact.metadata_json, '$.runId') = run.run_id
                OR EXISTS (SELECT 1 FROM run_checkpoints checkpoint WHERE checkpoint.run_id = run.run_id AND instr(checkpoint.checkpoint_json, artifact.artifact_id) > 0)
                OR EXISTS (SELECT 1 FROM run_events event WHERE event.run_id = run.run_id AND instr(event.data_json, artifact.artifact_id) > 0)
              )
        )
        """;
    private readonly MissumAiDatabase _database;
    private readonly MissumAiServerOptions _options;

    public StorageCleanupService(MissumAiDatabase database, IOptions<MissumAiServerOptions> options)
    {
        _database = database;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            await CleanupExpiredAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var current = DateTimeOffset.UtcNow;
        var now = MissumAiDatabase.FormatTimestamp(current);
        var historyCutoff = MissumAiDatabase.FormatTimestamp(current.AddHours(-24));
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var paths = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = $"""
                SELECT upload.upload_id
                FROM uploads upload
                WHERE upload.expires_at <= $now
                  AND NOT EXISTS (
                    SELECT 1 FROM runs run
                    WHERE {ProtectedRunCondition}
                      AND (
                        EXISTS (SELECT 1 FROM json_each(run.request_json, '$.uploadIds') item WHERE item.value = upload.upload_id)
                        OR EXISTS (SELECT 1 FROM json_tree(run.request_json) item WHERE item.key = 'uploadId' AND item.value = upload.upload_id)
                      )
                  )
                UNION ALL
                SELECT artifact.artifact_id FROM artifacts artifact
                WHERE artifact.expires_at <= $now AND {ArtifactIsUnreferenced};
                """;
            select.Parameters.AddWithValue("$now", now);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                paths.Add(reader.GetString(0));
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"""
                DELETE FROM uploads AS upload
                WHERE expires_at <= $now
                  AND NOT EXISTS (
                    SELECT 1 FROM runs run
                    WHERE {ProtectedRunCondition}
                      AND (
                        EXISTS (SELECT 1 FROM json_each(run.request_json, '$.uploadIds') item WHERE item.value = upload.upload_id)
                        OR EXISTS (SELECT 1 FROM json_tree(run.request_json) item WHERE item.key = 'uploadId' AND item.value = upload.upload_id)
                      )
                  );
                DELETE FROM artifacts AS artifact
                WHERE expires_at <= $now AND {ArtifactIsUnreferenced};
                DELETE FROM client_tool_proposals
                WHERE expires_at <= $now AND NOT EXISTS (
                    SELECT 1 FROM run_checkpoints checkpoint
                    JOIN runs run ON run.run_id = checkpoint.run_id
                    WHERE {ProtectedRunCondition}
                      AND json_extract(checkpoint.checkpoint_json, '$.pendingProposalId') = client_tool_proposals.proposal_id
                );
                DELETE FROM runs AS run
                WHERE updated_at <= $historyCutoff
                  AND state IN ('Completed', 'Failed', 'Cancelled', 'Interrupted')
                  AND NOT {ProtectedRunCondition};
                DELETE FROM gpu_leases
                WHERE created_at <= $historyCutoff
                  AND state IN ('released', 'cancelled', 'interrupted');
                """;
            delete.Parameters.AddWithValue("$now", now);
            delete.Parameters.AddWithValue("$historyCutoff", historyCutoff);
            _ = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        foreach (var id in paths)
        {
            if (id.StartsWith("upload-", StringComparison.Ordinal))
            {
                var directory = Path.Combine(_options.UploadDirectory, id);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            else if (id.StartsWith("artifact-", StringComparison.Ordinal))
            {
                var path = Path.Combine(_options.ArtifactDirectory, id + ".bin");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}

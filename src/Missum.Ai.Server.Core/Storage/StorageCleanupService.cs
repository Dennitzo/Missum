using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Missum.Ai.Server.Core.Storage;

public sealed class StorageCleanupService : BackgroundService
{
    private const string ExpiredPlaybackArtifact = """
        artifact.expires_at <= $now AND CASE WHEN json_valid(artifact.metadata_json)
            THEN json_extract(artifact.metadata_json,'$.purpose') = 'speech-playback'
                AND lower(CAST(json_extract(artifact.metadata_json,'$.transient') AS TEXT)) IN ('true','1')
            ELSE 0 END
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
        using var maintenance = await _database.EnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        var current = DateTimeOffset.UtcNow;
        var now = MissumAiDatabase.FormatTimestamp(current);
        var leaseCutoff = MissumAiDatabase.FormatTimestamp(current.AddHours(-24));
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
                    WHERE (
                        EXISTS (SELECT 1 FROM json_each(run.request_json, '$.uploadIds') item WHERE item.value = upload.upload_id)
                        OR EXISTS (SELECT 1 FROM json_tree(run.request_json) item WHERE item.key = 'uploadId' AND item.value = upload.upload_id)
                      )
                  )
                UNION ALL
                SELECT artifact.artifact_id FROM artifacts artifact WHERE {ExpiredPlaybackArtifact};
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
                    WHERE (
                        EXISTS (SELECT 1 FROM json_each(run.request_json, '$.uploadIds') item WHERE item.value = upload.upload_id)
                        OR EXISTS (SELECT 1 FROM json_tree(run.request_json) item WHERE item.key = 'uploadId' AND item.value = upload.upload_id)
                      )
                  );
                DELETE FROM artifacts AS artifact WHERE {ExpiredPlaybackArtifact};
                DELETE FROM gpu_leases
                WHERE created_at <= $leaseCutoff
                  AND state IN ('released', 'cancelled', 'interrupted');
                """;
            delete.Parameters.AddWithValue("$now", now);
            // Runs, journals, checkpoints, heads and tool receipts are durable
            // conversation data. Only explicit conversation deletion may remove
            // them; an elapsed upload/lease lifetime is not a retention policy.
            // Artifacts may be historical evidence, including references in old
            // checkpoints/events. Retain them conservatively rather than scan
            // growing journals at startup to infer whether deletion is safe.
            // Only explicitly marked playback paragraphs are disposable.
            delete.Parameters.AddWithValue("$leaseCutoff", leaseCutoff);
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
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}

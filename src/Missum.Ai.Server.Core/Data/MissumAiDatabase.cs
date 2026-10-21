using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Data;

public sealed class MissumAiDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 3;
    private readonly MissumAiServerOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = MissumAiProtocol.CreateJsonOptions();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private volatile bool _initialized;

    public MissumAiDatabase(IOptions<MissumAiServerOptions> options)
    {
        _options = options.Value;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            Directory.CreateDirectory(_options.UploadDirectory);
            Directory.CreateDirectory(_options.ArtifactDirectory);
            Directory.CreateDirectory(_options.WorkerArtifactDirectory);
            Directory.CreateDirectory(_options.LogDirectory);

            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var previousVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (previousVersion > CurrentSchemaVersion)
                throw new InvalidDataException("The gateway database was created by a newer Missum version.");
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE name='session_context_heads');";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = _options.DatabasePath + ".pre-session-only-" + Guid.NewGuid().ToString("N") + ".bak",
                }.ToString());
                await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backup);
            }
            // WAL belongs to the database, not an individual connection. Do
            // not ask every concurrent status reader to change journal mode.
            command.CommandText = "PRAGMA journal_mode=WAL;\n" + Schema;
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (previousVersion < CurrentSchemaVersion)
            {
                // Full scans belong to a real migration/explicit diagnostic,
                // not every normal startup of an overnight-sized run journal.
                await ValidateDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
                versionCommand.CommandText = """
                    INSERT OR IGNORE INTO schema_migrations(version, applied_at)
                    VALUES(3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                    PRAGMA user_version=3;
                    """;
                _ = await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            _initialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    public JsonSerializerOptions JsonOptions => _jsonOptions;

    public async Task ValidateIntegrityAsync(CancellationToken cancellationToken = default)
    {
        using var maintenance = await EnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ValidateDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serialize the two potentially long startup maintenance transactions without blocking a SQLite worker.</summary>
    internal async Task<IDisposable> EnterMaintenanceAsync(CancellationToken cancellationToken)
    {
        await _maintenanceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new MaintenanceLease(_maintenanceGate);
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public void Dispose()
    {
        _initializeGate.Dispose();
        _maintenanceGate.Dispose();
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared cache adds table locks on top of WAL. A held status reader
            // or startup cleanup can then fail recovery with SQLITE_LOCKED and
            // stop the entire gateway. Private caches retain WAL snapshots and
            // ordinary SQLite writer arbitration without shared table locks.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class MaintenanceLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static async Task ValidateDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(
                await integrity.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Missum AI SQLite integrity_check failed: {result}");
            }
        }

        await using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await foreignKeys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Missum AI SQLite foreign_key_check failed.");
        }
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        );
        DROP TABLE IF EXISTS api_keys;
        DROP TABLE IF EXISTS agent_observations;
        DROP TABLE IF EXISTS agent_actions;
        CREATE TABLE IF NOT EXISTS runs (
            run_id TEXT PRIMARY KEY,
            idempotency_key TEXT NULL UNIQUE,
            state TEXT NOT NULL,
            mode TEXT NOT NULL,
            selected_model TEXT NULL,
            session_title TEXT NULL,
            request_json TEXT NOT NULL,
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS run_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            data_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS ix_run_events_run_id_id ON run_events(run_id, id);
        CREATE INDEX IF NOT EXISTS ix_run_events_run_type_id ON run_events(run_id, event_type, id);
        CREATE INDEX IF NOT EXISTS ix_run_events_proposal ON run_events(run_id, event_type, json_extract(data_json, '$.proposalId'));
        CREATE TABLE IF NOT EXISTS run_steering_inputs (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL REFERENCES runs(run_id) ON DELETE CASCADE,
            input_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            text TEXT NOT NULL,
            accepted_at TEXT NOT NULL,
            applied_at TEXT NULL,
            UNIQUE(run_id, input_id)
        );
        CREATE INDEX IF NOT EXISTS ix_run_steering_pending ON run_steering_inputs(run_id, applied_at, sequence);
        CREATE TABLE IF NOT EXISTS client_tool_results (
            proposal_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            result_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS client_tool_proposals (
            proposal_id TEXT PRIMARY KEY,
            run_id TEXT NOT NULL,
            name TEXT NOT NULL,
            proposal_json TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            created_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS coding_session_contexts (
            run_id TEXT PRIMARY KEY REFERENCES runs(run_id) ON DELETE CASCADE,
            checkpoint_json TEXT NOT NULL
        );

        DROP TABLE IF EXISTS session_context_heads;
        DROP TABLE IF EXISTS runtime_context_state;

        CREATE TABLE IF NOT EXISTS run_checkpoints (
            run_id TEXT PRIMARY KEY,
            checkpoint_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS run_provider_retries (
            run_id TEXT PRIMARY KEY,
            attempt INTEGER NOT NULL,
            next_attempt_at TEXT NULL,
            FOREIGN KEY(run_id) REFERENCES runs(run_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS uploads (
            upload_id TEXT PRIMARY KEY,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            total_length INTEGER NOT NULL,
            total_sha256 TEXT NOT NULL,
            chunk_size INTEGER NOT NULL,
            chunk_count INTEGER NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS upload_chunks (
            upload_id TEXT NOT NULL,
            chunk_index INTEGER NOT NULL,
            chunk_length INTEGER NOT NULL,
            chunk_sha256 TEXT NOT NULL,
            file_path TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY(upload_id, chunk_index),
            FOREIGN KEY(upload_id) REFERENCES uploads(upload_id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS artifacts (
            artifact_id TEXT PRIMARY KEY,
            file_name TEXT NOT NULL,
            media_type TEXT NOT NULL,
            total_length INTEGER NOT NULL,
            sha256 TEXT NOT NULL,
            file_path TEXT NOT NULL,
            metadata_json TEXT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS gpu_leases (
            lease_id TEXT PRIMARY KEY,
            run_id TEXT NULL,
            workload TEXT NOT NULL,
            state TEXT NOT NULL,
            created_at TEXT NOT NULL,
            acquired_at TEXT NULL,
            released_at TEXT NULL
        );
        INSERT OR IGNORE INTO schema_migrations(version, applied_at)
        VALUES(1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        INSERT OR IGNORE INTO schema_migrations(version, applied_at)
        VALUES(2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        """;
}

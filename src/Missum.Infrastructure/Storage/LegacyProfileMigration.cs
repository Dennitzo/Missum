using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Missum.Infrastructure.Storage;

/// <summary>One-way compatibility for data already stored inside this application's own profile.</summary>
internal static class LegacyProfileMigration
{
    internal const string LegacyDatabaseName = "GO.db";
    private const string LegacyRunsTable = "go_ai_runs";
    private const string RunsTable = "missum_ai_runs";

    internal static async Task CopyDatabaseIfNeededAsync(string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) || !Path.GetFileName(destination).Equals("Missum.db", StringComparison.OrdinalIgnoreCase)) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        // Never discover, open or move a separate installation's old profile.
        if (Path.GetFileName(directory).Equals("GO", StringComparison.OrdinalIgnoreCase)) return;
        var sourcePath = Path.Combine(directory, LegacyDatabaseName);
        if (!File.Exists(sourcePath)) return;
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Ein verknüpftes Profil wird nicht automatisch migriert.");
        if (File.GetAttributes(sourcePath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Eine verknüpfte Altdatenbank wird nicht automatisch migriert.");

        var temporary = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Copying only the .db file loses committed records still present in its WAL.
            await using (var source = Open(sourcePath, SqliteOpenMode.ReadOnly))
            await using (var target = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await target.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(target);
                await using var command = target.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE;";
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                await VerifyAsync(target, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { } // Another initializer won; never replace its data.
            // The original database and WAL remain available as a recovery source.
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                try { File.Delete(temporary + suffix); } catch (IOException) { }
        }
    }

    internal static async Task RenameRunTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ($old, $new);";
        command.Parameters.AddWithValue("$old", LegacyRunsTable);
        command.Parameters.AddWithValue("$new", RunsTable);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) tables.Add(reader.GetString(0));
        if (tables.Contains(LegacyRunsTable))
        {
            if (tables.Contains(RunsTable))
                throw new InvalidDataException("Das Profil enthält zwei unterschiedliche Lauf-Tabellen; die Daten werden nicht automatisch überschrieben.");
            command.Parameters.Clear();
            command.CommandText = "ALTER TABLE " + Quote(LegacyRunsTable) + " RENAME TO " + Quote(RunsTable) + ";";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (tables.Count > 0)
        {
            command.Parameters.Clear();
            command.CommandText = "SELECT name, sql FROM sqlite_master WHERE type='index' AND tbl_name=$table AND sql IS NOT NULL;";
            command.Parameters.AddWithValue("$table", RunsTable);
            var indexes = new List<(string Name, string Sql)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    if (reader.GetString(0).StartsWith("ix_go_ai_", StringComparison.Ordinal))
                        indexes.Add((reader.GetString(0), reader.GetString(1)));
            command.Parameters.Clear();
            foreach (var (name, sql) in indexes)
            {
                command.CommandText = "DROP INDEX " + Quote(name) + ";";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                command.CommandText = sql.Replace(name, name.Replace("ix_go_ai_", "ix_missum_ai_", StringComparison.Ordinal), StringComparison.Ordinal);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RecordMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES(50, $now);";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30,
    }.ToString());

    private static async Task VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Altdatenbank konnte nicht fehlerfrei übernommen werden.");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("Die Altdatenbank enthält ungültige Fremdschlüssel.");
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

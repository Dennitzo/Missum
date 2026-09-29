using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure.Storage;
using Missum.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Missum.Infrastructure.Backup;

public sealed class ZipBackupService(SqliteDatabase database, ISettingsStore settingsStore, MissumInfrastructureOptions options) : IBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private const string DatabaseEntry = "Missum.db";
    private const string SettingsEntry = "settings.json";
    private const string ManifestEntry = "manifest.json";
    private const int DatabaseSnapshotAttempts = 8;

    public async Task<BackupResult> CreateAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination) ?? throw new InvalidOperationException("Ungültiger Backup-Pfad."));
        var workingDirectory = CreateWorkingDirectory();
        var temporaryArchive = fullDestination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var databaseSnapshot = Path.Combine(workingDirectory, DatabaseEntry);
            await database.MaintenanceAsync(async token =>
            {
                await CreateDatabaseSnapshotAsync(database.DatabasePath, databaseSnapshot, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);

            var settingsSnapshot = Path.Combine(workingDirectory, SettingsEntry);
            await File.WriteAllTextAsync(settingsSnapshot, JsonSerializer.Serialize(await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false), JsonOptions), cancellationToken).ConfigureAwait(false);

            var created = DateTimeOffset.UtcNow;
            var manifest = new BackupManifest(1, created, 1, await HashFileAsync(databaseSnapshot, cancellationToken).ConfigureAwait(false),
                await HashFileAsync(settingsSnapshot, cancellationToken).ConfigureAwait(false));
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ManifestEntry), JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);

            await using (var archiveStream = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                archive.CreateEntryFromFile(databaseSnapshot, DatabaseEntry, CompressionLevel.Optimal);
                archive.CreateEntryFromFile(settingsSnapshot, SettingsEntry, CompressionLevel.Optimal);
                archive.CreateEntryFromFile(Path.Combine(workingDirectory, ManifestEntry), ManifestEntry, CompressionLevel.Optimal);
            }

            File.Move(temporaryArchive, fullDestination, overwrite: true);
            return new(fullDestination, await HashFileAsync(fullDestination, cancellationToken).ConfigureAwait(false), created);
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteDirectory(workingDirectory);
        }
    }

    public async Task ValidateAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            await ExtractAndValidateAsync(backupPath, workingDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or SqliteException or FormatException)
        {
            throw new InvalidDataException("Das Missum-Backup ist ungültig oder beschädigt.", exception);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            // Validate exactly the extraction that will be installed, not an earlier opening of the archive.
            await ExtractAndValidateAsync(backupPath, workingDirectory, cancellationToken).ConfigureAwait(false);
            var backupDirectory = Path.Combine(options.DataDirectory, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var safetyPath = Path.Combine(backupDirectory, $"before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.missumbackup");
            _ = await CreateAsync(safetyPath, cancellationToken).ConfigureAwait(false);
            await database.MaintenanceAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                SqliteConnection.ClearAllPools();
                ReplaceDatabaseAndSettings(
                    Path.Combine(workingDirectory, DatabaseEntry), database.DatabasePath,
                    Path.Combine(workingDirectory, SettingsEntry), settingsStore.SettingsPath);
                database.MarkUninitialized();
                return Task.FromResult(true);
            }, cancellationToken).ConfigureAwait(false);
            try
            {
                await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                DeleteRestorePreviousFiles(database.DatabasePath, settingsStore.SettingsPath);
            }
            catch
            {
                await database.RecoveryMaintenanceAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    SqliteConnection.ClearAllPools();
                    RollBackDatabaseAndSettings(database.DatabasePath, settingsStore.SettingsPath);
                    database.MarkUninitialized();
                    return Task.FromResult(true);
                }, CancellationToken.None).ConfigureAwait(false);
                await database.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    private static async Task ExtractAndValidateAsync(string backupPath, string destination, CancellationToken cancellationToken)
    {
        try
        {
            var databaseEntry = await ExtractValidatedEntriesAsync(backupPath, destination, cancellationToken).ConfigureAwait(false);
            var manifest = await ReadManifestAsync(Path.Combine(destination, ManifestEntry), cancellationToken).ConfigureAwait(false);
            if (manifest.FormatVersion != 1 || manifest.SchemaVersion is < 1 or > 1)
                throw new InvalidDataException("Das Backup- oder Datenbankschema wird von dieser Missum-Version nicht unterstützt.");
            var extractedDatabase = Path.Combine(destination, databaseEntry);
            var extractedSettings = Path.Combine(destination, SettingsEntry);
            // Hash the original bytes and original entry names before any compatibility normalization.
            await EnsureHashAsync(extractedDatabase, manifest.DatabaseSha256, cancellationToken).ConfigureAwait(false);
            await EnsureHashAsync(extractedSettings, manifest.SettingsSha256, cancellationToken).ConfigureAwait(false);
            await ValidateDatabaseAsync(extractedDatabase, cancellationToken).ConfigureAwait(false);
            await JsonSettingsStore.NormalizeImportedFileAsync(extractedSettings, cancellationToken).ConfigureAwait(false);
            if (databaseEntry != DatabaseEntry)
                File.Move(extractedDatabase, Path.Combine(destination, DatabaseEntry), overwrite: false);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or SqliteException or FormatException)
        {
            throw new InvalidDataException("Das Missum-Backup ist ungültig oder beschädigt.", exception);
        }
    }

    private static async Task CreateDatabaseSnapshotAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= DatabaseSnapshotAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteFile(destinationPath);
            try
            {
                var sourceOptions = new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5,
                };
                var destinationOptions = new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5,
                };
                await using var source = new SqliteConnection(sourceOptions.ToString());
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var destination = new SqliteConnection(destinationOptions.ToString());
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode is 5 or 6
                && attempt < DatabaseSnapshotAttempts)
            {
                TryDeleteFile(destinationPath);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1_000, attempt * 100)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> ExtractValidatedEntriesAsync(string backupPath, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(Path.GetFullPath(backupPath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var databaseEntry = archive.GetEntry(DatabaseEntry) is not null ? DatabaseEntry : LegacyProfileMigration.LegacyDatabaseName;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { databaseEntry, SettingsEntry, ManifestEntry };
        if (archive.Entries.Count != allowed.Count || archive.Entries.Any(entry => !allowed.Contains(entry.FullName)))
            throw new InvalidDataException("Das Backup enthält unerwartete oder fehlende Dateien.");
        foreach (var name in allowed)
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Backup-Eintrag '{name}' fehlt.");
            await using var source = entry.Open();
            await using var target = new FileStream(Path.Combine(destination, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        return databaseEntry;
    }

    private static async Task<BackupManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BackupManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Backup-Manifest fehlt.");
    }

    private static async Task ValidateDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SQLite-Integritätsprüfung des Backups ist fehlgeschlagen.");
        command.CommandText = "PRAGMA foreign_key_check;";
        await using (var foreignKeyReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await foreignKeyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("SQLite-Fremdschlüsselprüfung des Backups ist fehlgeschlagen.");
        }

        command.CommandText = "SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
        var schema = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (schema is < 1 or > SqliteDatabase.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Nicht unterstützte Datenbankschemaversion {schema}.");
        }
    }

    private static async Task EnsureHashAsync(string path, string expected, CancellationToken cancellationToken)
    {
        if (expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Das Backup-Manifest enthält keine gültige SHA-256-Prüfsumme.");
        var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new InvalidDataException($"Prüfsumme von '{Path.GetFileName(path)}' stimmt nicht.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void ReplaceDatabaseAndSettings(string databaseSource, string databaseDestination, string settingsSource, string settingsDestination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination) ?? throw new InvalidOperationException("Ungültiger Datenbankpfad."));
        Directory.CreateDirectory(Path.GetDirectoryName(settingsDestination) ?? throw new InvalidOperationException("Ungültiger Einstellungspfad."));
        var previousDatabase = databaseDestination + ".restore-previous";
        var previousSettings = settingsDestination + ".restore-previous";
        TryDeleteFile(previousDatabase);
        TryDeleteFile(previousSettings);
        TryDeleteFile(databaseDestination + "-wal");
        TryDeleteFile(databaseDestination + "-shm");
        var databaseBackedUp = false;
        var settingsBackedUp = false;
        var databaseInstalled = false;
        var settingsInstalled = false;
        try
        {
            if (File.Exists(databaseDestination))
            {
                File.Move(databaseDestination, previousDatabase);
                databaseBackedUp = true;
            }
            if (File.Exists(settingsDestination))
            {
                File.Move(settingsDestination, previousSettings);
                settingsBackedUp = true;
            }
            File.Move(databaseSource, databaseDestination);
            databaseInstalled = true;
            File.Move(settingsSource, settingsDestination);
            settingsInstalled = true;
        }
        catch
        {
            if (databaseInstalled) TryDeleteFile(databaseDestination);
            if (settingsInstalled) TryDeleteFile(settingsDestination);
            if (File.Exists(previousDatabase)) File.Move(previousDatabase, databaseDestination);
            if (File.Exists(previousSettings)) File.Move(previousSettings, settingsDestination);
            if (!databaseBackedUp && databaseInstalled) TryDeleteFile(databaseDestination);
            if (!settingsBackedUp && settingsInstalled) TryDeleteFile(settingsDestination);
            throw;
        }
    }

    private static void DeleteRestorePreviousFiles(string databasePath, string settingsPath)
    {
        TryDeleteFile(databasePath + ".restore-previous");
        TryDeleteFile(settingsPath + ".restore-previous");
    }

    private static void RollBackDatabaseAndSettings(string databasePath, string settingsPath)
    {
        var previousDatabase = databasePath + ".restore-previous";
        var previousSettings = settingsPath + ".restore-previous";
        TryDeleteFile(databasePath + "-wal");
        TryDeleteFile(databasePath + "-shm");
        TryDeleteFile(databasePath);
        TryDeleteFile(settingsPath);
        if (File.Exists(previousDatabase)) File.Move(previousDatabase, databasePath);
        if (File.Exists(previousSettings)) File.Move(previousSettings, settingsPath);
    }

    private static string CreateWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Missum-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch (IOException) { } }
    private sealed record BackupManifest(int FormatVersion, DateTimeOffset CreatedAt, int SchemaVersion, string DatabaseSha256, string SettingsSha256);
}

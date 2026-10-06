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
    private const string ClientViewsEntry = "ClientState/views.json";
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

            var clientViewsSnapshot = Path.Combine(workingDirectory, ClientViewsEntry);
            var hasClientViews = await SnapshotClientViewsAsync(clientViewsSnapshot, cancellationToken).ConfigureAwait(false);

            var created = DateTimeOffset.UtcNow;
            var manifest = new BackupManifest(2, created, 1, await HashFileAsync(databaseSnapshot, cancellationToken).ConfigureAwait(false),
                await HashFileAsync(settingsSnapshot, cancellationToken).ConfigureAwait(false),
                hasClientViews ? await HashFileAsync(clientViewsSnapshot, cancellationToken).ConfigureAwait(false) : null);
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, ManifestEntry), JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);

            await using (var archiveStream = new FileStream(temporaryArchive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                archive.CreateEntryFromFile(databaseSnapshot, DatabaseEntry, CompressionLevel.Optimal);
                archive.CreateEntryFromFile(settingsSnapshot, SettingsEntry, CompressionLevel.Optimal);
                if (hasClientViews) archive.CreateEntryFromFile(clientViewsSnapshot, ClientViewsEntry, CompressionLevel.Optimal);
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
                    Path.Combine(workingDirectory, SettingsEntry), settingsStore.SettingsPath,
                    File.Exists(Path.Combine(workingDirectory, ClientViewsEntry)) ? Path.Combine(workingDirectory, ClientViewsEntry) : null,
                    Path.Combine(options.DataDirectory, ClientViewsEntry));
                database.MarkUninitialized();
                return Task.FromResult(true);
            }, cancellationToken).ConfigureAwait(false);
            try
            {
                await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                DeleteRestorePreviousFiles(database.DatabasePath, settingsStore.SettingsPath, Path.Combine(options.DataDirectory, ClientViewsEntry));
            }
            catch
            {
                await database.RecoveryMaintenanceAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    SqliteConnection.ClearAllPools();
                    RollBackDatabaseAndSettings(database.DatabasePath, settingsStore.SettingsPath, Path.Combine(options.DataDirectory, ClientViewsEntry));
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
            if (manifest.FormatVersion is not (1 or 2) || manifest.SchemaVersion is < 1 or > 1)
                throw new InvalidDataException("Das Backup- oder Datenbankschema wird von dieser Missum-Version nicht unterstützt.");
            var extractedDatabase = Path.Combine(destination, databaseEntry);
            var extractedSettings = Path.Combine(destination, SettingsEntry);
            // Hash the original bytes and original entry names before any compatibility normalization.
            await EnsureHashAsync(extractedDatabase, manifest.DatabaseSha256, cancellationToken).ConfigureAwait(false);
            await EnsureHashAsync(extractedSettings, manifest.SettingsSha256, cancellationToken).ConfigureAwait(false);
            var extractedClientViews = Path.Combine(destination, ClientViewsEntry);
            var hasClientViews = File.Exists(extractedClientViews);
            if (hasClientViews != (manifest.ClientViewsSha256 is not null)
                || manifest.FormatVersion == 1 && hasClientViews)
                throw new InvalidDataException("Die Clientzustände stimmen nicht mit dem Backup-Manifest überein.");
            if (hasClientViews)
            {
                await EnsureHashAsync(extractedClientViews, manifest.ClientViewsSha256!, cancellationToken).ConfigureAwait(false);
                await ValidateClientViewsAsync(extractedClientViews, cancellationToken).ConfigureAwait(false);
            }
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

    private async Task<bool> SnapshotClientViewsAsync(string destinationPath, CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(options.DataDirectory, ClientViewsEntry);
        if (!File.Exists(sourcePath)) return false;
        FileStream source;
        try
        {
            // The view store replaces this file atomically. Keep the opened version readable
            // without preventing another client from saving its next version.
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        await using (source)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var target = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        await ValidateClientViewsAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task ValidateClientViewsAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Die gespeicherten Clientzustände sind ungültig.");
        var clientIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var client in document.RootElement.EnumerateObject())
        {
            if (!IsValidClientId(client.Name) || !clientIds.Add(client.Name) || client.Value.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Das Backup enthält einen ungültigen Clientzustand.");
            foreach (var property in client.Value.EnumerateObject())
            {
                var value = property.Value;
                var valid = property.Name switch
                {
                    "mode" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var mode) && mode is >= 0 and <= 2,
                    "generalSession" or "codingSession" or "scienceSession" or "activeSession" or "activeProject"
                        => value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty,
                    "model" => value.ValueKind == JsonValueKind.Null || IsValidViewText(value, 512),
                    "sessionPaneOpen" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "reasoning" => ValidateViewDictionary(value, isDraft: false),
                    "drafts" => ValidateViewDictionary(value, isDraft: true),
                    _ => true,
                };
                if (!valid) throw new InvalidDataException($"Das Backup enthält ungültige Clientdaten in '{property.Name}'.");
            }
        }
    }

    private static bool ValidateViewDictionary(JsonElement value, bool isDraft)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(isDraft ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var entry in value.EnumerateObject())
        {
            if (!keys.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.String) return false;
            if (isDraft)
            {
                if (!Guid.TryParseExact(entry.Name, "D", out var id) || id == Guid.Empty || entry.Value.GetString()!.Length > 100_000) return false;
            }
            else if (!IsValidViewText(entry.Name, 512) || !IsValidViewText(entry.Value, 64)) return false;
        }
        return true;
    }

    private static bool IsValidClientId(string id) => id is { Length: > 0 and <= 128 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsValidViewText(JsonElement value, int maximumLength) => value.ValueKind == JsonValueKind.String
        && IsValidViewText(value.GetString()!, maximumLength);

    private static bool IsValidViewText(string value, int maximumLength) => value is { Length: > 0 }
        && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static async Task<string> ExtractValidatedEntriesAsync(string backupPath, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(Path.GetFullPath(backupPath), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var databaseEntry = archive.GetEntry(DatabaseEntry) is not null ? DatabaseEntry : LegacyProfileMigration.LegacyDatabaseName;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { databaseEntry, SettingsEntry, ManifestEntry };
        if (archive.GetEntry(ClientViewsEntry) is not null) allowed.Add(ClientViewsEntry);
        if (archive.Entries.Count != allowed.Count || archive.Entries.Any(entry => !allowed.Contains(entry.FullName)))
            throw new InvalidDataException("Das Backup enthält unerwartete oder fehlende Dateien.");
        foreach (var name in allowed)
        {
            var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Backup-Eintrag '{name}' fehlt.");
            await using var source = entry.Open();
            var targetPath = Path.Combine(destination, name);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
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

    private static void ReplaceDatabaseAndSettings(string databaseSource, string databaseDestination, string settingsSource, string settingsDestination,
        string? clientViewsSource, string clientViewsDestination)
    {
        (string? Source, string Destination)[] files = [(databaseSource, databaseDestination), (settingsSource, settingsDestination), (clientViewsSource, clientViewsDestination)];
        foreach (var file in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Destination) ?? throw new InvalidOperationException("Ungültiger Wiederherstellungspfad."));
            TryDeleteFile(file.Destination + ".restore-previous");
        }
        TryDeleteFile(databaseDestination + "-wal");
        TryDeleteFile(databaseDestination + "-shm");
        var backedUp = new bool[files.Length];
        var installed = new bool[files.Length];
        try
        {
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (File.Exists(file.Destination))
                {
                    File.Move(file.Destination, file.Destination + ".restore-previous");
                    backedUp[index] = true;
                }
            }
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                // An archive without views restores the previous absence of that cache.
                if (file.Source is null) continue;
                File.Move(file.Source, file.Destination);
                installed[index] = true;
            }
        }
        catch
        {
            for (var index = files.Length - 1; index >= 0; index--)
            {
                var destination = files[index].Destination;
                if (installed[index]) TryDeleteFile(destination);
                if (backedUp[index]) File.Move(destination + ".restore-previous", destination);
            }
            throw;
        }
    }

    private static void DeleteRestorePreviousFiles(string databasePath, string settingsPath, string clientViewsPath)
    {
        TryDeleteFile(databasePath + ".restore-previous");
        TryDeleteFile(settingsPath + ".restore-previous");
        TryDeleteFile(clientViewsPath + ".restore-previous");
    }

    private static void RollBackDatabaseAndSettings(string databasePath, string settingsPath, string clientViewsPath)
    {
        var previousDatabase = databasePath + ".restore-previous";
        var previousSettings = settingsPath + ".restore-previous";
        var previousClientViews = clientViewsPath + ".restore-previous";
        TryDeleteFile(databasePath + "-wal");
        TryDeleteFile(databasePath + "-shm");
        TryDeleteFile(databasePath);
        TryDeleteFile(settingsPath);
        TryDeleteFile(clientViewsPath);
        if (File.Exists(previousDatabase)) File.Move(previousDatabase, databasePath);
        if (File.Exists(previousSettings)) File.Move(previousSettings, settingsPath);
        if (File.Exists(previousClientViews)) File.Move(previousClientViews, clientViewsPath);
    }

    private static string CreateWorkingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Missum-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteFile(string path) { try { File.Delete(path); } catch (IOException) { } }
    private static void TryDeleteDirectory(string path) { try { Directory.Delete(path, recursive: true); } catch (IOException) { } }
    private sealed record BackupManifest(int FormatVersion, DateTimeOffset CreatedAt, int SchemaVersion, string DatabaseSha256, string SettingsSha256,
        string? ClientViewsSha256 = null);
}

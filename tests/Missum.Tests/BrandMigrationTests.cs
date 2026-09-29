using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;
using Missum.Infrastructure.Storage;

namespace Missum.Tests;

public sealed class BrandMigrationTests
{
    [Theory]
    [InlineData("goAiProtocolVersion")]
    [InlineData("protocolVersion")]
    public async Task LegacySettingsKeepConnectionAndSelectionsAndWriteOnlyNewKeys(string protocolKey)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = Assert.IsType<JsonSettingsStore>(environment.Get<ISettingsStore>());
        var sessionId = Guid.NewGuid();
        await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = AppSettings.CurrentVersion,
            ["aiProvider"] = "goAiServer",
            ["goAiServerUrl"] = "http://localhost:18080/",
            [protocolKey] = "2.7",
            ["selectedModel"] = "custom/general",
            ["selectedCodingModel"] = "custom/coding",
            ["activeSessionId"] = sessionId,
            ["isAiConnectionEnabled"] = false,
        }));

        var settings = await store.LoadAsync();

        Assert.Equal("http://localhost:18080", settings.MissumAiServerUrl);
        Assert.Equal("2.7", settings.MissumAiProtocolVersion);
        Assert.Equal("custom/general", settings.SelectedModel);
        Assert.Equal("custom/coding", settings.SelectedCodingModel);
        Assert.Equal(sessionId, settings.ActiveSessionId);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.Equal("http://localhost:18080", saved.RootElement.GetProperty("missumAiServerUrl").GetString());
        Assert.Equal("2.7", saved.RootElement.GetProperty("missumAiProtocolVersion").GetString());
        Assert.Equal("missumAiServer", saved.RootElement.GetProperty("aiProvider").GetString());
        Assert.False(saved.RootElement.TryGetProperty("goAiServerUrl", out _));
        Assert.False(saved.RootElement.TryGetProperty(protocolKey, out _));
        Assert.False(saved.RootElement.TryGetProperty("isAiConnectionEnabled", out _));
    }

    [Fact]
    public async Task ExplicitNewSettingsWinWhenBothSpellingsExist()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var store = Assert.IsType<JsonSettingsStore>(environment.Get<ISettingsStore>());
        await File.WriteAllTextAsync(store.SettingsPath, """
            {"version":21,"goAiServerUrl":"http://old.invalid:8080","goAiProtocolVersion":"old",
             "missumAiServerUrl":"http://new.local:9090","missumAiProtocolVersion":"new"}
            """);

        var settings = await store.LoadAsync();

        Assert.Equal("http://new.local:9090", settings.MissumAiServerUrl);
        Assert.Equal("new", settings.MissumAiProtocolVersion);
        Assert.DoesNotContain("goAiServerUrl", await File.ReadAllTextAsync(store.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseMigrationIncludesCommittedWalAndPreservesRunForeignKeys()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Vor der Umbenennung");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Historie", MessageStatus.Streaming);
        var run = await environment.Get<IMissumAiRunRepository>().CreateAsync(new MissumAiRunRecord(
            Guid.NewGuid(), session.Id, message.Id, null, "branding-test", "server-before-branding", 17, "running",
            "local-model", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var profile = Path.Combine(environment.Directory, "migrated-profile");
        Directory.CreateDirectory(profile);
        var legacyPath = Path.Combine(profile, "GO.db");
        await CopyDatabaseAsync(environment.Get<SqliteDatabase>().DatabasePath, legacyPath);
        await using var legacy = Open(legacyPath);
        await legacy.OpenAsync();
        await MakeLegacySchemaAsync(legacy);
        await ExecuteAsync(legacy, """
            PRAGMA journal_mode=WAL;
            PRAGMA wal_autocheckpoint=0;
            CREATE TABLE branding_wal_receipt(value TEXT NOT NULL);
            CREATE TABLE branding_run_reference(run_id TEXT NOT NULL REFERENCES go_ai_runs(id));
            PRAGMA wal_checkpoint(TRUNCATE);
            INSERT INTO branding_wal_receipt VALUES('committed-only-in-wal');
            """);
        await using (var insert = legacy.CreateCommand())
        {
            insert.CommandText = "INSERT INTO branding_run_reference VALUES($id);";
            insert.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            await insert.ExecuteNonQueryAsync();
        }
        Assert.True(new FileInfo(legacyPath + "-wal").Length > 0);

        await using var migrated = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = profile }, NullLogger<SqliteDatabase>.Instance);
        await migrated.InitializeAsync();

        Assert.Equal(Path.Combine(profile, "Missum.db"), migrated.DatabasePath);
        await using var current = Open(migrated.DatabasePath);
        await current.OpenAsync();
        Assert.Equal("committed-only-in-wal", await ScalarAsync(current, "SELECT value FROM branding_wal_receipt;"));
        Assert.Equal("server-before-branding", await ScalarAsync(current, "SELECT server_run_id FROM missum_ai_runs;"));
        Assert.Equal(17L, await ScalarAsync(current, "SELECT last_event_id FROM missum_ai_runs;"));
        Assert.Equal("missum_ai_runs", await ScalarAsync(current, "SELECT \"table\" FROM pragma_foreign_key_list('branding_run_reference');"));
        Assert.Equal(0L, await ScalarAsync(current, "SELECT COUNT(*) FROM sqlite_master WHERE name='go_ai_runs';"));
        Assert.Equal(0L, await ScalarAsync(current, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'ix_go_ai_%';"));
        Assert.Equal((long)SqliteDatabase.CurrentSchemaVersion, await ScalarAsync(current, "SELECT MAX(version) FROM schema_migrations;"));
        Assert.Equal(1L, await ScalarAsync(legacy, "SELECT COUNT(*) FROM go_ai_runs;"));
        Assert.Equal(0L, await ScalarAsync(legacy, "SELECT COUNT(*) FROM sqlite_master WHERE name='missum_ai_runs';"));
        await migrated.InitializeAsync(); // Repeated initialization is idempotent.
    }

    [Fact]
    public async Task ExistingMissumDatabaseAlwaysWinsOverLegacyFile()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Aktuelle Daten behalten");
        var legacyPath = Path.Combine(environment.Directory, "GO.db");
        const string marker = "This old file must not be opened or replaced.";
        await File.WriteAllTextAsync(legacyPath, marker);

        await using var reopened = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();

        Assert.Equal(marker, await File.ReadAllTextAsync(legacyPath));
        Assert.Equal(session.Id, Assert.Single(await environment.Get<IChatRepository>().ListSessionsAsync()).Id);
    }

    [Fact]
    public async Task LegacyBackupRestoresThenNormalizesDatabaseAndSettingsNames()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var original = await chats.CreateSessionAsync("Aus altem Archiv");
        var archive = await CreateLegacyArchiveAsync(environment, badDatabaseHash: false);
        _ = await chats.CreateSessionAsync("Nach dem Archiv");
        var backup = environment.Get<IBackupService>();

        await backup.ValidateAsync(archive);
        await backup.RestoreAsync(archive);

        Assert.Equal(original.Id, Assert.Single(await chats.ListSessionsAsync()).Id);
        Assert.True(File.Exists(Path.Combine(environment.Directory, "Missum.db")));
        Assert.False(File.Exists(Path.Combine(environment.Directory, "GO.db")));
        var settings = await environment.Get<ISettingsStore>().LoadAsync();
        Assert.Equal("http://legacy.local:8088", settings.MissumAiServerUrl);
        Assert.Equal("4.1", settings.MissumAiProtocolVersion);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(environment.Get<ISettingsStore>().SettingsPath));
        Assert.False(saved.RootElement.TryGetProperty("goAiServerUrl", out _));
        Assert.True(saved.RootElement.TryGetProperty("missumAiServerUrl", out _));
        await using var connection = Open(environment.Get<SqliteDatabase>().DatabasePath);
        await connection.OpenAsync();
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='missum_ai_runs';"));
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name='go_ai_runs';"));
        Assert.All(Directory.GetFiles(Path.Combine(environment.Directory, "Backups")), file => Assert.EndsWith(".missumbackup", file, StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidLegacyArchiveHashNeverChangesCurrentProfile()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var original = await chats.CreateSessionAsync("Unverändert");
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { MissumAiServerUrl = "http://keep.local:9091" });
        var settingsBytes = await File.ReadAllBytesAsync(environment.Get<ISettingsStore>().SettingsPath);
        var archive = await CreateLegacyArchiveAsync(environment, badDatabaseHash: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => environment.Get<IBackupService>().RestoreAsync(archive));

        Assert.Equal(original.Id, Assert.Single(await chats.ListSessionsAsync()).Id);
        Assert.Equal(settingsBytes, await File.ReadAllBytesAsync(environment.Get<ISettingsStore>().SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "Backups")));
    }

    private static async Task<string> CreateLegacyArchiveAsync(TestEnvironment environment, bool badDatabaseHash)
    {
        var fixture = Path.Combine(environment.Directory, "backup-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var legacyDatabase = Path.Combine(fixture, "GO.db");
        await CopyDatabaseAsync(environment.Get<SqliteDatabase>().DatabasePath, legacyDatabase);
        await using (var connection = Open(legacyDatabase))
        {
            await connection.OpenAsync();
            await MakeLegacySchemaAsync(connection);
            await ExecuteAsync(connection, "PRAGMA journal_mode=DELETE;");
        }
        var databaseBytes = await File.ReadAllBytesAsync(legacyDatabase);
        var settingsBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = AppSettings.CurrentVersion, aiProvider = "goAiServer",
            goAiServerUrl = "http://legacy.local:8088", goAiProtocolVersion = "4.1", selectedModel = "legacy-model",
        });
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1, createdAt = DateTimeOffset.UtcNow, schemaVersion = 1,
            databaseSha256 = badDatabaseHash ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(databaseBytes)),
            settingsSha256 = Convert.ToHexString(SHA256.HashData(settingsBytes)),
        });
        var path = Path.Combine(fixture, "legacy.gobackup");
        await using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write("GO.db", databaseBytes);
        Write("settings.json", settingsBytes);
        Write("manifest.json", manifestBytes);
        return path;

        void Write(string name, byte[] content)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(content);
        }
    }

    private static async Task CopyDatabaseAsync(string sourcePath, string targetPath)
    {
        await using var source = Open(sourcePath);
        await using var target = Open(targetPath);
        await source.OpenAsync();
        await target.OpenAsync();
        source.BackupDatabase(target);
        await ExecuteAsync(target, "PRAGMA journal_mode=DELETE;");
    }

    private static async Task MakeLegacySchemaAsync(SqliteConnection connection)
    {
        await ExecuteAsync(connection, """
            ALTER TABLE missum_ai_runs RENAME TO go_ai_runs;
            DROP INDEX ix_missum_ai_runs_resumable;
            DROP INDEX ix_missum_ai_runs_extension_action;
            CREATE INDEX ix_go_ai_runs_resumable ON go_ai_runs(state, updated_at);
            CREATE INDEX ix_go_ai_runs_extension_action ON go_ai_runs(extension_action_id, updated_at DESC);
            DELETE FROM schema_migrations WHERE version=50;
            """);
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 30,
    }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}

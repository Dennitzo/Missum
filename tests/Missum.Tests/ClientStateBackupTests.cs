using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ClientStateBackupTests
{
    private const string ViewsEntry = "ClientState/views.json";

    [Fact]
    public async Task RestoreRecoversClientDraftsAndNavigationTogetherWithChatsAndKeepsSafetySnapshot()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Before backup");
        var settingsStore = environment.Get<ISettingsStore>();
        using var settings = new SettingsCoordinator(settingsStore);
        await settings.InitializeAsync();
        await settings.UpdateAsync(value => value with { Language = "en-US" });
        using var views = new AssistantClientStateStore(environment.Directory);
        using (AssistantClientExecutionScope.Enter(views, "mac"))
        {
            await AssistantClientExecutionScope.UpdateAsync(settings, value => value with
            {
                SelectedChatMode = ChatMode.Coding, ActiveSessionId = session.Id, ActiveCodingSessionId = session.Id,
                SelectedModel = "mac-coding-model", IsAssistantSessionPaneOpen = false,
            }, CancellationToken.None);
            await AssistantClientExecutionScope.SaveDraftAsync(session.Id, "Original Mac draft", settings.Current, CancellationToken.None);
        }
        var requestsPath = Path.Combine(environment.Directory, "ClientState", "requests.json");
        await File.WriteAllTextAsync(requestsPath, "old receipts");
        var path = Path.Combine(environment.Directory, "clients.missumbackup");
        var backup = environment.Get<IBackupService>();
        _ = await backup.CreateAsync(path);

        using (var archive = ZipFile.OpenRead(path))
        {
            Assert.Equal(4, archive.Entries.Count);
            Assert.Null(archive.GetEntry("ClientState/requests.json"));
            var bytes = await ReadEntryAsync(archive, ViewsEntry);
            using var manifest = JsonDocument.Parse(await ReadEntryAsync(archive, "manifest.json"));
            Assert.Equal(2, manifest.RootElement.GetProperty("formatVersion").GetInt32());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                manifest.RootElement.GetProperty("clientViewsSha256").GetString());
        }
        await settingsStore.SaveAsync(new AppSettings { Language = "de-DE" });
        _ = await chats.CreateSessionAsync("After backup");
        using (AssistantClientExecutionScope.Enter(views, "mac"))
            await AssistantClientExecutionScope.SaveDraftAsync(session.Id, "Draft before restore", settings.Current, CancellationToken.None);
        await File.WriteAllTextAsync(requestsPath, "keep current request receipts");

        await backup.RestoreAsync(path);

        Assert.Equal(session.Id, Assert.Single(await chats.ListSessionsAsync()).Id);
        Assert.Equal("en-US", (await settingsStore.LoadAsync()).Language);
        using var restored = new AssistantClientStateStore(environment.Directory);
        using (AssistantClientExecutionScope.Enter(restored, "mac"))
        {
            var resolved = AssistantClientExecutionScope.Resolve(await settingsStore.LoadAsync());
            Assert.Equal(session.Id, resolved.ActiveSessionId);
            Assert.Equal(ChatMode.Coding, resolved.SelectedChatMode);
            Assert.Equal("mac-coding-model", resolved.SelectedModel);
            Assert.False(resolved.IsAssistantSessionPaneOpen);
            Assert.Equal("Original Mac draft", AssistantClientExecutionScope.Draft(session.Id, string.Empty, resolved));
        }
        Assert.Equal("keep current request receipts", await File.ReadAllTextAsync(requestsPath));
        var safetyPath = Assert.Single(Directory.GetFiles(Path.Combine(environment.Directory, "Backups"), "before-restore-*.missumbackup"));
        await backup.ValidateAsync(safetyPath);
        using var safety = ZipFile.OpenRead(safetyPath);
        using var safetyViews = JsonDocument.Parse(await ReadEntryAsync(safety, ViewsEntry));
        Assert.Equal("Draft before restore", safetyViews.RootElement.GetProperty("mac").GetProperty("drafts").GetProperty(session.Id.ToString("D")).GetString());
        Assert.Empty(Directory.GetFiles(Path.Combine(environment.Directory, "ClientState"), "*.restore-previous"));
    }

    [Fact]
    public async Task LegacyThreeEntryArchiveRestoresWithoutLeavingDraftsFromTheFuture()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var original = await chats.CreateSessionAsync("Legacy snapshot");
        var backup = environment.Get<IBackupService>();
        var path = Path.Combine(environment.Directory, "legacy.missumbackup");
        _ = await backup.CreateAsync(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var manifest = JsonNode.Parse(await ReadEntryAsync(archive, "manifest.json"))!;
            manifest["formatVersion"] = 1;
            _ = manifest.AsObject().Remove("clientViewsSha256");
            await ReplaceEntryAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
            Assert.Equal(3, archive.Entries.Count);
        }
        using var views = new AssistantClientStateStore(environment.Directory);
        await views.SaveDraftAsync("mac", original.Id, "Not part of the old backup", new AppSettings(), CancellationToken.None);
        _ = await chats.CreateSessionAsync("Later chat");

        await backup.ValidateAsync(path);
        await backup.RestoreAsync(path);

        Assert.Equal(original.Id, Assert.Single(await chats.ListSessionsAsync()).Id);
        Assert.False(File.Exists(Path.Combine(environment.Directory, ViewsEntry)));
        using var restored = new AssistantClientStateStore(environment.Directory);
        Assert.Empty(restored.Draft("mac", original.Id, "Desktop legacy text", new AppSettings()));
        using var safety = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(Path.Combine(environment.Directory, "Backups"), "*.missumbackup")));
        Assert.NotNull(safety.GetEntry(ViewsEntry));
    }

    [Fact]
    public async Task ClientViewsPayloadHashMismatchIsRejectedBeforeReplacingTheProfile()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { Language = "en-US" });
        using var views = new AssistantClientStateStore(environment.Directory);
        var session = await environment.Get<IChatRepository>().CreateSessionAsync("Unchanged chat");
        await views.SaveDraftAsync("mac", session.Id, "Keep draft", new AppSettings(), CancellationToken.None);
        var path = Path.Combine(environment.Directory, "bad-hash.missumbackup");
        var backup = environment.Get<IBackupService>();
        _ = await backup.CreateAsync(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            await ReplaceEntryAsync(archive, ViewsEntry, Encoding.UTF8.GetBytes("{}"));
        var originalViews = await File.ReadAllBytesAsync(Path.Combine(environment.Directory, ViewsEntry));
        var originalSettings = await File.ReadAllBytesAsync(environment.Get<ISettingsStore>().SettingsPath);

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.RestoreAsync(path));

        Assert.Equal(session.Id, Assert.Single(await environment.Get<IChatRepository>().ListSessionsAsync()).Id);
        Assert.Equal(originalViews, await File.ReadAllBytesAsync(Path.Combine(environment.Directory, ViewsEntry)));
        Assert.Equal(originalSettings, await File.ReadAllBytesAsync(environment.Get<ISettingsStore>().SettingsPath));
        Assert.False(Directory.Exists(Path.Combine(environment.Directory, "Backups")));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"mac\":null}")]
    [InlineData("{\"mac\":{\"reasoning\":null}}")]
    [InlineData("{\"mac\":{\"drafts\":null}}")]
    [InlineData("{\"mac\":{\"mode\":\"coding\"}}")]
    [InlineData("{\"mac\":{\"activeSession\":\"invalid-id\"}}")]
    [InlineData("{\"mac\":{\"drafts\":{\"invalid-id\":\"text\"}}}")]
    public async Task HashedButInvalidClientViewsAreRejected(string content)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var backup = environment.Get<IBackupService>();
        var path = Path.Combine(environment.Directory, "bad-shape.missumbackup");
        _ = await backup.CreateAsync(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await ReplaceEntryAsync(archive, ViewsEntry, bytes);
            await UpdateManifestHashAsync(archive, "clientViewsSha256", bytes);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.ValidateAsync(path));
    }

    [Fact]
    public async Task ManifestRequiringMissingViewsIsRejected()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var views = new AssistantClientStateStore(environment.Directory);
        await views.SaveDraftAsync("mac", Guid.NewGuid(), "Draft", new AppSettings(), CancellationToken.None);
        var path = Path.Combine(environment.Directory, "missing-views.missumbackup");
        var backup = environment.Get<IBackupService>();
        _ = await backup.CreateAsync(path);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update)) archive.GetEntry(ViewsEntry)!.Delete();

        await Assert.ThrowsAsync<InvalidDataException>(() => backup.ValidateAsync(path));
    }

    [Fact]
    public async Task FailureInstallingClientViewsRollsBackAlreadyInstalledDatabaseAndSettings()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        _ = await chats.CreateSessionAsync("Backup chat");
        using var views = new AssistantClientStateStore(environment.Directory);
        await views.SaveDraftAsync("mac", Guid.NewGuid(), "Backed up draft", new AppSettings(), CancellationToken.None);
        var path = Path.Combine(environment.Directory, "install-fails.missumbackup");
        var backup = environment.Get<IBackupService>();
        _ = await backup.CreateAsync(path);
        var later = await chats.CreateSessionAsync("Live chat must survive failure");
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { Language = "en-US" });
        var viewsPath = Path.Combine(environment.Directory, ViewsEntry);
        File.Delete(viewsPath);
        Directory.CreateDirectory(viewsPath);

        var failure = await Record.ExceptionAsync(() => backup.RestoreAsync(path));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.NotNull(await chats.GetSessionAsync(later.Id));
        Assert.Equal(2, (await chats.ListSessionsAsync()).Count);
        Assert.Equal("en-US", (await environment.Get<ISettingsStore>().LoadAsync()).Language);
        Assert.True(Directory.Exists(viewsPath));
        Assert.False(File.Exists(environment.DatabasePath + ".restore-previous"));
        Assert.False(File.Exists(environment.Get<ISettingsStore>().SettingsPath + ".restore-previous"));
    }

    [Fact]
    public async Task FailureInitializingImportedDatabaseRollsBackAllThreeFiles()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var original = await chats.CreateSessionAsync("Backup chat");
        using var views = new AssistantClientStateStore(environment.Directory);
        await views.SaveDraftAsync("mac", original.Id, "Backup draft", new AppSettings(), CancellationToken.None);
        var path = Path.Combine(environment.Directory, "migration-fails.missumbackup");
        var backup = environment.Get<IBackupService>();
        _ = await backup.CreateAsync(path);
        var importedDatabase = Path.Combine(environment.Directory, "imported.db");
        using (var archive = ZipFile.OpenRead(path)) archive.GetEntry("Missum.db")!.ExtractToFile(importedDatabase);
        await using (var connection = new SqliteConnection($"Data Source={importedDatabase};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Structurally intact SQLite with an inconsistent migration marker passes the
            // archive checks but fails initialization when migration 53 recreates its tables.
            command.CommandText = "DELETE FROM schema_migrations WHERE version=53;";
            await command.ExecuteNonQueryAsync();
        }
        var importedBytes = await File.ReadAllBytesAsync(importedDatabase);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            await ReplaceEntryAsync(archive, "Missum.db", importedBytes);
            await UpdateManifestHashAsync(archive, "databaseSha256", importedBytes);
        }
        await backup.ValidateAsync(path);
        var later = await chats.CreateSessionAsync("Live chat");
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings { Language = "en-US" });
        await views.SaveDraftAsync("mac", original.Id, "Live draft", new AppSettings(), CancellationToken.None);

        await Assert.ThrowsAsync<SqliteException>(() => backup.RestoreAsync(path));

        Assert.NotNull(await chats.GetSessionAsync(later.Id));
        Assert.Equal("en-US", (await environment.Get<ISettingsStore>().LoadAsync()).Language);
        using var restored = new AssistantClientStateStore(environment.Directory);
        Assert.Equal("Live draft", restored.Draft("mac", original.Id, string.Empty, new AppSettings()));
        Assert.False(File.Exists(Path.Combine(environment.Directory, ViewsEntry) + ".restore-previous"));
    }

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string name)
    {
        await using var source = archive.GetEntry(name)!.Open();
        await using var output = new MemoryStream();
        await source.CopyToAsync(output);
        return output.ToArray();
    }

    private static async Task ReplaceEntryAsync(ZipArchive archive, string name, byte[] bytes)
    {
        archive.GetEntry(name)?.Delete();
        await using var target = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
        await target.WriteAsync(bytes);
    }

    private static async Task UpdateManifestHashAsync(ZipArchive archive, string property, byte[] bytes)
    {
        var manifest = JsonNode.Parse(await ReadEntryAsync(archive, "manifest.json"))!;
        manifest[property] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await ReplaceEntryAsync(archive, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
    }
}

using Missum.Core.Contracts;
using Missum.Infrastructure;
using Missum.Infrastructure.Repositories;
using Missum.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ClaudeScienceMigrationTests
{
    [Fact]
    public async Task MigrationFortyNinePreservesSessionSearchRowIdsAcrossDeletedRowGap()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var sourceDatabase = environment.Get<IMissumDatabase>();
        var preMigrationBackup = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(environment.Directory, "DatabaseBackups"),
            "*.pre-v49-*.bak"));
        var migrationDirectory = Path.Combine(environment.Directory, "v49-rowid-gap");
        Directory.CreateDirectory(migrationDirectory);
        var databaseFileName = Path.GetFileName(sourceDatabase.DatabasePath);
        var migrationDatabasePath = Path.Combine(migrationDirectory, databaseFileName);
        environment.TrackDatabase(migrationDatabasePath);
        File.Copy(preMigrationBackup, migrationDatabasePath);

        var alphaId = Guid.NewGuid();
        var betaId = Guid.NewGuid();
        var gammaId = Guid.NewGuid();
        await using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = migrationDatabasePath,
            Pooling = false,
        }.ToString()))
        {
            await legacy.OpenAsync();
            await using var command = legacy.CreateCommand();
            command.CommandText = """
                INSERT INTO chat_sessions(id,title,created_at,updated_at,chat_mode)
                VALUES($alpha,'AlphaOnly',$now,$now,'general');
                INSERT INTO chat_sessions(id,title,created_at,updated_at,chat_mode)
                VALUES($beta,'BetaOnly',$now,$now,'general');
                INSERT INTO chat_sessions(id,title,created_at,updated_at,chat_mode)
                VALUES($gamma,'GammaOnly',$now,$now,'general');
                DELETE FROM chat_sessions WHERE id=$alpha;
                """;
            command.Parameters.AddWithValue("$alpha", alphaId.ToString("D"));
            command.Parameters.AddWithValue("$beta", betaId.ToString("D"));
            command.Parameters.AddWithValue("$gamma", gammaId.ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await using var migrated = new SqliteDatabase(
            new MissumInfrastructureOptions
            {
                DataDirectory = migrationDirectory,
                DatabaseFileName = databaseFileName,
            },
            NullLogger<SqliteDatabase>.Instance);
        await migrated.InitializeAsync();
        var chats = new SqliteChatRepository(migrated);

        var betaResult = Assert.Single(await chats.ListSessionsAsync("BetaOnly"));
        var gammaResult = Assert.Single(await chats.ListSessionsAsync("GammaOnly"));
        Assert.Equal(betaId, betaResult.Id);
        Assert.Equal(gammaId, gammaResult.Id);

        await using var verification = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = migrationDatabasePath,
            Pooling = false,
        }.ToString());
        await verification.OpenAsync();
        await using var verify = verification.CreateCommand();
        verify.CommandText = "SELECT rowid FROM chat_sessions WHERE id=$id;";
        var id = verify.Parameters.Add("$id", SqliteType.Text);
        id.Value = betaId.ToString("D");
        Assert.Equal(2L, (long)(await verify.ExecuteScalarAsync())!);
        id.Value = gammaId.ToString("D");
        Assert.Equal(3L, (long)(await verify.ExecuteScalarAsync())!);
    }
}

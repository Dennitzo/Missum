using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace Missum.Tests;

public sealed class SessionOnlyContextMigrationTests
{
    [Fact]
    public async Task MigrationBacksUpSharedDataAndPreservesChatAndSessionCompaction()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Existing session");
        var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, "Original $x^2$", MessageStatus.Completed);
        await chats.SaveToolStepAsync(message.Id, new("project", "context.projectCompact", "completed", "Shared summary"));
        await chats.SaveToolStepAsync(message.Id, new("session", "context.compact", "completed", "Own session summary"));
        await using (var connection = new SqliteConnection("Data Source=" + environment.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE project_context_snapshots(project_key TEXT, text TEXT);
                INSERT INTO project_context_snapshots VALUES('project','Shared knowledge');
                CREATE TABLE project_context_contributions(message_id TEXT);
                CREATE TABLE project_context_index_progress(session_id TEXT);
                CREATE VIRTUAL TABLE project_context_fts USING fts5(text);
                DELETE FROM schema_migrations WHERE version=57;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var reopened = new SqliteDatabase(new MissumInfrastructureOptions { DataDirectory = environment.Directory }, NullLogger<SqliteDatabase>.Instance);
        await reopened.InitializeAsync();
        await reopened.InitializeAsync();
        var stored = (await chats.GetMessageAsync(message.Id))!;
        Assert.Equal(message.Content, stored.Content);
        Assert.Equal(message.CreatedAt, stored.CreatedAt);
        Assert.Equal(session.Id, stored.SessionId);
        Assert.Equal("context.compact", Assert.Single(stored.ToolSteps!).Tool);
        Assert.Single(Directory.GetFiles(Path.Combine(environment.Directory, "DatabaseBackups"), "Missum.pre-session-only-*.bak"));
        await using var verify = new SqliteConnection("Data Source=" + environment.DatabasePath);
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'project_context_%';";
        Assert.Equal(0L, await check.ExecuteScalarAsync());
    }
}

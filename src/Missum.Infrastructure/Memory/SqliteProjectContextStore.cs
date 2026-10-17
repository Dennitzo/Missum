using System.Globalization;
using Missum.Ai.Contracts;
using Missum.Core.Memory;
using Missum.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Missum.Core.Models;
using System.Security.Cryptography;
using System.Text;

namespace Missum.Infrastructure.Memory;

/// <summary>Current semantic project knowledge and an indexed evidence catalogue, never a KV blob.</summary>
public sealed class SqliteProjectContextStore(SqliteDatabase database) : IProjectContextStore, IDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private volatile bool _initialized;

    private async Task InitializeAsync(CancellationToken token)
    {
        if (_initialized) return;
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await database.WriteAsync(async (connection, transaction, cancellation) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='project_context_fts';";
                var searchSql = await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) as string;
                var hasSearchIndex = searchSql?.Contains("body_text", StringComparison.Ordinal) == true;
                if (searchSql is not null && !hasSearchIndex)
                {
                    command.CommandText = """
                        DROP TRIGGER IF EXISTS project_context_fts_insert;
                        DROP TRIGGER IF EXISTS project_context_fts_delete;
                        DROP TRIGGER IF EXISTS project_context_fts_update;
                        DROP TABLE project_context_fts;
                        """;
                    await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS project_context_snapshots(
                        project_key TEXT PRIMARY KEY, generation INTEGER NOT NULL, content_hash TEXT NOT NULL,
                        text TEXT NOT NULL, source_watermark TEXT NULL, updated_at TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS project_context_contributions(
                        project_key TEXT NOT NULL, session_id TEXT NOT NULL, message_id TEXT NOT NULL,
                        message_revision INTEGER NOT NULL, text TEXT NOT NULL, updated_at TEXT NOT NULL, body_text TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY(project_key,message_id));
                    CREATE INDEX IF NOT EXISTS ix_project_context_recent
                        ON project_context_contributions(project_key,updated_at DESC,message_id);
                    CREATE TABLE IF NOT EXISTS project_context_index_progress(
                        project_key TEXT NOT NULL, session_id TEXT NOT NULL,
                        before_created_at TEXT NULL, before_message_id TEXT NULL, completed INTEGER NOT NULL,
                        session_revision INTEGER NOT NULL, updated_at TEXT NOT NULL,
                        newest_created_at TEXT NULL, newest_message_id TEXT NULL,
                        catch_up_before_created_at TEXT NULL, catch_up_before_message_id TEXT NULL,
                        catch_up_through_created_at TEXT NULL, catch_up_through_message_id TEXT NULL,
                        PRIMARY KEY(project_key,session_id));
                    """;
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                await EnsureIndexCursorColumnsAsync(connection, transaction, cancellation).ConfigureAwait(false);
                command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('project_context_contributions') WHERE name='body_text';";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false), CultureInfo.InvariantCulture) == 0)
                {
                    command.CommandText = "ALTER TABLE project_context_contributions ADD COLUMN body_text TEXT NOT NULL DEFAULT ''; UPDATE project_context_contributions SET body_text=text;";
                    await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }
                command.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS project_context_fts USING fts5(
                        project_key UNINDEXED, message_id UNINDEXED, body_text,
                        content='project_context_contributions', content_rowid='rowid', tokenize='unicode61');
                    CREATE TRIGGER IF NOT EXISTS project_context_fts_insert AFTER INSERT ON project_context_contributions BEGIN
                        INSERT INTO project_context_fts(rowid,project_key,message_id,body_text) VALUES(new.rowid,new.project_key,new.message_id,new.body_text);
                    END;
                    CREATE TRIGGER IF NOT EXISTS project_context_fts_delete AFTER DELETE ON project_context_contributions BEGIN
                        INSERT INTO project_context_fts(project_context_fts,rowid,project_key,message_id,body_text) VALUES('delete',old.rowid,old.project_key,old.message_id,old.body_text);
                    END;
                    CREATE TRIGGER IF NOT EXISTS project_context_fts_update AFTER UPDATE ON project_context_contributions BEGIN
                        INSERT INTO project_context_fts(project_context_fts,rowid,project_key,message_id,body_text) VALUES('delete',old.rowid,old.project_key,old.message_id,old.body_text);
                        INSERT INTO project_context_fts(rowid,project_key,message_id,body_text) VALUES(new.rowid,new.project_key,new.message_id,new.body_text);
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                if (!hasSearchIndex)
                {
                    command.CommandText = "INSERT INTO project_context_fts(project_context_fts) VALUES('rebuild');";
                    await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);
                }
            }, token).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    private static async Task EnsureIndexCursorColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(project_context_index_progress);";
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) columns.Add(reader.GetString(1));
        string[] required = ["newest_created_at", "newest_message_id", "catch_up_before_created_at", "catch_up_before_message_id",
            "catch_up_through_created_at", "catch_up_through_message_id"];
        foreach (var name in required)
        {
            if (columns.Contains(name)) continue;
            command.CommandText = "ALTER TABLE project_context_index_progress ADD COLUMN " + name + " TEXT NULL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<ProjectContextSnapshot?> GetAsync(string projectKey, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT generation,content_hash,text,source_watermark,updated_at FROM project_context_snapshots WHERE project_key=$key;";
        command.Parameters.AddWithValue("$key", projectKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(projectKey, reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)) : null;
    }

    public async Task<bool> PublishAsync(ProjectContextSnapshot snapshot, long expectedGeneration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Generation != expectedGeneration + 1) throw new ArgumentException("Die Projektfassung ist nicht die nächste Fassung.", nameof(snapshot));
        if (expectedGeneration < 0 || string.IsNullOrWhiteSpace(snapshot.ProjectKey) || snapshot.ProjectKey.Length > 4096
            || snapshot.Text is null || snapshot.Text.Length > 65_536 || snapshot.SourceWatermark?.Length > 4096
            || !string.Equals(snapshot.ContentHash, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(snapshot.Text))), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Ungültiger Projektkontext oder Inhalts-Hash.", nameof(snapshot));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_context_snapshots(project_key,generation,content_hash,text,source_watermark,updated_at)
                SELECT $key,$generation,$hash,$text,$watermark,$at
                WHERE $expected=0 OR EXISTS(SELECT 1 FROM project_context_snapshots WHERE project_key=$key AND generation=$expected)
                ON CONFLICT(project_key) DO UPDATE SET generation=excluded.generation,content_hash=excluded.content_hash,
                    text=excluded.text,source_watermark=excluded.source_watermark,updated_at=excluded.updated_at
                WHERE project_context_snapshots.generation=$expected;
                """;
            command.Parameters.AddWithValue("$key", snapshot.ProjectKey);
            command.Parameters.AddWithValue("$generation", snapshot.Generation);
            command.Parameters.AddWithValue("$expected", expectedGeneration);
            command.Parameters.AddWithValue("$hash", snapshot.ContentHash);
            command.Parameters.AddWithValue("$text", snapshot.Text);
            command.Parameters.AddWithValue("$watermark", (object?)snapshot.SourceWatermark ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", snapshot.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task CaptureAsync(ProjectContextContribution contribution, CancellationToken cancellationToken = default) =>
        CaptureBatchAsync([contribution], cancellationToken);

    public async Task CaptureBatchAsync(IReadOnlyList<ProjectContextContribution> contributions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contributions);
        if (contributions.Count > 128) throw new ArgumentException("Ein Indexbatch darf höchstens 128 Textnachrichten enthalten.", nameof(contributions));
        foreach (var contribution in contributions)
            if (contribution is null || string.IsNullOrWhiteSpace(contribution.ProjectKey) || contribution.ProjectKey.Length > 4096
                || contribution.SessionId == Guid.Empty || contribution.MessageId == Guid.Empty || contribution.MessageRevision < 0
                || contribution.Text is null || contribution.Text.Length > 65_536 || contribution.OriginalText?.Length > 4 * 1024 * 1024)
                throw new ArgumentException("Ungültiger Textbeitrag für den Projektindex.", nameof(contributions));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_context_contributions(project_key,session_id,message_id,message_revision,text,updated_at,body_text)
                VALUES($key,$session,$message,$revision,$text,$at,$body)
                ON CONFLICT(project_key,message_id) DO UPDATE SET message_revision=excluded.message_revision,
                    text=excluded.text,updated_at=excluded.updated_at,body_text=excluded.body_text
                WHERE project_context_contributions.message_revision<excluded.message_revision
                    AND project_context_contributions.session_id=excluded.session_id;
                """;
            foreach (var contribution in contributions)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$key", contribution.ProjectKey);
                command.Parameters.AddWithValue("$session", contribution.SessionId.ToString("D"));
                command.Parameters.AddWithValue("$message", contribution.MessageId.ToString("D"));
                command.Parameters.AddWithValue("$revision", contribution.MessageRevision);
                command.Parameters.AddWithValue("$text", contribution.Text);
                command.Parameters.AddWithValue("$at", contribution.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$body", contribution.OriginalText ?? contribution.Text);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectContextContribution>> ListAsync(string projectKey, int limit = 64,
        DateTimeOffset? after = null, Guid? afterMessageId = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,message_id,message_revision,text,updated_at FROM project_context_contributions
            WHERE project_key=$key AND ($after IS NULL OR updated_at>$after OR updated_at=$after AND message_id>$id)
            ORDER BY updated_at,message_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$key", projectKey);
        command.Parameters.AddWithValue("$after", after is { } at ? at.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$id", afterMessageId?.ToString("D") ?? "");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));
        var result = new List<ProjectContextContribution>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(projectKey, Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task<IReadOnlyList<ProjectContextContribution>> ListRecentAsync(string projectKey, int limit = 8,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.session_id,c.message_id,c.message_revision,c.text,c.updated_at
            FROM project_context_contributions c LEFT JOIN chat_messages m ON m.id=c.message_id
            WHERE c.project_key=$key ORDER BY COALESCE(m.updated_at,c.updated_at) DESC,c.message_id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$key", projectKey);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 128));
        return await ReadContributionsAsync(command, projectKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectContextContribution>> SearchAsync(string projectKey, string query, int limit = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 4000) throw new ArgumentException("Die Suchanfrage ist zu lang.", nameof(query));
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(static term => term.Any(char.IsLetterOrDigit)).Take(12)
            .Select(static term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        var expression = string.Join(" AND ", terms);
        if (expression.Length == 0) return [];
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.session_id,c.message_id,c.message_revision,
                snippet(project_context_fts,2,char(1),char(2),' … ',48),c.updated_at
            FROM project_context_fts JOIN project_context_contributions c ON c.rowid=project_context_fts.rowid
            WHERE project_context_fts MATCH $query AND c.project_key=$key
            ORDER BY bm25(project_context_fts),c.updated_at DESC,c.message_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$key", projectKey);
        command.Parameters.AddWithValue("$query", expression);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10));
        var rows = await ReadContributionsAsync(command, projectKey, cancellationToken).ConfigureAwait(false);
        return rows.Select(item => item with { Text = SearchExcerpt(item.Text) }).ToArray();
    }

    private static string SearchExcerpt(string text)
    {
        // An FTS "token" can itself be an 80k-character identifier. Center on
        // the actual highlighted hit before applying a byte bound, rather than
        // cutting a valid hit out of its returned context.
        var hit = text.IndexOf('\u0001', StringComparison.Ordinal);
        var start = Math.Max(0, hit - 384);
        if (hit >= 0)
            while (start < hit && Encoding.UTF8.GetByteCount(text.AsSpan(start, hit - start)) > 384) start++;
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        var excerpt = text.Substring(start, Math.Min(4096, text.Length - start))
            .Replace("\u0001", "", StringComparison.Ordinal).Replace("\u0002", "", StringComparison.Ordinal);
        var low = 0; var high = Math.Min(2048, excerpt.Length);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(excerpt.AsSpan(0, middle)) <= 2048) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && char.IsHighSurrogate(excerpt[low - 1])) low--;
        return excerpt[..low];
    }

    public async Task<ProjectContextContribution?> ReadAsync(string projectKey, Guid messageId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,message_id,message_revision,text,updated_at FROM project_context_contributions
            WHERE project_key=$key AND message_id=$id;
            """;
        command.Parameters.AddWithValue("$key", projectKey);
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        var rows = await ReadContributionsAsync(command, projectKey, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<ProjectContextIndexProgress>> ListIndexProgressAsync(string projectKey,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,before_created_at,before_message_id,completed,session_revision,updated_at,
                newest_created_at,newest_message_id,catch_up_before_created_at,catch_up_before_message_id,
                catch_up_through_created_at,catch_up_through_message_id
            FROM project_context_index_progress WHERE project_key=$key;
            """;
        command.Parameters.AddWithValue("$key", projectKey);
        var rows = new List<ProjectContextIndexProgress>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new(projectKey, Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) || reader.IsDBNull(2) ? null
                : new(DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(2))),
                reader.GetBoolean(3), reader.GetInt64(4), DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
                ReadCursor(reader, 6), ReadCursor(reader, 8), ReadCursor(reader, 10)));
        return rows;
    }

    public async Task SaveIndexProgressAsync(ProjectContextIndexProgress progress, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await database.WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO project_context_index_progress(project_key,session_id,before_created_at,before_message_id,completed,session_revision,updated_at,
                    newest_created_at,newest_message_id,catch_up_before_created_at,catch_up_before_message_id,catch_up_through_created_at,catch_up_through_message_id)
                VALUES($key,$session,$before,$id,$completed,$revision,$at,$newest_at,$newest_id,$catch_before_at,$catch_before_id,$catch_through_at,$catch_through_id)
                ON CONFLICT(project_key,session_id) DO UPDATE SET before_created_at=excluded.before_created_at,
                    before_message_id=excluded.before_message_id,completed=excluded.completed,session_revision=excluded.session_revision,updated_at=excluded.updated_at,
                    newest_created_at=excluded.newest_created_at,newest_message_id=excluded.newest_message_id,
                    catch_up_before_created_at=excluded.catch_up_before_created_at,catch_up_before_message_id=excluded.catch_up_before_message_id,
                    catch_up_through_created_at=excluded.catch_up_through_created_at,catch_up_through_message_id=excluded.catch_up_through_message_id;
                """;
            command.Parameters.AddWithValue("$key", progress.ProjectKey);
            command.Parameters.AddWithValue("$session", progress.SessionId.ToString("D"));
            command.Parameters.AddWithValue("$before", progress.Before is { } before ? before.CreatedAt.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
            command.Parameters.AddWithValue("$id", progress.Before is { } cursor ? cursor.Id.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$completed", progress.Completed);
            command.Parameters.AddWithValue("$revision", progress.SessionRevision);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            AddCursor(command, "$newest", progress.Newest);
            AddCursor(command, "$catch_before", progress.CatchUpBefore);
            AddCursor(command, "$catch_through", progress.CatchUpThrough);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ConversationCursor? ReadCursor(SqliteDataReader reader, int offset) =>
        reader.IsDBNull(offset) || reader.IsDBNull(offset + 1) ? null
            : new(DateTimeOffset.Parse(reader.GetString(offset), CultureInfo.InvariantCulture), Guid.Parse(reader.GetString(offset + 1)));

    private static void AddCursor(SqliteCommand command, string name, ConversationCursor? cursor)
    {
        command.Parameters.AddWithValue(name + "_at", cursor is null ? DBNull.Value : cursor.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(name + "_id", cursor is null ? DBNull.Value : cursor.Id.ToString("D"));
    }

    private static async Task<List<ProjectContextContribution>> ReadContributionsAsync(SqliteCommand command, string projectKey, CancellationToken token)
    {
        var result = new List<ProjectContextContribution>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            result.Add(new(projectKey, Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return result;
    }

    public void Dispose() => _initialization.Dispose();
}

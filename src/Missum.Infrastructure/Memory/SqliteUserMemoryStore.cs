using System.Globalization;
using Missum.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Missum.Infrastructure.Memory;

/// <summary>
/// Global user memories inside the same SQLite file as the project memory. Rows are kept
/// forever: a corrected rule stays as superseded and a forgotten rule stays as a tombstone,
/// so neither the derivation nor the user's "forget this" can be undone by an old chat.
/// </summary>
public sealed partial class SqliteProjectMemoryStore
{
    public const int MaximumUserMemoryCharacters = 2_000;
    private const int DefaultSearchLimit = 8;
    private const string LegacyOrigin = "legacy_project_memory";

    private const string UserEntryColumns = """
        id,kind,scope,status,content,content_key,project_key,project_id,
        source_message_id,session_id,origin,revision,supersedes_id,evidence_count,
        created_at,updated_at,last_confirmed_at
        """;

    // The full text index carries columns with the same names, so every query that joins
    // both tables has to name the entry table explicitly.
    private const string UserEntryColumnsAliased = """
        e.id,e.kind,e.scope,e.status,e.content,e.content_key,e.project_key,e.project_id,
        e.source_message_id,e.session_id,e.origin,e.revision,e.supersedes_id,e.evidence_count,
        e.created_at,e.updated_at,e.last_confirmed_at
        """;

    private static readonly string SelectUserEntriesSql =
        $"SELECT {UserEntryColumns} FROM user_memory_entries";

    internal static string CreateUserMemorySchemaSql() => """
        CREATE TABLE IF NOT EXISTS user_memory_entries(
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL CHECK(kind IN ('preference','instruction','correction','project_rule','fact')),
            scope TEXT NOT NULL CHECK(scope IN ('global','project')),
            status TEXT NOT NULL CHECK(status IN ('suggested','active','superseded','forgotten')),
            content TEXT NOT NULL,
            content_key TEXT NOT NULL,
            project_key TEXT NULL,
            project_id TEXT NULL,
            source_message_id TEXT NULL,
            session_id TEXT NULL,
            origin TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision > 0),
            supersedes_id TEXT NULL,
            evidence_count INTEGER NOT NULL DEFAULT 1 CHECK(evidence_count > 0),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            last_confirmed_at TEXT NULL,
            CHECK((scope = 'project' AND project_key IS NOT NULL) OR (scope = 'global' AND project_key IS NULL))
        );

        CREATE INDEX IF NOT EXISTS ix_user_memory_effective
            ON user_memory_entries(status,scope,project_key,created_at,id);
        CREATE INDEX IF NOT EXISTS ix_user_memory_content_key
            ON user_memory_entries(content_key);

        CREATE TABLE IF NOT EXISTS user_memory_evidence(
            entry_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            session_id TEXT NULL,
            PRIMARY KEY(entry_id,message_id)
        );

        CREATE TABLE IF NOT EXISTS user_memory_scope_changes(
            content_key TEXT NOT NULL,
            scope_key TEXT NOT NULL,
            entry_id TEXT NOT NULL,
            changed_at TEXT NOT NULL,
            PRIMARY KEY(content_key,scope_key)
        );

        CREATE TABLE IF NOT EXISTS user_memory_forgetmarks(
            marker TEXT PRIMARY KEY,
            content_key TEXT NULL,
            source_message_id TEXT NULL,
            project_key TEXT NULL,
            entry_id TEXT NULL,
            forgotten_at TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_user_memory_forgetmarks_content
            ON user_memory_forgetmarks(content_key);

        CREATE TABLE IF NOT EXISTS user_memory_profile(
            scope_key TEXT PRIMARY KEY,
            text TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK(revision >= 0),
            fingerprint TEXT NOT NULL,
            estimated_tokens INTEGER NOT NULL CHECK(estimated_tokens >= 0),
            generated_at TEXT NOT NULL,
            source_ids TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_memory_state(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS user_memory_processed_messages(
            message_id TEXT PRIMARY KEY,
            session_id TEXT NOT NULL,
            decision TEXT NOT NULL,
            entry_id TEXT NULL,
            processed_at TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS user_memory_search USING fts5(
            entry_id UNINDEXED,
            content,
            kind UNINDEXED,
            scope UNINDEXED,
            project_key UNINDEXED
        );
        """;

    public async Task<IReadOnlyList<UserMemoryEntry>> ListAsync(
        UserMemoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (query.Status is { } status)
        {
            filters.Add($"status = ${nameof(status)}");
            command.Parameters.AddWithValue($"${nameof(status)}", UserMemoryKindNames.ToStorage(status));
        }
        else if (!query.IncludeForgotten)
        {
            filters.Add("status IN ('suggested','active','superseded')");
        }

        if (NullIfBlank(query.ProjectKey) is { } projectKey)
        {
            filters.Add(query.IncludeGlobal
                ? "(scope = 'global' OR project_key = $project)"
                : "project_key = $project");
            command.Parameters.AddWithValue("$project", projectKey);
        }
        else if (!query.IncludeGlobal) return [];
        else filters.Add("scope = 'global'");

        var order = query.PreferRecent ? "created_at DESC,id" : "created_at,id";
        command.CommandText = SelectUserEntriesSql
            + (filters.Count > 0 ? " WHERE " + string.Join(" AND ", filters) : string.Empty)
            + $" ORDER BY CASE scope WHEN 'project' THEN 0 ELSE 1 END,{order} LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 2_000));
        return await ReadUserEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    // Explicit: the project store already has GetAsync for project entries. The two id types
    // are identical, so an overload would silently accept the wrong caller.
    async Task<UserMemoryEntry?> IUserMemoryStore.GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUserEntryAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserMemorySaveResult> SaveAsync(UserMemoryDraft draft, CancellationToken cancellationToken = default)
    {
        var validated = ValidateUserDraft(draft);
        return WriteAsync(async (connection, transaction, token) =>
        {
            if (validated.SupersedesId is { } supersededId)
            {
                var replacement = await ReplaceUserEntryCoreAsync(
                    connection, transaction, supersededId, expectedRevision: null, validated, token).ConfigureAwait(false);
                return new UserMemorySaveResult(replacement, Created: true, MergedWithExisting: false, EvidenceAdded: true);
            }

            var contentKey = UserMemoryKey.ContentKey(validated.Content);
            if (await IsForgottenCoreAsync(connection, transaction, validated.SourceMessageId, contentKey,
                    validated.ProjectKey, token)
                    .ConfigureAwait(false))
            {
                var tombstone = await FindUserEntryByContentKeyAsync(
                    connection, transaction, validated.ProjectKey, contentKey, token,
                    forgottenOnly: true).ConfigureAwait(false);
                return new UserMemorySaveResult(
                    tombstone ?? ForgottenFromDraft(validated), Created: false, MergedWithExisting: false,
                    SuppressedByForgetmark: true);
            }

            var duplicate = await FindUserEntryByContentKeyAsync(
                connection, transaction, validated.ProjectKey, contentKey, token).ConfigureAwait(false);
            if (duplicate is { Status: UserMemoryStatus.Active or UserMemoryStatus.Suggested })
            {
                var (merged, evidenceAdded) = await MergeEvidenceAsync(connection, transaction, duplicate, validated, token)
                    .ConfigureAwait(false);
                return new UserMemorySaveResult(merged, Created: false, MergedWithExisting: true,
                    EvidenceAdded: evidenceAdded);
            }

            if (duplicate is { Status: UserMemoryStatus.Superseded }
                && (validated.ObservedAt <= duplicate.UpdatedAt
                    || await HasUserEvidenceAsync(connection, transaction, duplicate.Id, validated.SourceMessageId,
                        token).ConfigureAwait(false)))
            {
                return new UserMemorySaveResult(duplicate, Created: false, MergedWithExisting: false);
            }

            var moved = await FindUserScopeReplayAsync(connection, transaction, contentKey, validated.ProjectKey,
                validated.SourceMessageId, validated.ObservedAt, token).ConfigureAwait(false);
            if (moved is not null)
            {
                return new UserMemorySaveResult(moved, Created: false, MergedWithExisting: false);
            }

            var created = NewUserEntry(validated);
            await InsertUserEntryAsync(connection, transaction, created, token).ConfigureAwait(false);
            await UpsertUserSearchAsync(connection, transaction, created, token).ConfigureAwait(false);
            return new UserMemorySaveResult(created, Created: true, MergedWithExisting: false, EvidenceAdded: true);
        }, cancellationToken);
    }

    public Task<UserMemoryEntry> ReviseAsync(
        Guid supersededId,
        long expectedRevision,
        UserMemoryDraft replacement,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedVersion(expectedRevision);
        var validated = ValidateUserDraft(replacement);
        return WriteAsync((connection, transaction, token) => ReplaceUserEntryCoreAsync(
            connection, transaction, supersededId, expectedRevision, validated, token), cancellationToken);
    }

    public Task<UserMemoryEntry> SetStatusAsync(
        Guid id,
        long expectedRevision,
        UserMemoryStatus status,
        DateTimeOffset? confirmedAt = null,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedVersion(expectedRevision);
        if (status is UserMemoryStatus.Forgotten)
        {
            throw new InvalidOperationException(
                "Eine vergessene Vorgabe wird über das eigene Vergessen entfernt, damit sie dauerhaft markiert bleibt.");
        }

        return MutateUserAsync(
            id,
            expectedRevision,
            "status=$status,last_confirmed_at=$confirmed",
            command =>
            {
                command.Parameters.AddWithValue("$status", UserMemoryKindNames.ToStorage(status));
                command.Parameters.AddWithValue("$confirmed", ToDbValue(status == UserMemoryStatus.Active
                    ? confirmedAt ?? DateTimeOffset.UtcNow
                    : confirmedAt));
            },
            cancellationToken);
    }

    public Task<UserMemoryEntry> SetScopeAsync(
        Guid id,
        long expectedRevision,
        UserMemoryScopeKind scope,
        string? projectKey = null,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureExpectedVersion(expectedRevision);
        if (scope == UserMemoryScopeKind.Project && string.IsNullOrWhiteSpace(projectKey))
        {
            throw new ArgumentException("Eine Projektvorgabe benötigt ihren Projektschlüssel.", nameof(projectKey));
        }

        var nextScopeKey = scope == UserMemoryScopeKind.Project ? projectKey!.Trim() : null;
        var nextProjectId = scope == UserMemoryScopeKind.Project ? NullIfBlank(projectId) : null;
        return WriteAsync(async (connection, transaction, token) =>
        {
            var current = await ReadUserEntryAsync(connection, transaction, id, token).ConfigureAwait(false)
                ?? throw new ProjectMemoryVersionConflictException(id);
            var moved = await MutateUserCoreAsync(connection, transaction, id, expectedRevision,
                "scope=$scope,project_key=$project_key,project_id=$project_id", command =>
                {
                    command.Parameters.AddWithValue("$scope", UserMemoryKindNames.ToStorage(scope));
                    command.Parameters.AddWithValue("$project_key", (object?)nextScopeKey ?? DBNull.Value);
                    command.Parameters.AddWithValue("$project_id", (object?)nextProjectId ?? DBNull.Value);
                }, token).ConfigureAwait(false);
            if (!string.Equals(current.ProjectKey, nextScopeKey, StringComparison.Ordinal))
            {
                await RecordUserScopeChangeAsync(connection, transaction, current, moved.UpdatedAt, token)
                    .ConfigureAwait(false);
            }

            return moved;
        }, cancellationToken);
    }

    public Task<UserMemoryEntry> ForgetAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default)
    {
        EnsureExpectedVersion(expectedRevision);
        return WriteAsync(async (connection, transaction, token) =>
        {
            var entry = await ReadUserEntryAsync(connection, transaction, id, token).ConfigureAwait(false)
                ?? throw new ProjectMemoryVersionConflictException(id);
            var updated = await MutateUserCoreAsync(
                connection, transaction, id, expectedRevision, "status=$status",
                command => command.Parameters.AddWithValue("$status", "forgotten"), token).ConfigureAwait(false);

            await using (var mark = connection.CreateCommand())
            {
                mark.Transaction = transaction;
                mark.CommandText = """
                    INSERT INTO user_memory_forgetmarks(marker,content_key,source_message_id,project_key,entry_id,forgotten_at)
                    VALUES($marker,$content_key,$source,$project,$entry,$now)
                    ON CONFLICT(marker) DO UPDATE SET
                        content_key=excluded.content_key,
                        source_message_id=excluded.source_message_id,
                        project_key=excluded.project_key,
                        entry_id=excluded.entry_id,
                        forgotten_at=excluded.forgotten_at;
                    """;
                var projectMarker = entry.ProjectKey is null ? string.Empty : entry.ProjectKey + ":";
                mark.Parameters.AddWithValue("$marker", "content:" + projectMarker + entry.ContentKey);
                mark.Parameters.AddWithValue("$content_key", entry.ContentKey);
                mark.Parameters.AddWithValue("$source", (object?)entry.SourceMessageId ?? DBNull.Value);
                mark.Parameters.AddWithValue("$project", (object?)entry.ProjectKey ?? DBNull.Value);
                mark.Parameters.AddWithValue("$entry", entry.Id.ToString("D"));
                mark.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
                await mark.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            if (entry.SourceMessageId is { Length: > 0 } sourceId)
            {
                await using var byMessage = connection.CreateCommand();
                byMessage.Transaction = transaction;
                byMessage.CommandText = """
                    INSERT INTO user_memory_forgetmarks(marker,content_key,source_message_id,project_key,entry_id,forgotten_at)
                    VALUES($marker,$content_key,$source,$project,$entry,$now)
                    ON CONFLICT(marker) DO UPDATE SET
                        content_key=excluded.content_key,
                        entry_id=excluded.entry_id,
                        forgotten_at=excluded.forgotten_at;
                    """;
                byMessage.Parameters.AddWithValue("$marker", "message:" + sourceId);
                byMessage.Parameters.AddWithValue("$content_key", entry.ContentKey);
                byMessage.Parameters.AddWithValue("$source", sourceId);
                byMessage.Parameters.AddWithValue("$project", (object?)entry.ProjectKey ?? DBNull.Value);
                byMessage.Parameters.AddWithValue("$entry", entry.Id.ToString("D"));
                byMessage.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
                await byMessage.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return updated;
        }, cancellationToken);
    }

    public async Task<bool> IsForgottenAsync(
        string? sourceMessageId,
        string contentKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentKey);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await IsForgottenCoreAsync(connection, null, sourceMessageId, contentKey, null, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<IReadOnlyList<UserMemoryEntry>> SearchAsync(
        string query,
        int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default) => SearchAsync(query, null, limit, cancellationToken);

    public async Task<IReadOnlyList<UserMemoryEntry>> SearchAsync(
        string query,
        string? projectKey,
        int limit = DefaultSearchLimit,
        CancellationToken cancellationToken = default)
    {
        var match = BuildMatchExpression(query);
        if (match.Length == 0) return [];
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {UserEntryColumnsAliased}
            FROM user_memory_search s
            JOIN user_memory_entries e ON e.rowid = s.rowid
            WHERE user_memory_search MATCH $match AND e.status='active'
                AND (e.scope='global' OR e.project_key=$project)
            ORDER BY bm25(user_memory_search), e.created_at, e.id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$project", (object?)NullIfBlank(projectKey) ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 32));
        return await ReadUserEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserProfileMemory> GetProfileAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT text,revision,fingerprint,estimated_tokens,generated_at,source_ids
            FROM user_memory_profile WHERE scope_key=$scope;
            """;
        command.Parameters.AddWithValue("$scope", scopeKey ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return UserProfileMemory.Empty(scopeKey);
        var ids = new List<Guid>();
        foreach (var value in reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(value, out var id)) ids.Add(id);
        }

        return new UserProfileMemory(
            scopeKey ?? string.Empty,
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            (int)reader.GetInt64(3),
            ReadDate(reader.GetString(4)),
            ids);
    }

    public Task<bool> SaveProfileAsync(UserProfileMemory profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO user_memory_profile(scope_key,text,revision,fingerprint,estimated_tokens,generated_at,source_ids)
                SELECT $scope,$text,$revision,$fingerprint,$tokens,$generated,$sources
                WHERE $revision=1 OR EXISTS(SELECT 1 FROM user_memory_profile WHERE scope_key=$scope)
                ON CONFLICT(scope_key) DO UPDATE SET
                    text=excluded.text,revision=excluded.revision,fingerprint=excluded.fingerprint,
                    estimated_tokens=excluded.estimated_tokens,generated_at=excluded.generated_at,
                    source_ids=excluded.source_ids
                WHERE excluded.revision=user_memory_profile.revision+1;
                """;
            command.Parameters.AddWithValue("$scope", profile.ScopeKey);
            command.Parameters.AddWithValue("$text", profile.Text);
            command.Parameters.AddWithValue("$revision", profile.Revision);
            command.Parameters.AddWithValue("$fingerprint", profile.Fingerprint);
            command.Parameters.AddWithValue("$tokens", profile.EstimatedTokens);
            command.Parameters.AddWithValue("$generated", ToDb(profile.GeneratedAt));
            command.Parameters.AddWithValue("$sources", string.Join(',', profile.SourceEntryIds.Select(id => id.ToString("D"))));
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, cancellationToken);
    }

    public async Task<UserMemoryBackfillCursor?> GetBackfillCursorAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value,updated_at FROM user_memory_state WHERE key='backfill-cursor';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var parts = reader.GetString(0).Split('|');
        if (parts.Length != 5
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sessionTicks)
            || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var messageTicks))
        {
            return null;
        }

        return new UserMemoryBackfillCursor(
            parts[1],
            new DateTimeOffset(sessionTicks, TimeSpan.Zero),
            parts[3],
            new DateTimeOffset(messageTicks, TimeSpan.Zero),
            long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0,
            ReadDate(reader.GetString(1)));
    }

    public Task SaveBackfillCursorAsync(UserMemoryBackfillCursor cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return WriteStateAsync("backfill-cursor", cursor.Position + "|"
            + cursor.ProcessedMessages.ToString(CultureInfo.InvariantCulture), cursor.UpdatedAt, cancellationToken);
    }

    public async Task<bool> IsMessageProcessedAsync(string messageId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM user_memory_processed_messages WHERE message_id=$id;";
        command.Parameters.AddWithValue("$id", messageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public Task MarkMessageProcessedAsync(
        string messageId,
        string sessionId,
        string decision,
        Guid? entryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);
        return WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO user_memory_processed_messages(message_id,session_id,decision,entry_id,processed_at)
                VALUES($id,$session,$decision,$entry,$now)
                ON CONFLICT(message_id) DO UPDATE SET
                    decision=excluded.decision,entry_id=excluded.entry_id,processed_at=excluded.processed_at;
                """;
            command.Parameters.AddWithValue("$id", messageId);
            command.Parameters.AddWithValue("$session", sessionId);
            command.Parameters.AddWithValue("$decision", decision);
            command.Parameters.AddWithValue("$entry", (object?)entryId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<int> MigrateLegacyEntriesAsync(CancellationToken cancellationToken = default) =>
        WriteAsync(async (connection, transaction, token) =>
        {
            var migrated = 0;
            var candidates = new List<(string Id, string ProjectKey, string? ProjectId, string Content,
                DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ConfirmedAt)>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = """
                    SELECT id,project_key,project_id,content,created_at,updated_at,last_confirmed_at
                    FROM project_memory_entries
                    WHERE kind IN ('requirement','decision') AND source='manual'
                    ORDER BY created_at,id;
                    """;
                await using var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    candidates.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3),
                        ReadDate(reader.GetString(4)),
                        ReadDate(reader.GetString(5)),
                        reader.IsDBNull(6) ? null : ReadDate(reader.GetString(6))));
                }
            }

            foreach (var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                var marker = "project-memory:" + candidate.Id;
                await using (var exists = connection.CreateCommand())
                {
                    exists.Transaction = transaction;
                    exists.CommandText = "SELECT 1 FROM user_memory_entries WHERE source_message_id=$marker;";
                    exists.Parameters.AddWithValue("$marker", marker);
                    if (await exists.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) continue;
                }

                var content = NormalizeUserContent(candidate.Content);
                if (content.Length == 0) continue;
                var contentKey = UserMemoryKey.ContentKey(content);
                if (await IsForgottenCoreAsync(connection, transaction, marker, contentKey, candidate.ProjectKey, token)
                    .ConfigureAwait(false)) continue;
                if (await FindUserScopeReplayAsync(connection, transaction, contentKey, candidate.ProjectKey, marker,
                        candidate.UpdatedAt, token).ConfigureAwait(false) is not null) continue;
                if (await FindUserEntryByContentKeyAsync(connection, transaction, candidate.ProjectKey, contentKey, token)
                        .ConfigureAwait(false) is not null) continue;
                var entry = new UserMemoryEntry(
                    Guid.NewGuid(),
                    UserMemoryKind.ProjectRule,
                    UserMemoryScopeKind.Project,
                    candidate.ConfirmedAt.HasValue ? UserMemoryStatus.Active : UserMemoryStatus.Suggested,
                    content,
                    contentKey,
                    candidate.ProjectKey,
                    candidate.ProjectId,
                    marker,
                    null,
                    LegacyOrigin,
                    1,
                    null,
                    1,
                    candidate.CreatedAt,
                    candidate.UpdatedAt,
                    candidate.ConfirmedAt);
                await InsertUserEntryAsync(connection, transaction, entry, token).ConfigureAwait(false);
                await UpsertUserSearchAsync(connection, transaction, entry, token).ConfigureAwait(false);
                migrated++;
            }

            return migrated;
        }, cancellationToken);

    private static string BuildMatchExpression(string query)
    {
        var terms = System.Text.RegularExpressions.Regex
            .Split(query ?? string.Empty, "[^\\p{L}\\p{N}]+")
            .Where(term => term.Length >= 3)
            .Take(8)
            .Select(term => "\"" + term.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"*");
        return string.Join(" OR ", terms);
    }

    private static UserMemoryDraft ValidateUserDraft(UserMemoryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Origin);
        if (draft.Status is UserMemoryStatus.Superseded or UserMemoryStatus.Forgotten)
        {
            throw new ArgumentException("Überschriebene und vergessene Vorgaben entstehen nur durch ihre eigene Änderung.",
                nameof(draft));
        }

        if (draft.Content.Length > 4_000)
        {
            throw new ArgumentOutOfRangeException(nameof(draft),
                $"Eine Nutzervorgabe darf höchstens 4.000 Zeichen enthalten; gespeichert werden {MaximumUserMemoryCharacters}.");
        }

        if (draft.Scope == UserMemoryScopeKind.Project && string.IsNullOrWhiteSpace(draft.ProjectKey))
        {
            throw new ArgumentException("Eine Projektvorgabe benötigt ihren Projektschlüssel.", nameof(draft));
        }

        return draft with
        {
            Content = NormalizeUserContent(draft.Content),
            Origin = draft.Origin.Trim(),
            ProjectKey = draft.Scope == UserMemoryScopeKind.Project
                ? draft.ProjectKey!.Trim()
                : null,
            ProjectId = draft.Scope == UserMemoryScopeKind.Project ? draft.ProjectId?.Trim() : null,
            SourceMessageId = NullIfBlank(draft.SourceMessageId),
            SessionId = NullIfBlank(draft.SessionId),
        };
    }

    /// <summary>One line per memory, so the profile stays readable and diffable.</summary>
    private static string NormalizeUserContent(string content)
    {
        var parts = content
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(" ", parts);
        joined = System.Text.RegularExpressions.Regex.Replace(joined, "[ \t]+", " ");
        return joined.Length <= MaximumUserMemoryCharacters ? joined : joined[..MaximumUserMemoryCharacters].TrimEnd();
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static UserMemoryEntry NewUserEntry(UserMemoryDraft draft, long revision = 1, int evidence = 1)
    {
        var now = draft.ObservedAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        return new UserMemoryEntry(
            Guid.NewGuid(),
            draft.Kind,
            draft.Scope,
            draft.Status,
            draft.Content,
            UserMemoryKey.ContentKey(draft.Content),
            draft.ProjectKey,
            draft.ProjectId,
            draft.SourceMessageId,
            draft.SessionId,
            draft.Origin,
            revision,
            draft.SupersedesId,
            evidence,
            now,
            now,
            draft.Status == UserMemoryStatus.Active ? now : null);
    }

    private static UserMemoryEntry ForgottenFromDraft(UserMemoryDraft draft) => NewUserEntry(
        draft with { Status = UserMemoryStatus.Active },
        revision: 1) with { Status = UserMemoryStatus.Forgotten };

    private static async Task<UserMemoryEntry> ReplaceUserEntryCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid supersededId,
        long? expectedRevision,
        UserMemoryDraft replacement,
        CancellationToken cancellationToken)
    {
        var old = await ReadUserEntryAsync(connection, transaction, supersededId, cancellationToken).ConfigureAwait(false);
        if (old is null || old.Status is UserMemoryStatus.Forgotten or UserMemoryStatus.Superseded
            || (expectedRevision is { } version && old.Revision != version))
        {
            throw new ProjectMemoryVersionConflictException(supersededId);
        }

        var replacementKey = UserMemoryKey.ContentKey(replacement.Content);
        if (await IsForgottenCoreAsync(connection, transaction, replacement.SourceMessageId, replacementKey,
                replacement.ProjectKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ProjectMemoryVersionConflictException(supersededId);
        }

        await using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE user_memory_entries
                SET status='superseded',revision=revision+1,updated_at=$updated
                WHERE id=$id;
                """;
            supersede.Parameters.AddWithValue("$id", supersededId.ToString("D"));
            supersede.Parameters.AddWithValue("$updated", ToDb(DateTimeOffset.UtcNow));
            if (await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new ProjectMemoryVersionConflictException(supersededId);
            }
        }

        await RemoveUserSearchAsync(connection, transaction, supersededId, cancellationToken).ConfigureAwait(false);

        var created = NewUserEntry(replacement with
        {
            SupersedesId = supersededId,
            Status = UserMemoryStatus.Active,
            SourceMessageId = replacement.SourceMessageId ?? old.SourceMessageId,
            SessionId = replacement.SessionId ?? old.SessionId,
        });
        await InsertUserEntryAsync(connection, transaction, created, cancellationToken).ConfigureAwait(false);
        await UpsertUserSearchAsync(connection, transaction, created, cancellationToken).ConfigureAwait(false);
        return created;
    }

    private static async Task<UserMemoryEntry?> FindUserEntryByContentKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? projectKey,
        string contentKey,
        CancellationToken cancellationToken,
        bool forgottenOnly = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SelectUserEntriesSql
            + """ WHERE content_key=$key AND (($project IS NULL AND scope='global') OR project_key=$project)"""
            + (forgottenOnly ? " AND status='forgotten'" : " AND status<>'forgotten'")
            + " ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'suggested' THEN 1 ELSE 2 END,updated_at DESC,id LIMIT 1;";
        command.Parameters.AddWithValue("$key", contentKey);
        command.Parameters.AddWithValue("$project", (object?)projectKey ?? DBNull.Value);
        var entries = await ReadUserEntriesAsync(command, cancellationToken).ConfigureAwait(false);
        return entries.Count > 0 ? entries[0] : null;
    }

    private static async Task<(UserMemoryEntry Entry, bool EvidenceAdded)> MergeEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserMemoryEntry existing,
        UserMemoryDraft incoming,
        CancellationToken cancellationToken)
    {
        var promote = incoming.Status == UserMemoryStatus.Active && existing.Status == UserMemoryStatus.Suggested;
        var added = await AddUserEvidenceAsync(connection, transaction, existing.Id, incoming.SourceMessageId,
            incoming.SessionId, cancellationToken).ConfigureAwait(false);
        if (!added && !promote) return (existing, false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE user_memory_entries
            SET evidence_count=evidence_count+$evidence,
                updated_at=$updated,
                revision=revision+1,
                status=CASE WHEN $promote=1 THEN 'active' ELSE status END,
                last_confirmed_at=CASE WHEN $promote=1 THEN $confirmed ELSE last_confirmed_at END
            WHERE id=$id;
            """;
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("$updated", ToDb(now));
        command.Parameters.AddWithValue("$evidence", added ? 1 : 0);
        command.Parameters.AddWithValue("$promote", promote ? 1 : 0);
        command.Parameters.AddWithValue("$confirmed", ToDbValue(incoming.ObservedAt ?? now));
        command.Parameters.AddWithValue("$id", existing.Id.ToString("D"));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ProjectMemoryVersionConflictException(existing.Id);
        }

        var merged = await ReadUserEntryAsync(connection, transaction, existing.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new ProjectMemoryVersionConflictException(existing.Id);
        if (promote) await UpsertUserSearchAsync(connection, transaction, merged, cancellationToken).ConfigureAwait(false);
        return (merged, added);
    }

    private static async Task<bool> AddUserEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        string? messageId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (messageId is null) return true;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_memory_evidence(entry_id,message_id,session_id) VALUES($entry,$message,$session)
            ON CONFLICT(entry_id,message_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$session", (object?)sessionId ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<bool> HasUserEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        string? messageId,
        CancellationToken cancellationToken)
    {
        if (messageId is null) return false;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM user_memory_evidence WHERE entry_id=$entry AND message_id=$message;";
        command.Parameters.AddWithValue("$entry", entryId.ToString("D"));
        command.Parameters.AddWithValue("$message", messageId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> IsForgottenCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string? sourceMessageId,
        string contentKey,
        string? projectKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM user_memory_forgetmarks
            WHERE (content_key=$content AND project_key IS $project) OR marker=$message
            UNION ALL
            SELECT 1 FROM user_memory_entries
            WHERE status='forgotten' AND content_key=$content AND project_key IS $project LIMIT 1;
            """;
        var messageMarker = NullIfBlank(sourceMessageId) is { } id ? "message:" + id : null;
        command.Parameters.AddWithValue("$content", contentKey);
        command.Parameters.AddWithValue("$project", (object?)projectKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$message", (object?)messageMarker ?? DBNull.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    // A scope narrowed by the user still wins when a later backfill reaches older chats.
    private static async Task RecordUserScopeChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserMemoryEntry previous,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_memory_scope_changes(content_key,scope_key,entry_id,changed_at)
            VALUES($key,$scope,$id,$at)
            ON CONFLICT(content_key,scope_key) DO UPDATE SET entry_id=excluded.entry_id,changed_at=excluded.changed_at;
            """;
        command.Parameters.AddWithValue("$key", previous.ContentKey);
        command.Parameters.AddWithValue("$scope", previous.ProjectKey ?? string.Empty);
        command.Parameters.AddWithValue("$id", previous.Id.ToString("D"));
        command.Parameters.AddWithValue("$at", ToDb(changedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UserMemoryEntry?> FindUserScopeReplayAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contentKey,
        string? projectKey,
        string? sourceMessageId,
        DateTimeOffset? observedAt,
        CancellationToken cancellationToken)
    {
        if (sourceMessageId is null && observedAt is null) return null;
        var matches = await ReadUserEntriesAsync(connection, transaction, $"""
            SELECT {UserEntryColumnsAliased} FROM user_memory_scope_changes h
            JOIN user_memory_entries e ON e.id=h.entry_id
            WHERE h.content_key=$key AND h.scope_key=$scope AND (
                h.changed_at >= $observed OR EXISTS (SELECT 1 FROM user_memory_evidence v
                    WHERE v.entry_id=h.entry_id AND v.message_id=$source));
            """, command =>
            {
                command.Parameters.AddWithValue("$key", contentKey);
                command.Parameters.AddWithValue("$scope", projectKey ?? string.Empty);
                command.Parameters.AddWithValue("$observed", ToDbValue(observedAt));
                command.Parameters.AddWithValue("$source", (object?)sourceMessageId ?? DBNull.Value);
            }, cancellationToken).ConfigureAwait(false);
        return matches.Count == 0 ? null : matches[0];
    }

    private async Task<UserMemoryEntry> MutateUserAsync(
        Guid id,
        long expectedRevision,
        string assignments,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        return await WriteAsync((connection, transaction, token) => MutateUserCoreAsync(
            connection, transaction, id, expectedRevision, assignments, addParameters, token), cancellationToken);
    }

    private static async Task<UserMemoryEntry> MutateUserCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid id,
        long expectedRevision,
        string assignments,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE user_memory_entries
            SET {assignments},revision=revision+1,updated_at=$updated
            WHERE id=$id AND revision=$expected AND status IN ('active','suggested');
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$expected", expectedRevision);
        command.Parameters.AddWithValue("$updated", ToDb(DateTimeOffset.UtcNow));
        addParameters(command);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new ProjectMemoryVersionConflictException(id);
        }

        var after = await ReadUserEntryAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false)
            ?? throw new ProjectMemoryVersionConflictException(id);
        await UpsertUserSearchAsync(connection, transaction, after, cancellationToken).ConfigureAwait(false);
        return after;
    }

    private static async Task InsertUserEntryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserMemoryEntry entry,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_memory_entries(
                id,kind,scope,status,content,content_key,project_key,project_id,source_message_id,
                session_id,origin,revision,supersedes_id,evidence_count,created_at,updated_at,last_confirmed_at)
            VALUES($id,$kind,$scope,$status,$content,$content_key,$project_key,$project_id,$source,
                $session,$origin,$revision,$supersedes,$evidence,$created,$updated,$confirmed);
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$kind", UserMemoryKindNames.ToStorage(entry.Kind));
        command.Parameters.AddWithValue("$scope", UserMemoryKindNames.ToStorage(entry.Scope));
        command.Parameters.AddWithValue("$status", UserMemoryKindNames.ToStorage(entry.Status));
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$content_key", entry.ContentKey);
        command.Parameters.AddWithValue("$project_key", (object?)entry.ProjectKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$project_id", (object?)entry.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)entry.SourceMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$session", (object?)entry.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$origin", entry.Origin);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$supersedes", (object?)entry.SupersedesId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$evidence", entry.EvidenceCount);
        command.Parameters.AddWithValue("$created", ToDb(entry.CreatedAt));
        command.Parameters.AddWithValue("$updated", ToDb(entry.UpdatedAt));
        command.Parameters.AddWithValue("$confirmed", ToDbValue(entry.LastConfirmedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await AddUserEvidenceAsync(connection, transaction, entry.Id, entry.SourceMessageId, entry.SessionId,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertUserSearchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserMemoryEntry entry,
        CancellationToken cancellationToken)
    {
        await RemoveUserSearchAsync(connection, transaction, entry.Id, cancellationToken).ConfigureAwait(false);
        if (!entry.IsEffective) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_memory_search(rowid,entry_id,content,kind,scope,project_key)
            SELECT rowid,$id,$content,$kind,$scope,$project FROM user_memory_entries WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
        command.Parameters.AddWithValue("$content", entry.Content);
        command.Parameters.AddWithValue("$kind", UserMemoryKindNames.ToStorage(entry.Kind));
        command.Parameters.AddWithValue("$scope", UserMemoryKindNames.ToStorage(entry.Scope));
        command.Parameters.AddWithValue("$project", (object?)entry.ProjectKey ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveUserSearchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid entryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM user_memory_search WHERE rowid=(SELECT rowid FROM user_memory_entries WHERE id=$id);";
        command.Parameters.AddWithValue("$id", entryId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<UserMemoryEntry?> ReadUserEntryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        var entries = await ReadUserEntriesAsync(connection, transaction,
            SelectUserEntriesSql + " WHERE id=$id;", command =>
                command.Parameters.AddWithValue("$id", id.ToString("D")), cancellationToken).ConfigureAwait(false);
        return entries.Count > 0 ? entries[0] : null;
    }

    private static async Task<IReadOnlyList<UserMemoryEntry>> ReadUserEntriesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUserEntriesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<UserMemoryEntry>> ReadUserEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        addParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await ReadUserEntriesAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<UserMemoryEntry>> ReadUserEntriesAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        var result = new List<UserMemoryEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new UserMemoryEntry(
                Guid.Parse(reader.GetString(0)),
                UserMemoryKindNames.FromStorage(reader.GetString(1)),
                UserMemoryKindNames.ScopeFromStorage(reader.GetString(2)),
                UserMemoryKindNames.StatusFromStorage(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetInt64(11),
                reader.IsDBNull(12) ? null : Guid.Parse(reader.GetString(12)),
                (int)reader.GetInt64(13),
                ReadDate(reader.GetString(14)),
                ReadDate(reader.GetString(15)),
                reader.IsDBNull(16) ? null : ReadDate(reader.GetString(16))));
        }

        return result;
    }

    private async Task WriteStateAsync(string key, string value, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        await WriteAsync(async (connection, transaction, token) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO user_memory_state(key,value,updated_at) VALUES($key,$value,$now)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value,updated_at=excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$now", ToDb(updatedAt));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Missum.Core.Models;
using Missum.Infrastructure.Storage;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    // This projection is deliberately separate from durable messages/model history.
    // SQLite returns compact headers, never the closed tools' potentially megabyte-sized payloads.
    private const string HeadersSql = """
        COALESCE(h.headers_json,(SELECT json_group_array(json_object(
            'id',json_extract(s.value,'$.id'),'tool',json_extract(s.value,'$.tool'),
            'status',json_extract(s.value,'$.status'),'contentOffset',json_extract(s.value,'$.contentOffset'),
            'startedAt',json_extract(s.value,'$.startedAt'),'completedAt',json_extract(s.value,'$.completedAt'),
            'updatedAt',json_extract(s.value,'$.updatedAt'),'agentId',json_extract(s.value,'$.agentId'),
            'explanation',substr(json_extract(s.value,'$.explanation'),1,400),
            'detail',CASE WHEN json_extract(s.value,'$.tool') IN ('assistant.narration','assistant.progress')
                THEN json_extract(s.value,'$.detail') ELSE NULL END,
            'outputJson',CASE WHEN json_extract(s.value,'$.tool') = 'assistant.continuation'
                AND json_valid(json_extract(s.value,'$.outputJson')) THEN json_object(
                    'sessionId',json_extract(s.value,'$.outputJson')->>'$.sessionId',
                    'messageId',json_extract(s.value,'$.outputJson')->>'$.messageId',
                    'localRunId',json_extract(s.value,'$.outputJson')->>'$.localRunId',
                    'priorActiveMilliseconds',json_extract(s.value,'$.outputJson')->>'$.priorActiveMilliseconds')||''
                WHEN json_extract(s.value,'$.tool') IN ('subagent','subagent.completed')
                AND json_valid(json_extract(s.value,'$.outputJson')) THEN json_object(
                    'agentId',json_extract(s.value,'$.outputJson')->>'$.agentId',
                    'runId',json_extract(s.value,'$.outputJson')->>'$.runId',
                    'parentRunId',json_extract(s.value,'$.outputJson')->>'$.parentRunId',
                    'sessionId',json_extract(s.value,'$.outputJson')->>'$.sessionId',
                    'title',json_extract(s.value,'$.outputJson')->>'$.title',
                    'status',json_extract(s.value,'$.outputJson')->>'$.status',
                    'resultDelivered',json_extract(s.value,'$.outputJson')->>'$.resultDelivered',
                    'projectionRevision',json_extract(s.value,'$.outputJson')->>'$.projectionRevision',
                    'attemptStartedAt',COALESCE(json_extract(s.value,'$.outputJson')->>'$.attemptStartedAt',
                        json_extract(s.value,'$.outputJson')->>'$.assistantMessage.createdAt'),
                    'subagentStorageKey',json_extract(s.value,'$.outputJson')->>'$.subagentStorageKey')||'' ELSE NULL END,
            'detailsDeferred',json(CASE WHEN json_extract(s.value,'$.tool') IN ('assistant.narration','assistant.progress')
                THEN 'false' ELSE 'true' END)
        )) FROM json_each(m.tool_steps_json) s))
        """;

    public async Task<ConversationPage?> GetPageAsync(Guid sessionId, int pageSize = 24, ConversationCursor? before = null,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,title,created_at,updated_at,chat_mode,draft,persistent_extension_action_id,conversation_revision,coding_workspace_path,session_group_id
            FROM chat_sessions WHERE id=$session;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        ChatSession? session;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            session = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? SqliteChatRepository.ReadSession(reader) : null;
        if (session is null) return null;
        command.CommandText = "SELECT COUNT(*) FROM chat_messages WHERE session_id=$session;";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = $"""
            SELECT m.id,m.session_id,m.role,m.content,m.status,m.created_at,m.updated_at,m.error,
                m.tool_name,m.tool_context,m.tool_status,NULL,m.tool_provider,NULL,m.content_profile,m.revision,
                {HeadersSql},h.message_id
            FROM chat_messages m LEFT JOIN chat_message_ui_headers h ON h.message_id=m.id
            WHERE m.session_id=$session AND ($before IS NULL OR (m.created_at,m.id)<($before,$beforeId))
            ORDER BY m.created_at DESC,m.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", before is null ? DBNull.Value : before.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$beforeId", before?.Id.ToString("D") ?? "");
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var messages = new List<ChatMessage>();
        var cache = new List<(Guid Id, long Revision, string Json)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = SqliteChatRepository.ReadMessage(reader);
                messages.Add(message);
                if (reader.IsDBNull(17)) cache.Add((message.Id, message.Revision, reader.GetString(16)));
            }
        var hasMore = messages.Count > pageSize;
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        messages.Reverse();
        var artifacts = new Dictionary<Guid, IReadOnlyList<ChatArtifact>>();
        if (messages.Count > 0)
        {
            command.Parameters.Clear();
            var keys = messages.Select((message, index) =>
            {
                var key = "$m" + index;
                command.Parameters.AddWithValue(key, message.Id.ToString("D"));
                return key;
            });
            command.CommandText = SqliteChatArtifactRepository.SelectSql
                + " WHERE a.message_id IN (" + string.Join(',', keys) + ") ORDER BY a.created_at,a.id;";
            artifacts = (await SqliteChatArtifactRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false))
                .GroupBy(a => a.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<ChatArtifact>)g.ToArray());
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Cache only the displayed page. No eager migration of old transcripts, no conversation revision change.
        if (cache.Count > 0) await database.WriteAsync(async (writer, tx, token) =>
        {
            await using var save = writer.CreateCommand();
            save.Transaction = tx;
            save.CommandText = """
                INSERT OR IGNORE INTO chat_message_ui_headers(message_id,headers_json)
                SELECT id,$headers FROM chat_messages WHERE id=$id AND revision=$revision;
                """;
            foreach (var item in cache)
            {
                save.Parameters.Clear();
                save.Parameters.AddWithValue("$id", item.Id.ToString("D"));
                save.Parameters.AddWithValue("$revision", item.Revision);
                save.Parameters.AddWithValue("$headers", item.Json);
                await save.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        return new(new(session, messages, artifacts), hasMore,
            messages.Count > 0 ? new(messages[0].CreatedAt, messages[0].Id) : null, total);
    }

    public async Task<AssistantToolStep?> GetToolStepAsync(Guid sessionId, Guid messageId, string stepId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.value FROM chat_messages m,json_each(m.tool_steps_json) s
            WHERE m.id=$message AND m.session_id=$session AND json_extract(s.value,'$.id')=$step LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$step", stepId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<AssistantToolStep>(json, JsonSerializerOptions.Web) : null;
    }
}

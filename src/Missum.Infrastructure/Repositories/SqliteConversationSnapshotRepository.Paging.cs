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
            'displayHeader',json_extract(s.value,'$.displayHeader'),
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
                    'kind',json_extract(s.value,'$.outputJson')->>'$.kind',
                    'originToolStepId',json_extract(s.value,'$.outputJson')->>'$.originToolStepId',
                    'originMessageId',json_extract(s.value,'$.outputJson')->>'$.originMessageId',
                    'gpuIndex',json_extract(s.value,'$.outputJson')->>'$.gpuIndex',
                    'resultDelivered',json_extract(s.value,'$.outputJson')->>'$.resultDelivered',
                    'projectionRevision',json_extract(s.value,'$.outputJson')->>'$.projectionRevision',
                    'transcriptRevision',json_extract(s.value,'$.outputJson')->>'$.transcriptRevision',
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
        command.CommandText = """
            SELECT m.id,m.session_id,m.role,m.content,m.status,m.created_at,m.updated_at,m.error,
                m.tool_name,m.tool_context,m.tool_status,NULL,m.tool_provider,NULL,m.content_profile,m.revision,NULL
            FROM chat_messages m
            WHERE m.session_id=$session AND ($before IS NULL OR (m.created_at,m.id)<($before,$beforeId))
            ORDER BY m.created_at DESC,m.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", before is null ? DBNull.Value : before.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$beforeId", before?.Id.ToString("D") ?? "");
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var messages = new List<ChatMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(SqliteChatRepository.ReadMessage(reader));
        var hasMore = messages.Count > pageSize;
        if (hasMore) messages.RemoveAt(messages.Count - 1);
        messages.Reverse();
        var windows = new Dictionary<Guid, ConversationToolStepWindow>();
        for (var index = 0; index < messages.Count; index++)
        {
            var toolPage = await ReadToolStepPageAsync(connection, transaction, messages[index].Id, 64, null, cancellationToken).ConfigureAwait(false);
            messages[index] = messages[index] with { ToolSteps = toolPage.Steps };
            if (toolPage.Window.TotalSteps > 0) windows[messages[index].Id] = toolPage.Window;
        }
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
            command.CommandText = SqliteChatArtifactRepository.DisplaySelectSql
                + " WHERE a.message_id IN (" + string.Join(',', keys) + ") ORDER BY a.created_at,a.id;";
            artifacts = (await SqliteChatArtifactRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false))
                .GroupBy(a => a.MessageId).ToDictionary(g => g.Key, g => (IReadOnlyList<ChatArtifact>)g.ToArray());
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(new(session, messages, artifacts), hasMore,
            messages.Count > 0 ? new(messages[0].CreatedAt, messages[0].Id) : null, total, windows);
    }

    public async Task<ConversationToolStepPage?> GetToolStepsPageAsync(Guid sessionId, Guid messageId, int pageSize = 64,
        long? beforeOrdinal = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM chat_messages WHERE id=$message AND session_id=$session;";
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return null;
        var page = await ReadToolStepPageAsync(connection, transaction, messageId, Math.Clamp(pageSize, 1, 128), beforeOrdinal, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }

    private static async Task<ConversationToolStepPage> ReadToolStepPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, int pageSize, long? beforeOrdinal, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chat_tool_step_messages WHERE message_id=$message);";
        var normalized = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 0;
        command.CommandText = normalized ? "SELECT COUNT(*) FROM chat_tool_steps WHERE message_id=$message;"
            : "SELECT COALESCE(json_array_length(tool_steps_json),0) FROM chat_messages WHERE id=$message;";
        var total = Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        command.CommandText = normalized ? """
            SELECT ordinal,header_json FROM chat_tool_steps WHERE message_id=$message AND ($before IS NULL OR ordinal<$before)
            ORDER BY ordinal DESC LIMIT $limit;
            """ : $"""
            SELECT CAST(s.key AS INTEGER),s.value
            FROM chat_messages m LEFT JOIN chat_message_ui_headers h ON h.message_id=m.id,json_each({HeadersSql}) s
            WHERE m.id=$message AND ($before IS NULL OR CAST(s.key AS INTEGER)<$before)
            ORDER BY CAST(s.key AS INTEGER) DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$before", (object?)beforeOrdinal ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var rows = new List<(long Ordinal, AssistantToolStep Step)>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (JsonSerializer.Deserialize<AssistantToolStep>(reader.GetString(1), JsonSerializerOptions.Web) is { } step)
                    rows.Add((reader.GetInt64(0), step));
        var more = rows.Count > pageSize;
        if (more) rows.RemoveAt(rows.Count - 1);
        rows.Reverse();
        await RefreshToolStepHeadersAsync(connection, transaction, messageId, normalized, rows, token).ConfigureAwait(false);
        return new(messageId, rows.Select(row => row.Step).ToArray(), new(total, more,
            rows.Count > 0 ? rows[0].Ordinal : null, RequiresLegacyNormalization: !normalized && total > 0));
    }

    private static async Task RefreshToolStepHeadersAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid messageId, bool normalized, List<(long Ordinal, AssistantToolStep Step)> rows, CancellationToken token)
    {
        var missing = rows.Where(row => row.Step.DisplayHeader?.Version != AssistantToolStepHeaders.CurrentVersion).ToArray();
        if (missing.Length == 0) return;

        // Upgrade only the visible page's disposable projection. Historical tool bodies remain unchanged,
        // and neither chat revisions nor model/KV history are touched by this lazy presentation migration.
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        var ordinalKeys = missing.Select((row, index) =>
        {
            var key = "$ordinal" + index;
            command.Parameters.AddWithValue(key, row.Ordinal);
            return key;
        }).ToArray();
        command.CommandText = normalized
            ? $"SELECT ordinal,step_json FROM chat_tool_steps WHERE message_id=$message AND ordinal IN ({string.Join(',', ordinalKeys)});"
            : $"""
                SELECT CAST(s.key AS INTEGER),s.value FROM chat_messages m,json_each(m.tool_steps_json) s
                WHERE m.id=$message AND CAST(s.key AS INTEGER) IN ({string.Join(',', ordinalKeys)});
                """;
        var replacements = new Dictionary<long, AssistantToolStep>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                if (JsonSerializer.Deserialize<AssistantToolStep>(reader.GetString(1), JsonSerializerOptions.Web) is { } step)
                    replacements[reader.GetInt64(0)] = ConversationToolStepStorage.Header(step);
        if (replacements.Count == 0) return;

        command.Parameters.Clear();
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        if (normalized)
        {
            command.CommandText = "UPDATE chat_tool_steps SET header_json=$header WHERE message_id=$message AND ordinal=$ordinal;";
            command.Parameters.Add("$header", SqliteType.Text);
            command.Parameters.Add("$ordinal", SqliteType.Integer);
            foreach (var (ordinal, header) in replacements)
            {
                command.Parameters["$header"].Value = JsonSerializer.Serialize(header, JsonSerializerOptions.Web);
                command.Parameters["$ordinal"].Value = ordinal;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        else
        {
            // Keep the legacy cache compact, including when it predates display headers. Updating all selected
            // entries together avoids rewriting the whole cached array once for every step in a large message.
            command.CommandText = $"""
                INSERT OR IGNORE INTO chat_message_ui_headers(message_id,headers_json)
                SELECT m.id,{HeadersSql} FROM chat_messages m
                LEFT JOIN chat_message_ui_headers h ON h.message_id=m.id WHERE m.id=$message;
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var replacementValues = replacements.Select((pair, index) =>
            {
                command.Parameters.AddWithValue("$ordinal" + index, pair.Key);
                command.Parameters.AddWithValue("$header" + index, JsonSerializer.Serialize(pair.Value, JsonSerializerOptions.Web));
                return "($ordinal" + index + ",$header" + index + ")";
            }).ToArray();
            command.CommandText = $"""
                WITH replacements(ordinal,header) AS (VALUES {string.Join(',', replacementValues)})
                UPDATE chat_message_ui_headers SET headers_json=(
                    SELECT json_group_array(json(header)) FROM (
                        SELECT COALESCE(replacement.header,step.value) AS header FROM json_each(headers_json) step
                        LEFT JOIN replacements replacement ON replacement.ordinal=CAST(step.key AS INTEGER)
                        ORDER BY CAST(step.key AS INTEGER)))
                WHERE message_id=$message;
                """;
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        for (var index = 0; index < rows.Count; index++)
            if (replacements.TryGetValue(rows[index].Ordinal, out var header)) rows[index] = (rows[index].Ordinal, header);
    }

    public async Task<AssistantToolStep?> GetToolStepAsync(Guid sessionId, Guid messageId, string stepId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT step.step_json FROM chat_tool_steps step JOIN chat_messages m ON m.id=step.message_id
            WHERE m.id=$message AND m.session_id=$session AND step.step_id=$step
            UNION ALL
            SELECT s.value FROM chat_messages m,json_each(m.tool_steps_json) s
            WHERE m.id=$message AND m.session_id=$session AND json_extract(s.value,'$.id')=$step
                AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$step", stepId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<AssistantToolStep>(json, JsonSerializerOptions.Web) : null;
    }

    public async Task<AssistantToolStep?> GetSubagentReceiptAsync(Guid sessionId, Guid messageId, string agentId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload FROM (
                SELECT step.step_json AS payload,step.ordinal AS ordinal FROM chat_tool_steps step JOIN chat_messages m ON m.id=step.message_id
                WHERE m.id=$message AND m.session_id=$session AND step.tool_name='subagent' AND step.child_agent_id=$agent
                UNION ALL
                SELECT s.value,CAST(s.key AS INTEGER) FROM chat_messages m,json_each(m.tool_steps_json) s
                WHERE m.id=$message AND m.session_id=$session AND json_extract(s.value,'$.tool')='subagent'
                    AND json_valid(json_extract(s.value,'$.outputJson')) AND json_extract(s.value,'$.outputJson')->>'$.agentId'=$agent
                    AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
            ) ORDER BY ordinal DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$message", messageId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$agent", agentId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<AssistantToolStep>(json, JsonSerializerOptions.Web) : null;
    }
}

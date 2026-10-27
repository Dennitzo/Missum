using Microsoft.Data.Sqlite;
using Missum.Core.Chat;
using Missum.Core.Models;
using Missum.Infrastructure.Storage;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    public async Task<IReadOnlyList<ConversationPrompt>> GetPromptsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Bounded user text and indexed steering headers only; no assistant
        // answers, tool outputs, artifacts or model context are loaded here.
        command.CommandText = """
            SELECT id,created_at,substr(content,1,256) FROM chat_messages
            WHERE session_id=$session AND role='user' AND length(trim(content))>0
            ORDER BY created_at,id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var prompts = new List<ConversationPrompt>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                if (!string.IsNullOrWhiteSpace(reader.GetString(2)))
                    prompts.Add(new(reader.ReadGuid(0), reader.ReadDate(1), reader.GetString(2)));
        prompts.AddRange((await ReadSteeringPromptsAsync(connection, transaction, sessionId, cancellationToken).ConfigureAwait(false))
            .Select(target => target.Prompt));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return prompts.OrderBy(prompt => prompt.CreatedAt)
            .ThenBy(prompt => prompt.ParentMessageId ?? prompt.Id)
            .ThenBy(prompt => prompt.StepOrdinal ?? -1).ThenBy(prompt => prompt.Id).ToArray();
    }

    public async Task<ConversationPage?> GetPromptPageAsync(Guid sessionId, Guid promptId, CancellationToken cancellationToken = default)
    {
        ConversationCursor? before = null;
        PromptTarget? target;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var transaction = connection.BeginTransaction(deferred: true))
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT created_at,substr(content,1,256) FROM chat_messages
                WHERE session_id=$session AND id=$prompt AND role='user' AND length(trim(content))>0;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$prompt", promptId.ToString("D"));
            target = null;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && !string.IsNullOrWhiteSpace(reader.GetString(1)))
                    target = new(new(promptId, reader.ReadDate(0), reader.GetString(1)), promptId, reader.ReadDate(0));
            target ??= (await ReadSteeringPromptsAsync(connection, transaction, sessionId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(item => item.Prompt.Id == promptId);
            if (target is null) return null;

            // A steering prompt belongs to its actual assistant row. Resolve
            // that owner inside this session, never from untrusted client hints.
            command.CommandText = """
                SELECT created_at,id FROM chat_messages
                WHERE session_id=$session AND (created_at,id)>=($at,$parent)
                ORDER BY created_at,id LIMIT 1 OFFSET 24;
                """;
            command.Parameters.AddWithValue("$at", target.ParentCreatedAt.ToDb());
            command.Parameters.AddWithValue("$parent", target.ParentId.ToString("D"));
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    before = new(reader.ReadDate(0), reader.ReadGuid(1));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        var page = await GetPageAsync(sessionId, before: before, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (page?.Conversation.Messages.Any(message => message.Id == target.ParentId) != true) return null;
        if (target.Prompt.StepOrdinal is { } ordinal)
        {
            // The target may predate thousands of closed steps. Load one thin
            // header window around it, without hydrating all tool details.
            var boundary = ordinal <= long.MaxValue - 64 ? (long?)(ordinal + 64) : null;
            var toolPage = await GetToolStepsPageAsync(sessionId, target.ParentId, 64, boundary, cancellationToken).ConfigureAwait(false);
            if (toolPage is null || !toolPage.Steps.Any(step => step.Id == target.Prompt.StepId
                && step.Tool == "assistant.steering" && step.Status is "running" or "completed"
                && !string.IsNullOrWhiteSpace(step.Detail))) return null;
            var windows = page.ToolStepWindows?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
            windows[target.ParentId] = toolPage.Window;
            page = page with
            {
                Conversation = page.Conversation with { Messages = page.Conversation.Messages.Select(message =>
                    message.Id == target.ParentId ? message with { ToolSteps = toolPage.Steps } : message).ToArray() },
                ToolStepWindows = windows,
            };
        }
        return page with { TargetPrompt = target.Prompt };
    }

    private sealed record PromptTarget(ConversationPrompt Prompt, Guid ParentId, DateTimeOffset ParentCreatedAt);

    private static async Task<IReadOnlyList<PromptTarget>> ReadSteeringPromptsAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        // Existing (message_id,tool_name,...) indexing filters normalized rows.
        // Older headers can lack steering text; only that steering row's detail
        // is used as fallback. Legacy JSON stays readable until normalization.
        command.CommandText = """
            SELECT m.id,m.created_at,s.step_id,s.ordinal,s.content_offset,
                COALESCE(json_extract(s.header_json,'$.startedAt'),json_extract(s.header_json,'$.completedAt'),
                    json_extract(s.header_json,'$.updatedAt'),m.created_at),
                substr(COALESCE(json_extract(s.header_json,'$.detail'),json_extract(s.step_json,'$.detail')),1,256)
            FROM chat_messages m JOIN chat_tool_steps s ON s.message_id=m.id
            WHERE m.session_id=$session AND m.role='assistant' AND s.tool_name='assistant.steering'
                AND EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
                AND json_extract(s.header_json,'$.status') IN ('running','completed')
            UNION ALL
            SELECT m.id,m.created_at,json_extract(s.value,'$.id'),CAST(s.key AS INTEGER),json_extract(s.value,'$.contentOffset'),
                COALESCE(json_extract(s.value,'$.startedAt'),json_extract(s.value,'$.completedAt'),
                    json_extract(s.value,'$.updatedAt'),m.created_at),substr(json_extract(s.value,'$.detail'),1,256)
            FROM chat_messages m,json_each(CASE WHEN json_valid(m.tool_steps_json) THEN m.tool_steps_json ELSE '[]' END) s
            WHERE m.session_id=$session AND m.role='assistant'
                AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
                AND json_extract(s.value,'$.tool')='assistant.steering'
                AND json_extract(s.value,'$.status') IN ('running','completed');
            """;
        var targets = new List<PromptTarget>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(2) || string.IsNullOrWhiteSpace(reader.GetString(2))
                || reader.IsDBNull(6) || string.IsNullOrWhiteSpace(reader.GetString(6))) continue;
            var parent = reader.ReadGuid(0);
            var parentAt = reader.ReadDate(1);
            var stepId = reader.GetString(2);
            var created = !reader.IsDBNull(5) && DateTimeOffset.TryParse(reader.GetString(5),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : parentAt;
            targets.Add(new(new(SteeringPromptIdentity.Create(parent, stepId), created, reader.GetString(6), parent, stepId,
                reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetInt64(3)), parent, parentAt));
        }
        return targets;
    }
}

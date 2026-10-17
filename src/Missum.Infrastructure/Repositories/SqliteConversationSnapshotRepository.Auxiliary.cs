using Missum.Core.Models;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    /// <summary>Thin cross-window metadata: old subagent tabs and continuation timing do not depend on the displayed tool page.</summary>
    public async Task<IReadOnlyList<ChatMessage>> GetAuxiliaryMessagesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH receipts(message_id,ordinal,header_json) AS (
                SELECT step.message_id,step.ordinal,step.header_json FROM chat_tool_steps step
                JOIN chat_messages owner ON owner.id=step.message_id
                WHERE owner.session_id=$session AND step.tool_name IN ('subagent','assistant.continuation','web.deepResearch')
                UNION ALL
                SELECT m.id,CAST(s.key AS INTEGER),s.value FROM chat_messages m
                LEFT JOIN chat_message_ui_headers h ON h.message_id=m.id,json_each({HeadersSql}) s
                WHERE m.session_id=$session AND json_extract(s.value,'$.tool') IN ('subagent','assistant.continuation','web.deepResearch')
                    AND NOT EXISTS(SELECT 1 FROM chat_tool_step_messages normalized WHERE normalized.message_id=m.id)
            )
            SELECT m.id,m.session_id,m.role,'',m.status,m.created_at,m.updated_at,m.error,
                NULL,NULL,NULL,NULL,NULL,NULL,m.content_profile,m.revision,json_array(json(receipt.header_json))
            FROM receipts receipt JOIN chat_messages m ON m.id=receipt.message_id
            ORDER BY m.created_at,m.id,receipt.ordinal;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var messages = new List<ChatMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(SqliteChatRepository.ReadMessage(reader));
        return messages.GroupBy(message => message.Id)
            .Select(group => group.First() with { ToolSteps = group.SelectMany(message => message.ToolSteps ?? []).ToArray() }).ToArray();
    }
}

using Missum.Core.Models;
using Missum.Infrastructure.Storage;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteChatRepository
{
    private const string TextMessageColumns = """
        id,session_id,role,content,status,created_at,updated_at,error,
        tool_name,tool_context,tool_status,NULL,tool_provider,context_summary,content_profile,revision,NULL
        """;

    public async Task<ChatMessage?> GetMessageTextAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TextMessageColumns} FROM chat_messages WHERE id=$id;";
        command.Parameters.AddWithValue("$id", messageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    public async Task<ChatMessage?> GetFirstUserMessageAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TextMessageColumns} FROM chat_messages WHERE session_id=$session AND role='user' ORDER BY created_at,id LIMIT 1;";
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadMessage(reader) : null;
    }

    public async Task<IReadOnlyList<ChatMessage>> ListContextMessagesAsync(Guid sessionId, int maximumMessages = 256,
        ConversationCursor? before = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {TextMessageColumns} FROM chat_messages
            WHERE session_id=$session AND ($before IS NULL OR (created_at,id)<($before,$beforeId))
            ORDER BY created_at DESC,id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$before", before is null ? DBNull.Value : before.CreatedAt.ToDb());
        command.Parameters.AddWithValue("$beforeId", before?.Id.ToString("D") ?? "");
        command.Parameters.AddWithValue("$limit", Math.Clamp(maximumMessages, 1, 512));
        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(ReadMessage(reader));
        messages.Reverse();
        return messages;
    }
}

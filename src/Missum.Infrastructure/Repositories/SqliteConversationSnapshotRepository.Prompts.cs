using Missum.Core.Models;
using Missum.Infrastructure.Storage;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteConversationSnapshotRepository
{
    public async Task<IReadOnlyList<ConversationPrompt>> GetPromptsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // The timeline needs bounded user text only, never answers, tool bodies,
        // artifacts or the model's durable context.
        command.CommandText = """
            SELECT id,created_at,substr(content,1,256) FROM chat_messages
            WHERE session_id=$session AND role='user' AND length(trim(content))>0
            ORDER BY created_at,id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        var prompts = new List<ConversationPrompt>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            prompts.Add(new(reader.ReadGuid(0), reader.ReadDate(1), reader.GetString(2)));
        return prompts;
    }

    public async Task<ConversationPage?> GetPromptPageAsync(Guid sessionId, Guid promptId, CancellationToken cancellationToken = default)
    {
        ConversationCursor? before = null;
        await using (var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT created_at FROM chat_messages WHERE session_id=$session AND id=$prompt AND role='user';";
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            command.Parameters.AddWithValue("$prompt", promptId.ToString("D"));
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string createdAt) return null;
            // Use the existing thin page projection. This cursor selects the
            // requested prompt plus its following messages in one bounded read.
            command.CommandText = """
                SELECT created_at,id FROM chat_messages
                WHERE session_id=$session AND (created_at,id)>=($at,$prompt)
                ORDER BY created_at,id LIMIT 1 OFFSET 24;
                """;
            command.Parameters.AddWithValue("$at", createdAt);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                before = new(reader.ReadDate(0), reader.ReadGuid(1));
        }
        var page = await GetPageAsync(sessionId, before: before, cancellationToken: cancellationToken).ConfigureAwait(false);
        return page?.Conversation.Messages.Any(message => message.Id == promptId) == true ? page : null;
    }
}

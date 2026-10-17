using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Missum.Infrastructure.Repositories;

/// <summary>
/// Reads every durable surface of one conversation from the same SQLite snapshot.
/// This prevents the WebView from observing a message revision together with stale
/// artifacts while a write transaction is being committed.
/// </summary>
public sealed partial class SqliteConversationSnapshotRepository(SqliteDatabase database)
    : IConversationSnapshotRepository
{
    public async Task<ConversationSnapshot?> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        ChatSession? session;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT id,title,created_at,updated_at,chat_mode,draft,
                       persistent_extension_action_id,conversation_revision,coding_workspace_path,
                       session_group_id
                FROM chat_sessions WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            session = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? SqliteChatRepository.ReadSession(reader)
                : null;
        }
        if (session is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var messages = new List<ChatMessage>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT m.id,m.session_id,m.role,m.content,m.status,m.created_at,m.updated_at,m.error,
                       m.tool_name,m.tool_context,m.tool_status,m.tool_detail,m.tool_provider,m.context_summary,
                       m.content_profile,m.revision,{ConversationToolStepStorage.CompleteStepsSql}
                FROM chat_messages m
                WHERE m.session_id=$session
                ORDER BY m.created_at,m.id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(SqliteChatRepository.ReadMessage(reader));
            }
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<ChatArtifact>> artifactMap;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = SqliteChatArtifactRepository.DisplaySelectSql + """

                JOIN chat_messages message ON message.id=a.message_id
                WHERE message.session_id=$session
                ORDER BY a.created_at,a.id;
                """;
            command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
            artifactMap = (await SqliteChatArtifactRepository.ReadAsync(command, cancellationToken).ConfigureAwait(false))
                .GroupBy(static artifact => artifact.MessageId)
                .ToDictionary(
                    static group => group.Key,
                    static group => (IReadOnlyList<ChatArtifact>)group.ToArray());
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ConversationSnapshot(session, messages, artifactMap);
    }
}

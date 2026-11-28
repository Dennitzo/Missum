using Missum.Core.Models;

namespace Missum.Infrastructure.Repositories;

public sealed partial class SqliteChatRepository
{
    private const int MaximumSearchTerms = 24;

    /// <summary>
    /// Bounded cross-session text search for the ChatGPT memory tool. SQLite's LIKE folds only ASCII
    /// letters, so callers pass the case variants they need (for example a capitalized umlaut word).
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> SearchMessagesAsync(IReadOnlyList<string> terms, int maximumMessages = 200,
        Guid? excludeSessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var bounded = terms.Where(static term => !string.IsNullOrWhiteSpace(term))
            .Select(static term => term.Trim())
            .Select(static term => term.Length > 128 ? term[..128] : term)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumSearchTerms)
            .ToArray();
        if (bounded.Length == 0) return [];

        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var conditions = new string[bounded.Length];
        for (var index = 0; index < bounded.Length; index++)
        {
            conditions[index] = "content LIKE $term" + index + " ESCAPE '!'";
            command.Parameters.AddWithValue("$term" + index, "%" + EscapeLike(bounded[index]) + "%");
        }
        command.CommandText = $"""
            SELECT {TextMessageColumns} FROM chat_messages
            WHERE role IN ('user','assistant') AND status IN ('completed','cancelled','interrupted')
              AND ($exclude IS NULL OR session_id<>$exclude)
              AND ({string.Join(" OR ", conditions)})
            ORDER BY created_at DESC,id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$exclude", excludeSessionId is { } excluded ? excluded.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(maximumMessages, 1, 1_000));
        var messages = new List<ChatMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) messages.Add(ReadMessage(reader));
        return messages;
    }

    private static string EscapeLike(string value) => value
        .Replace("!", "!!", StringComparison.Ordinal)
        .Replace("%", "!%", StringComparison.Ordinal)
        .Replace("_", "!_", StringComparison.Ordinal);
}

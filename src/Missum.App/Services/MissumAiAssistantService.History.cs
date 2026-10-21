using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task<IReadOnlyList<ChatMessage>> LoadModelHistoryAsync(Guid sessionId, CancellationToken token)
    {
        var pages = new List<IReadOnlyList<ChatMessage>>();
        ConversationCursor? before = null;
        while (true)
        {
            var page = await chats.ListContextMessagesAsync(sessionId, 512, before, token).ConfigureAwait(false);
            if (page.Count == 0) break;
            pages.Add(page);
            if (page.Count < 512) break;
            var first = page[0];
            before = new(first.CreatedAt, first.Id);
        }
        pages.Reverse();
        return pages.SelectMany(page => page).ToArray();
    }
}

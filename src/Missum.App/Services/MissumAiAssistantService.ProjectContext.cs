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

    private async Task<ProjectContextSnapshot?> PrepareSharedProjectContextAsync(ChatSession session, int contextTokens,
        string modelId, string role, bool supported, CancellationToken token)
    {
        if (!supported || projectContexts is null) return null;
        try
        {
            var workspace = session.CodingWorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace) && session.SessionGroupId is { } id)
                workspace = (await chats.ListSessionGroupsAsync(session.ChatMode, token).ConfigureAwait(false))
                    .FirstOrDefault(group => group.Id == id)?.WorkspacePath;
            return string.IsNullOrWhiteSpace(workspace) ? null
                : await projectContexts.GetAsync(workspace, contextTokens, modelId, role, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        { ProjectMemoryUnavailable(logger, session.Id.ToString("D"), error); return null; }
    }

    private async Task CaptureSharedProjectContextAsync(ChatSession? session, ChatMessage message)
    {
        if (projectContexts is null || session is null) return;
        try
        {
            var workspace = session.CodingWorkspacePath;
            if (string.IsNullOrWhiteSpace(workspace) && session.SessionGroupId is { } id)
                workspace = (await chats.ListSessionGroupsAsync(session.ChatMode, CancellationToken.None).ConfigureAwait(false))
                    .FirstOrDefault(group => group.Id == id)?.WorkspacePath;
            if (!string.IsNullOrWhiteSpace(workspace))
                await projectContexts.CaptureAsync(workspace, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ProjectMemoryUnavailable(logger, session.Id.ToString("D"), error); }
    }
}

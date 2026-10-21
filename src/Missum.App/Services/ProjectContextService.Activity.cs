using System.Collections.Concurrent;
using Missum.Ai.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class ProjectContextService
{
    private Func<ProjectContextSummarizeRequest, Func<Task>, CancellationToken, Task<ProjectContextSummarizeResult>>? _summarizeWithProgress;
    private readonly ConcurrentDictionary<string, Guid> _activityOwners = new(StringComparer.Ordinal);
    internal event Func<Guid, Guid, AssistantToolStep, Task>? CompactionChanged;

    private async Task<ChatMessage?> ActivityOwnerAsync(string key, Work work, IReadOnlyList<ProjectContextContribution> items, CancellationToken token)
    {
        var id = _activityOwners.GetValueOrDefault(key);
        if (id == Guid.Empty) id = items.LastOrDefault(IsAssistantReport)?.MessageId ?? Guid.Empty;
        if (id != Guid.Empty) return await _chats.GetMessageTextAsync(id, token).ConfigureAwait(false);
        var session = (await ProjectSessionsAsync(ProjectMemoryScope.FromWorkspace(work.Workspace), token).ConfigureAwait(false)).FirstOrDefault();
        return session is null ? null : (await _chats.ListContextMessagesAsync(session.Id, 8, cancellationToken: token).ConfigureAwait(false))
            .LastOrDefault(message => message.Role == ChatRole.Assistant && Indexable(message));
    }

    private async Task PersistCompactionAsync(ChatMessage? owner, string id, string status, string detail, DateTimeOffset started)
    {
        if (owner is null || owner.Status is MessageStatus.Pending or MessageStatus.Streaming) return;
        var now = DateTimeOffset.UtcNow;
        var step = await _chats.SaveBackgroundToolStepAsync(owner.Id, new(id, "context.projectCompact", status,
            Detail: detail, Explanation: "Projektkontext komprimieren", ContentOffset: owner.Content.Length,
            StartedAt: started, CompletedAt: status == "running" ? null : now, UpdatedAt: now), CancellationToken.None).ConfigureAwait(false);
        if (CompactionChanged is not { } changed) return;
        foreach (Func<Guid, Guid, AssistantToolStep, Task> observer in changed.GetInvocationList())
            try { await observer(owner.SessionId, owner.Id, step).ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { MaintenanceFailed(_logger, error); }
    }
}

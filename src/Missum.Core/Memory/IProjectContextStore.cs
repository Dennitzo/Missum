using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.Core.Memory;

public sealed record ProjectContextContribution(string ProjectKey, Guid SessionId, Guid MessageId,
    long MessageRevision, string Text, DateTimeOffset UpdatedAt, string? OriginalText = null);

public sealed record ProjectContextIndexProgress(string ProjectKey, Guid SessionId, ConversationCursor? Before,
    bool Completed, long SessionRevision, DateTimeOffset UpdatedAt = default,
    ConversationCursor? Newest = null, ConversationCursor? CatchUpBefore = null, ConversationCursor? CatchUpThrough = null);

public interface IProjectContextStore
{
    Task<ProjectContextSnapshot?> GetAsync(string projectKey, CancellationToken cancellationToken = default);
    Task<bool> PublishAsync(ProjectContextSnapshot snapshot, long expectedGeneration, CancellationToken cancellationToken = default);
    Task CaptureAsync(ProjectContextContribution contribution, CancellationToken cancellationToken = default);
    Task CaptureBatchAsync(IReadOnlyList<ProjectContextContribution> contributions, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectContextContribution>> ListAsync(string projectKey, int limit = 64,
        DateTimeOffset? after = null, Guid? afterMessageId = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectContextContribution>> ListRecentAsync(string projectKey, int limit = 8, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectContextContribution>> SearchAsync(string projectKey, string query, int limit = 5, CancellationToken cancellationToken = default);
    Task<ProjectContextContribution?> ReadAsync(string projectKey, Guid messageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectContextIndexProgress>> ListIndexProgressAsync(string projectKey, CancellationToken cancellationToken = default);
    Task SaveIndexProgressAsync(ProjectContextIndexProgress progress, CancellationToken cancellationToken = default);
}

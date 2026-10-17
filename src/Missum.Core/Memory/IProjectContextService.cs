using Missum.Ai.Contracts;
using Missum.Core.Models;

namespace Missum.Core.Memory;

public interface IProjectContextService
{
    Task<ProjectContextSnapshot?> GetAsync(string workspace, int contextTokens, string modelId, string role,
        CancellationToken cancellationToken = default);
    Task CaptureAsync(string workspace, ChatMessage message, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectContextContribution>> SearchAsync(string workspace, string query, int limit = 5,
        CancellationToken cancellationToken = default);
    Task<ProjectContextContribution?> ReadAsync(string workspace, Guid messageId, CancellationToken cancellationToken = default);
}

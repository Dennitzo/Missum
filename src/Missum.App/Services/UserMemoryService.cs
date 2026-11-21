using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>
/// Application side of the long term user memory: it decides which memories are in effect for a
/// session, renders the compact profile that every new request carries, and applies the changes
/// a user asks for in chat. Storage and rendering stay model independent; only recognition of new
/// statements needs an AI.
/// </summary>
public sealed class UserMemoryService(IUserMemoryStore memories, IChatRepository chats)
{
    public const string DisabledEnvironmentVariable = "MISSUM_USER_MEMORY_DISABLED";
    public const int ProfileTokenBudget = UserProfileFormatter.DefaultTokenBudget;

    /// <summary>Escape hatch for operators and acceptance runs, read per request.</summary>
    public static bool Disabled
        => Environment.GetEnvironmentVariable(DisabledEnvironmentVariable)?.Trim().ToLowerInvariant()
            is "1" or "true" or "yes" or "on";

    public async Task<UserMemoryScopeContext> ResolveScopeAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var groupId = session.SessionGroupId?.ToString("D");
        string? groupWorkspace = null;
        string? groupLabel = null;
        if (session.SessionGroupId is { } id)
        {
            var groups = await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false);
            var group = groups.FirstOrDefault(candidate => candidate.Id == id);
            if (group is not null)
            {
                groupWorkspace = group.WorkspacePath;
                groupLabel = group.Name;
            }
        }

        return UserMemoryScopeResolver.Resolve(session.CodingWorkspacePath, groupWorkspace, groupId, groupLabel);
    }

    public async Task<UserProfileMemory> ProfileForSessionAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        if (Disabled) return UserProfileMemory.Empty(string.Empty);
        var scope = await ResolveScopeAsync(session, cancellationToken).ConfigureAwait(false);
        return await ProfileForProjectAsync(scope.ProjectKey, cancellationToken).ConfigureAwait(false);
    }

    public Task<UserProfileMemory> ProfileForSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken) => ProfileBySessionCoreAsync(sessionId, cancellationToken);

    /// <summary>
    /// Resolves and persists the effective profile for one project view. An unchanged set of
    /// memories returns the stored revision untouched, so the prompt prefix keeps its cache.
    /// </summary>
    public async Task<UserProfileMemory> ProfileForProjectAsync(
        string? projectKey,
        CancellationToken cancellationToken)
    {
        var scopeKey = projectKey ?? string.Empty;
        if (Disabled) return UserProfileMemory.Empty(scopeKey);

        while (true)
        {
            var stored = await memories.GetProfileAsync(scopeKey, cancellationToken).ConfigureAwait(false);
            var entries = await memories.ListAsync(
                new UserMemoryQuery(Status: UserMemoryStatus.Active, ProjectKey: projectKey, Limit: 400, PreferRecent: true),
                cancellationToken).ConfigureAwait(false);
            var built = UserProfileFormatter.Build(entries, projectKey, ProfileTokenBudget);
            if (stored.Fingerprint == built.Fingerprint) return stored;

            var next = new UserProfileMemory(
                scopeKey,
                built.Text,
                stored.Revision + 1,
                built.Fingerprint,
                built.EstimatedTokens,
                DateTimeOffset.UtcNow,
                built.Included.Select(entry => entry.Id).ToArray());
            if (await memories.SaveProfileAsync(next, cancellationToken).ConfigureAwait(false)) return next;
        }
    }

    public async Task<UserMemorySaveResult> RememberAsync(
        UserMemoryDraft draft,
        CancellationToken cancellationToken)
    {
        var result = await memories.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(draft.ProjectKey, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<UserMemoryEntry> ReviseAsync(
        Guid supersededId,
        long expectedRevision,
        UserMemoryDraft replacement,
        CancellationToken cancellationToken)
    {
        var updated = await memories.ReviseAsync(supersededId, expectedRevision, replacement, cancellationToken)
            .ConfigureAwait(false);
        await RefreshAfterChangeAsync(updated.ProjectKey, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<UserMemoryEntry> ConfirmAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var entry = await memories.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Vorgabe wurde nicht gefunden.");
        var updated = await memories.SetStatusAsync(id, expectedRevision, UserMemoryStatus.Active,
            DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(entry.ProjectKey, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<UserMemoryEntry> MakeProjectScopedAsync(
        Guid id,
        long expectedRevision,
        UserMemoryScopeContext project,
        CancellationToken cancellationToken)
    {
        var entry = await memories.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Vorgabe wurde nicht gefunden.");
        var updated = await memories.SetScopeAsync(id, expectedRevision, UserMemoryScopeKind.Project,
            project.ProjectKey, project.ProjectId, cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(entry.ProjectKey, cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(project.ProjectKey, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<UserMemoryEntry> MakeGlobalAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var entry = await memories.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Vorgabe wurde nicht gefunden.");
        var updated = await memories.SetScopeAsync(id, expectedRevision, UserMemoryScopeKind.Global,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(entry.ProjectKey, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<UserMemoryEntry> ForgetAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var entry = await memories.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Vorgabe wurde nicht gefunden.");
        var updated = await memories.ForgetAsync(id, expectedRevision, cancellationToken).ConfigureAwait(false);
        await RefreshAfterChangeAsync(entry.ProjectKey, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>What the user asked for, as stored: content, scope, source and revision.</summary>
    public async Task<IReadOnlyList<UserMemoryEntry>> ListVisibleAsync(
        ChatSession session,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveScopeAsync(session, cancellationToken).ConfigureAwait(false);
        return await memories.ListAsync(
            new UserMemoryQuery(ProjectKey: scope.ProjectKey, IncludeGlobal: true, Limit: 100),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<UserMemoryEntry>> SearchAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default)
        => memories.SearchAsync(query, Math.Clamp(limit, 1, 32), cancellationToken);

    public Task<UserProfileMemory> StoredProfileAsync(string? projectKey, CancellationToken cancellationToken)
        => memories.GetProfileAsync(projectKey ?? string.Empty, cancellationToken);

    /// <summary>
    /// A profile change has to be visible in the next request of any other chat as well, so the
    /// affected project view and the global view are both rebuilt here.
    /// </summary>
    private async Task RefreshAfterChangeAsync(string? projectKey, CancellationToken cancellationToken)
    {
        await ProfileForProjectAsync(projectKey, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            await ProfileForProjectAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<UserProfileMemory> ProfileBySessionCoreAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die AI-Sitzung wurde nicht gefunden.");
        return await ProfileForSessionAsync(session, cancellationToken).ConfigureAwait(false);
    }
}

namespace Missum.Core.Memory;

/// <summary>
/// Durable store for global user memories. It lives beside the project memory in the same
/// SQLite file so one installation has one long term store, but the rows are separate:
/// project knowledge stays project knowledge and personal instructions never overwrite it.
/// </summary>
public interface IUserMemoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists global memories and, when a project key is supplied, that project's memories.</summary>
    Task<IReadOnlyList<UserMemoryEntry>> ListAsync(
        UserMemoryQuery query,
        CancellationToken cancellationToken = default);

    Task<UserMemoryEntry?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Stores one statement, merging duplicates and honouring forget marks.</summary>
    Task<UserMemorySaveResult> SaveAsync(UserMemoryDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an old statement with a corrected one in a single transaction. The old row
    /// remains as superseded, which keeps the derivation of the profile auditable.
    /// </summary>
    Task<UserMemoryEntry> ReviseAsync(
        Guid supersededId,
        long expectedRevision,
        UserMemoryDraft replacement,
        CancellationToken cancellationToken = default);

    Task<UserMemoryEntry> SetStatusAsync(
        Guid id,
        long expectedRevision,
        UserMemoryStatus status,
        DateTimeOffset? confirmedAt = null,
        CancellationToken cancellationToken = default);

    Task<UserMemoryEntry> SetScopeAsync(
        Guid id,
        long expectedRevision,
        UserMemoryScopeKind scope,
        string? projectKey = null,
        string? projectId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a memory from the active projection and marks it as forgotten forever.</summary>
    Task<UserMemoryEntry> ForgetAsync(Guid id, long expectedRevision, CancellationToken cancellationToken = default);

    Task<bool> IsForgottenAsync(
        string? sourceMessageId,
        string contentKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserMemoryEntry>> SearchAsync(
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default);

    /// <summary>Searches active global memories and those of the specified project.</summary>
    Task<IReadOnlyList<UserMemoryEntry>> SearchAsync(
        string query,
        string? projectKey,
        int limit = 8,
        CancellationToken cancellationToken = default);

    Task<UserProfileMemory> GetProfileAsync(string scopeKey, CancellationToken cancellationToken = default);
    /// <summary>Stores the next revision. A concurrent refresh can win and return false here.</summary>
    Task<bool> SaveProfileAsync(UserProfileMemory profile, CancellationToken cancellationToken = default);

    Task<UserMemoryBackfillCursor?> GetBackfillCursorAsync(CancellationToken cancellationToken = default);
    Task SaveBackfillCursorAsync(UserMemoryBackfillCursor cursor, CancellationToken cancellationToken = default);

    Task<bool> IsMessageProcessedAsync(string messageId, CancellationToken cancellationToken = default);
    Task MarkMessageProcessedAsync(
        string messageId,
        string sessionId,
        string decision,
        Guid? entryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Carries manually recorded project requirements and decisions into the new store.
    /// Repeatable: a second run adds nothing.
    /// </summary>
    Task<int> MigrateLegacyEntriesAsync(CancellationToken cancellationToken = default);
}

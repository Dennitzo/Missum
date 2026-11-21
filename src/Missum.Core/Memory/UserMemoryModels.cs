using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Missum.Core.Memory;

/// <summary>What kind of lasting statement a user memory records.</summary>
public enum UserMemoryKind
{
    /// <summary>Enduring way of working, phrasing or formatting the user asked for.</summary>
    Preference,

    /// <summary>Standing instruction that must be followed in later sessions.</summary>
    Instruction,

    /// <summary>Correction of earlier assistant behaviour that the user wants kept.</summary>
    Correction,

    /// <summary>Rule that only applies inside one project or workspace.</summary>
    ProjectRule,

    /// <summary>Durable personal fact, for example a role or a usual environment.</summary>
    Fact,
}

/// <summary>How far a memory reaches. Project scope never leaks into another project.</summary>
public enum UserMemoryScopeKind
{
    Global,
    Project,
}

/// <summary>
/// Lifecycle of a memory. Suggested entries wait for a later message that clarifies them,
/// forgotten entries stay in the table as tombstones so old chats cannot restore them.
/// </summary>
public enum UserMemoryStatus
{
    Suggested,
    Active,
    Superseded,
    Forgotten,
}

public sealed record UserMemoryEntry(
    Guid Id,
    UserMemoryKind Kind,
    UserMemoryScopeKind Scope,
    UserMemoryStatus Status,
    string Content,
    string ContentKey,
    string? ProjectKey,
    string? ProjectId,
    string? SourceMessageId,
    string? SessionId,
    string Origin,
    long Revision,
    Guid? SupersedesId,
    int EvidenceCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastConfirmedAt)
{
    public bool IsEffective => Status == UserMemoryStatus.Active;
}

/// <summary>
/// A statement before it is stored. Suggestions start inactive and become active either through
/// a later message or through the user; a deliberate replacement always stores as active.
/// </summary>
public sealed record UserMemoryDraft(
    UserMemoryKind Kind,
    UserMemoryScopeKind Scope,
    string Content,
    string Origin,
    UserMemoryStatus Status = UserMemoryStatus.Suggested,
    string? ProjectKey = null,
    string? ProjectId = null,
    string? SourceMessageId = null,
    string? SessionId = null,
    Guid? SupersedesId = null,
    DateTimeOffset? ObservedAt = null);

public sealed record UserMemoryQuery(
    UserMemoryStatus? Status = null,
    string? ProjectKey = null,
    bool IncludeGlobal = true,
    bool IncludeForgotten = false,
    int Limit = 200,
    bool PreferRecent = false);

/// <summary>
/// A duplicate adds evidence to the existing entry. A forget mark blocks old chats from
/// restoring a statement, and replaying a superseded statement does not make it active again.
/// </summary>
public sealed record UserMemorySaveResult(
    UserMemoryEntry Entry,
    bool Created,
    bool MergedWithExisting,
    bool SuppressedByForgetmark = false,
    bool EvidenceAdded = false);

/// <summary>
/// Rendered profile for one scope. The scope key is part of the value, so a project profile and
/// the global profile keep their own revision instead of overwriting each other on every turn.
/// </summary>
public sealed record UserProfileMemory(
    string ScopeKey,
    string Text,
    long Revision,
    string Fingerprint,
    int EstimatedTokens,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<Guid> SourceEntryIds)
{
    public static UserProfileMemory Empty(string? scopeKey = null) => new(
        scopeKey ?? string.Empty, string.Empty, 0, UserMemoryKey.Fingerprint(string.Empty), 0,
        DateTimeOffset.UnixEpoch, []);

    public bool HasContent => Text.Length > 0;
}

/// <summary>Project identity of a chat session, resolved from Missums own session assignment.</summary>
public sealed record UserMemoryScopeContext(string? ProjectKey, string? ProjectId, string? Label)
{
    public static readonly UserMemoryScopeContext Global = new(null, null, null);
}

/// <summary>
/// Durable read position for the incremental chat review. Both the session and the message
/// inside it are recorded, because sessions are walked in creation order while a long running
/// session keeps receiving messages after its own start.
/// </summary>
public sealed record UserMemoryBackfillCursor(
    string LastSessionId,
    DateTimeOffset LastSessionCreatedAt,
    string LastMessageId,
    DateTimeOffset LastMessageCreatedAt,
    long ProcessedMessages,
    DateTimeOffset UpdatedAt)
{
    /// <summary>One comparable string, used to detect finished work.</summary>
    public string Position => FormattableString.Invariant(
        $"{LastSessionCreatedAt.UtcTicks}|{LastSessionId}|{LastMessageCreatedAt.UtcTicks}|{LastMessageId}");
}

/// <summary>Per message review outcome, so a restart neither repeats nor skips a message.</summary>
public sealed record UserMemoryProcessedMessage(
    string MessageId,
    string SessionId,
    string Decision,
    Guid? EntryId,
    DateTimeOffset ProcessedAt);

public static class UserMemoryKey
{
    private static readonly Regex Punctuation = new("[\\p{P}\\p{S}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new("\\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Normalised identity of a statement. Case, punctuation and whitespace are ignored so a
    /// reformulated preference still meets the stored one; wording differences that change the
    /// statement itself remain distinct.
    /// </summary>
    public static string ContentKey(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var normalized = Punctuation.Replace(content, " ");
        normalized = Whitespace.Replace(normalized.Trim(), " ");
        normalized = normalized.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
    }

    /// <summary>Stable fingerprint of a rendered profile; the prompt prefix cache keys on it.</summary>
    public static string Fingerprint(string profileText)
        => "profile-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileText))).ToLowerInvariant();
}

public static class UserMemoryKindNames
{
    public static string ToStorage(UserMemoryKind kind) => kind switch
    {
        UserMemoryKind.Preference => "preference",
        UserMemoryKind.Instruction => "instruction",
        UserMemoryKind.Correction => "correction",
        UserMemoryKind.ProjectRule => "project_rule",
        UserMemoryKind.Fact => "fact",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static UserMemoryKind FromStorage(string value) => value switch
    {
        "preference" => UserMemoryKind.Preference,
        "instruction" => UserMemoryKind.Instruction,
        "correction" => UserMemoryKind.Correction,
        "project_rule" => UserMemoryKind.ProjectRule,
        "fact" => UserMemoryKind.Fact,
        _ => throw new InvalidDataException($"Unbekannter Nutzererinnerungstyp '{value}'."),
    };

    public static string ToStorage(UserMemoryScopeKind scope) => scope switch
    {
        UserMemoryScopeKind.Global => "global",
        UserMemoryScopeKind.Project => "project",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    public static UserMemoryScopeKind ScopeFromStorage(string value) => value switch
    {
        "global" => UserMemoryScopeKind.Global,
        "project" => UserMemoryScopeKind.Project,
        _ => throw new InvalidDataException($"Unbekannter Geltungsbereich '{value}'."),
    };

    public static string ToStorage(UserMemoryStatus status) => status switch
    {
        UserMemoryStatus.Suggested => "suggested",
        UserMemoryStatus.Active => "active",
        UserMemoryStatus.Superseded => "superseded",
        UserMemoryStatus.Forgotten => "forgotten",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static UserMemoryStatus StatusFromStorage(string value) => value switch
    {
        "suggested" => UserMemoryStatus.Suggested,
        "active" => UserMemoryStatus.Active,
        "superseded" => UserMemoryStatus.Superseded,
        "forgotten" => UserMemoryStatus.Forgotten,
        _ => throw new InvalidDataException($"Unbekannter Status '{value}'."),
    };
}

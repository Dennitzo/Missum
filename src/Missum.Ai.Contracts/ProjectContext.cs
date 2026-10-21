namespace Missum.Ai.Contracts;

/// <summary>Bounded, versioned project knowledge; never an authorization or a shared mutable chat.</summary>
public sealed record ProjectContextRef(string ProjectKey, long Generation, string ContentHash, string Text,
    string? SourceWatermark = null);

public sealed record ProjectContextSnapshot(string ProjectKey, long Generation, string ContentHash, string Text,
    string? SourceWatermark, DateTimeOffset UpdatedAt);

/// <summary>An idle-only disposable-cache reset. Session history is never deleted.</summary>
public sealed record ContextCacheResetRequest(string Epoch);

public sealed record ContextCacheResetResult(string Epoch, bool Changed, string RuntimeStatus);

public sealed record ProjectContextSummarizeRequest(string ProjectKey, string ModelId, string Role,
    string PreviousText, IReadOnlyList<string> Contributions, int MaximumTokens = 4096);

public sealed record ProjectContextSummarizeResult(string ProjectKey, string Text, bool Interrupted = false);

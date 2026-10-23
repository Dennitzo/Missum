namespace Missum.Ai.Contracts;

/// <summary>Summarizes stored inference context without starting another user task.</summary>
public sealed record SessionContextCompactionRequest(
    string SessionId,
    string? PreferredModelId = null,
    int? MaximumContextTokens = null);

public sealed record SessionContextCompactionResponse(
    string? SourceRunId,
    bool Changed,
    int BeforeTokenEstimate,
    int AfterTokenEstimate,
    long CompactionCount,
    string Detail);

namespace Missum.Ai.Server.Core.Runs;

// Serialized read compatibility for checkpoints created before publication-only projects.
public sealed record ScientificStateRunProgress(int SourcesSinceProgress = 0, long Revision = 0,
    long PublicationRevision = 0, string Target = "foundation", string? LastStagnationHint = null,
    IReadOnlyList<ScientificStateToolFailure>? Failures = null, long ExecutionEnvironmentRevision = 0,
    string? PrematureCompletionFingerprint = null, int PrematureCompletionAttempts = 0,
    bool CompletionMethodChangeHinted = false, IReadOnlyList<string>? CompletionEvidenceFingerprints = null,
    int NoChangeUpdateAttempts = 0, string? LastNoChangeUpdateFingerprint = null);
public sealed record ScientificStateToolFailure(string Fingerprint, int Count, string? LastHint = null,
    string? Tool = null, string? ArgumentHash = null, string? Cause = null, string? LastError = null,
    bool PrerequisiteBlocked = false, int BlockedAttempts = 0, int FeedbackCount = 0,
    long EnvironmentRevision = 0);


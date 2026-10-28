namespace Missum.Ai.Contracts;

/// <summary>Cancels an accepted or not-yet-accepted request without creating a run.</summary>
public sealed record RunCancelByKeyRequest(string SessionId, string IdempotencyKey);

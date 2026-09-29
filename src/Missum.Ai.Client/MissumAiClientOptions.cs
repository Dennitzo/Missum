namespace Missum.Ai.Client;

public sealed record MissumAiClientOptions(
    Uri ServerUri,
    string? ClientId = null,
    TimeSpan? RequestTimeout = null);

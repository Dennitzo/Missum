namespace Missum.Ai.Contracts;

public sealed record RunModelSelectionRequest(string SessionId, string ModelId, string? ReasoningEffort = null);
public sealed record RunModelSelectionApplied(string ModelId, string Role, string? ReasoningEffort, string Message);
public static class RunModelSelectionEvents
{
    public const string Requested = "run.selection.requested";
    public const string Failed = "run.selection.failed";
    public const string Applied = "run.selection.applied";
}

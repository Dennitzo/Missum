namespace Missum.Ai.Contracts;

/// <summary>Explicit, opt-in post-run agent; never an automatic parallel-model fallback.</summary>
public sealed record SpecializedAgentOptions(string ProfileId, string? Goal = null);
public sealed record SpecializedAgentProfileDescriptor(string Id, string DisplayName, string Description);

public static class SpecializedAgentProfiles
{
    public const string Loop = "loop";
    public static IReadOnlyList<SpecializedAgentProfileDescriptor> BuiltIn { get; } =
    [new(Loop, "Loop-Agent", "Ziel nach jedem Arbeitsabschnitt prüfen und offene Arbeit automatisch fortsetzen")];
}

public sealed record SpecializedAgentCheck(string Point, bool Completed, string Evidence);

/// <summary>The model owns the substantive judgment. An absent/invalid decision is never success.</summary>
public sealed record SpecializedAgentDecision(string Outcome, string Summary,
    IReadOnlyList<SpecializedAgentCheck> Checks, IReadOnlyList<string> OpenPoints, string? NextPrompt = null);

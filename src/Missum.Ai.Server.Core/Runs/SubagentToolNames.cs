namespace Missum.Ai.Server.Core.Runs;

public static class SubagentToolNames
{
    public const string Spawn = "subagent.spawn";
    public const string Wait = "subagent.wait";
    public static IReadOnlyList<string> All { get; } = [Spawn, Wait];
    internal const string ResultConsumedEvent = "subagent.resultConsumed";
    internal const string ResultContextMarker = "MISSUM_SUBAGENT_RESULTS\n";
}

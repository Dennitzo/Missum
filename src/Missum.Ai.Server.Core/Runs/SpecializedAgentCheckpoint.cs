using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>One explicit profile owns a durable serial phase of the outer run.</summary>
public sealed record SpecializedAgentCheckpoint(string ProfileId, string Version, string OriginalGoal, string AgentId,
    long Cycle = 1, string Phase = "main", string? InvocationId = null, string? LastMainContent = null,
    SpecializedAgentDecision? Decision = null, string? FollowUpInputId = null, long LastEventSequence = 0,
    long ReviewedSteeringSequence = 0, int ReviewAttempts = 0);

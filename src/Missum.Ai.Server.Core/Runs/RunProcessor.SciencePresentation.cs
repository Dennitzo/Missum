using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    // Individual literature/compute stages keep their own progress guards.
    // The complete research project must not exhaust a small chat-turn reserve.
    private static bool HasUnboundedResearchBudget(RunRequest request) =>
        request.Mode != RunMode.Coding && request.DeepResearch;
}

using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal static bool IsDeepResearchInternalToolEvent(RunEvent item, IEnumerable<SubagentChatState> activities)
    {
        if (item.Type is not (RunEventTypes.ServerToolStarted or RunEventTypes.ServerToolCompleted)
            || StringProperty(item.Data, "tool") is not ("web.search" or "web.fetch")) return false;

        // New gateways identify ownership on the durable envelope, including
        // reconnects that begin between a tool dispatch and its result.
        if (!string.IsNullOrWhiteSpace(StringProperty(item.Data, "parentOperationId"))) return true;

        // An already running gateway can outlive this client update. Its
        // sequential research operation still has a durable active tab whose
        // parent run identifies the old, untagged search/fetch envelopes.
        return activities.Any(activity => activity.IsDeepResearch && activity.ParentRunId == item.RunId
            && activity.IsRunning && activity.LastEventId < item.Id);
    }
}

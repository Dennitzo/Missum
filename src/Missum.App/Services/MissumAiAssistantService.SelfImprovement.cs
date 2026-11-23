using System.Text.Json.Nodes;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    /// <summary>
    /// Records what the restarted app must find again: the local run with its event checkpoint,
    /// the visible assistant message and the still executing apply proposal. The values live in the
    /// app database too; the copy in operation.json lets the broker report the real state if resume fails.
    /// </summary>
    internal async Task<JsonNode?> CaptureSelfImprovementResumeAsync(Guid sessionId, string serverRunId, CancellationToken cancellationToken)
    {
        var run = await runs.GetByServerRunIdAsync(serverRunId, cancellationToken).ConfigureAwait(false);
        if (run is null || run.SessionId != sessionId) return null;
        var message = await chats.GetMessageAsync(run.AssistantMessageId, cancellationToken).ConfigureAwait(false);
        var pending = await toolExecutions.ListIncompleteExecutionsAsync(run.Id, serverRunId, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["localRunId"] = run.Id.ToString("D"),
            ["serverRunId"] = serverRunId,
            ["lastEventId"] = run.LastEventId,
            ["runState"] = run.State,
            ["assistantMessageId"] = run.AssistantMessageId.ToString("D"),
            ["messageStatus"] = message?.Status.ToString(),
            ["messageLength"] = message?.Content.Length ?? 0,
            ["toolSteps"] = message?.ToolSteps?.Count ?? 0,
            ["pendingToolCalls"] = new JsonArray(pending.Select(item => (JsonNode?)new JsonObject
            {
                ["proposalId"] = item.ProposalId, ["toolName"] = item.ToolName, ["eventId"] = item.EventId, ["state"] = item.State,
            }).ToArray()),
        };
    }
}

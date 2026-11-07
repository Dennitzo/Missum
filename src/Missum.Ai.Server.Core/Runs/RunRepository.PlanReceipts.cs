using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunRepository
{
    // Repair the historical transport omission from the authentic operation journal.
    // This never executes a tool or infers success from the current plan.
    private async Task<AgentRunCheckpoint> RestoreMissingPlanReceiptsAsync(string runId,
        AgentRunCheckpoint checkpoint, CancellationToken token)
    {
        var acknowledged = checkpoint.Messages.Where(message => message.Role == "tool")
            .Select(message => message.ToolCallId).ToHashSet(StringComparer.Ordinal);
        var pending = (checkpoint.ActiveToolCalls ?? []).Skip(checkpoint.NextToolIndex)
            .Select(call => call.Id).ToHashSet(StringComparer.Ordinal);
        var calls = checkpoint.Messages.SelectMany(message => message.ToolCalls ?? [])
            .Where(call => call.Name == CodingWorkingStateTools.PlanTool && !acknowledged.Contains(call.Id) && !pending.Contains(call.Id)).ToArray();
        if (calls.Length == 0) return checkpoint;
        var journal = await ReadEventsByTypesAsync(runId, 0,
            [RunEventTypes.ServerToolStarted, RunEventTypes.ServerToolCompleted], token).ConfigureAwait(false);
        var starts = journal.Where(item => item.Type == RunEventTypes.ServerToolStarted
            && item.Data.TryGetProperty("tool", out var tool) && tool.GetString() == CodingWorkingStateTools.PlanTool).ToArray();
        var used = new HashSet<long>();
        var restored = new Dictionary<string, LmChatMessage>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            var matches = starts.Where(start => start.Data.TryGetProperty("arguments", out var arguments)
                && JsonElement.DeepEquals(arguments, call.Arguments)).ToArray();
            // Never guess between repeated identical historical operations.
            if (matches.Length != 1) continue;
            foreach (var start in matches)
            {
                if (used.Contains(start.Id) || !start.Data.TryGetProperty("arguments", out var arguments)
                    || !JsonElement.DeepEquals(arguments, call.Arguments)
                    || !start.Data.TryGetProperty("toolCallId", out var operation)) continue;
                var completed = journal.FirstOrDefault(item => item.Id > start.Id
                    && item.Type == RunEventTypes.ServerToolCompleted
                    && item.Data.TryGetProperty("toolCallId", out var id) && id.GetString() == operation.GetString());
                if (completed is null || !completed.Data.TryGetProperty("result", out var result)) continue;
                restored[call.Id] = new("tool", result.GetRawText(), ToolCallId: call.Id);
                used.Add(start.Id);
                break;
            }
        }
        if (restored.Count == 0) return checkpoint;
        var messages = new List<LmChatMessage>();
        foreach (var message in checkpoint.Messages)
        {
            messages.Add(message);
            foreach (var call in message.ToolCalls ?? [])
                if (restored.TryGetValue(call.Id, out var receipt)) messages.Add(receipt);
        }
        return checkpoint with { Messages = messages, PreserveSessionPromptPrefix = false };
    }
}

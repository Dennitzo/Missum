using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

internal static class ComputerPromptProjection
{
    // Each computer frame authorizes just one action and old screenshots cannot
    // be used again. Drop completed old observe exchanges from the active prompt,
    // while keeping actions and their outcomes. The complete event journal is untouched.
    internal static IReadOnlyList<LmChatMessage> KeepLatestObservation(IReadOnlyList<LmChatMessage> messages, string runId)
    {
        var observations = messages.Select((message, index) => (message, index))
            .Where(item => item.message.ComputerObservationId is not null
                && item.message.ComputerObservationRunId == runId)
            .Select(item => item.index).ToArray();
        if (observations.Length < 2) return messages;
        var remove = new HashSet<int>();
        foreach (var index in observations[..^1])
        {
            if (index < 2) continue;
            var receipt = messages[index - 1];
            var assistant = messages[index - 2];
            if (receipt.Role != "tool" || receipt.ToolCallId is null || assistant.Role != "assistant"
                || assistant.ToolCalls is not { Count: 1 } calls || calls[0].Name != "computer.observe"
                || calls[0].Id != receipt.ToolCallId) continue;
            remove.Add(index - 2); remove.Add(index - 1); remove.Add(index);
        }
        return remove.Count == 0 ? messages : messages.Where((_, index) => !remove.Contains(index)).ToArray();
    }
}

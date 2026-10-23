using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Projects chronological output; only the unfinished tail can show a thinking placeholder.</summary>
internal static class NativeActivityProjection
{
    internal sealed record Entry(string Key, JsonElement Step, int? Offset, int? EndOffset = null, string? ReplacesKey = null);
    private static readonly JsonElement Active = JsonSerializer.SerializeToElement(new { tool = "assistant.activity", status = "running" });
    private static readonly JsonElement Answer = JsonSerializer.SerializeToElement(new { tool = "assistant.answer" });
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    internal static IReadOnlyList<Entry> Build(IEnumerable<JsonElement> steps, bool active, int contentLength, bool waitingForModel = false)
    {
        var result = new List<Entry>();
        var slot = "activity:start";
        var cursor = 0;
        var thinking = false;
        var thinkingOffset = 0;
        var completedTool = false;
        void AnswerUntil(int end)
        {
            end = Math.Clamp(end, cursor, contentLength);
            if (end == cursor) return;
            var start = cursor;
            var replaced = thinking && thinkingOffset > start ? "activity:after:text:" + start : slot;
            result.Add(new("source:" + start, Answer, start, end, replaced));
            slot = "activity:after:text:" + start;
            cursor = end;
            thinking = completedTool = false;
        }
        foreach (var step in steps)
        {
            var tool = Text(step, "tool");
            var id = Text(step, "id");
            var offset = step.TryGetProperty("contentOffset", out var position) && position.ValueKind == JsonValueKind.Number
                && position.TryGetInt32(out var value) && value >= 0 ? Math.Clamp(value, cursor, contentLength) : cursor;
            if (tool is "assistant.reasoning" or "assistant.progress")
            {
                thinking = true;
                thinkingOffset = offset;
                completedTool = false;
                continue;
            }
            AnswerUntil(offset);
            if (tool == "assistant.steering")
            {
                result.Add(new("tool:" + id, step, offset));
                continue;
            }
            // A visible AI message consumes the placeholder just like a tool.
            if (tool == "assistant.narration" && string.IsNullOrWhiteSpace(Text(step, "detail"))) continue;
            result.Add(new((tool == "assistant.narration" ? "narration:" : "tool:") + id, step, offset, ReplacesKey: slot));
            slot = "activity:after:" + id;
            thinking = false;
            completedTool = tool != "assistant.narration" && Text(step, "status") is not ("running" or "pending");
        }
        var thinkingAtTail = thinking && thinkingOffset >= contentLength;
        var completedAtTail = completedTool && cursor >= contentLength;
        AnswerUntil(contentLength);
        // A durable running compaction already describes the current work.
        // Do not append a second transient thinking row for its model phase.
        var compactingAtTail = result.LastOrDefault() is { } last
            && Text(last.Step, "tool") == "context.compact"
            && Text(last.Step, "status") is "running" or "pending";
        // No completed/historical thinking rows, and never a placeholder before an answer.
        if (active && !compactingAtTail && (waitingForModel || thinkingAtTail || completedAtTail || result.Count == 0)) result.Add(new(slot, Active, cursor));
        return result;
    }
}

using System.Text.Json;

namespace Missum.App.Services;

/// <summary>One source-anchored slot survives prefill, reasoning and the following tool.</summary>
internal static class NativeActivityProjection
{
    internal sealed record Entry(string Key, JsonElement Step, int? Offset);
    private static readonly JsonElement Active = JsonSerializer.SerializeToElement(new { tool = "assistant.activity", status = "running" });
    private static readonly JsonElement Inactive = JsonSerializer.SerializeToElement(new { tool = "assistant.activity", status = "completed" });
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
    internal static IReadOnlyList<Entry> Build(IEnumerable<JsonElement> steps, bool active, int contentLength)
    {
        var result = new List<Entry>();
        var slot = "activity:start";
        int? anchor = 0;
        var pending = -1;
        var lastWasTool = false;
        foreach (var step in steps)
        {
            var tool = Text(step, "tool");
            var id = Text(step, "id");
            int? offset = step.TryGetProperty("contentOffset", out var position) && position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out var value) && value >= 0 ? value : null;
            if (tool is "assistant.reasoning" or "assistant.progress" or "context.compact")
            {
                if (pending < 0)
                {
                    pending = result.Count;
                    result.Add(new(slot, active ? Active : Inactive, anchor ?? offset));
                }
                lastWasTool = false;
                continue;
            }
            if (tool is "assistant.narration" or "assistant.steering")
            {
                result.Add(new("tool:" + id, step, offset));
                continue;
            }
            var entry = new Entry(slot, step, pending >= 0 ? result[pending].Offset : offset);
            if (pending >= 0) result[pending] = entry;
            else result.Add(entry);
            pending = -1;
            slot = "activity:after:" + id;
            anchor = offset ?? anchor ?? 0;
            lastWasTool = Text(step, "status") is not ("running" or "pending");
        }
        // Keep the terminal slot after completion too; finishing must not collapse the chat.
        if (pending < 0 && (lastWasTool || result.Count == 0))
            result.Add(new(slot, active ? Active : Inactive, anchor ?? 0));
        return result;
    }
}

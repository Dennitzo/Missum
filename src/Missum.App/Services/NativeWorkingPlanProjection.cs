using System.Text.Json;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>One compact view of the saved plan, shared by the composer and tool details.</summary>
internal static class NativeWorkingPlanProjection
{
    internal sealed record Item(string Id, string Title, string Status);
    internal sealed record Snapshot(IReadOnlyList<Item> Steps, IReadOnlyList<Item> AcceptanceCriteria)
    {
        internal static Snapshot Empty { get; } = new([], []);
    }

    internal static Snapshot FromToolStep(AssistantToolStep? step)
    {
        if (step?.Tool != "coding.updatePlan") return Snapshot.Empty;
        return Read(Parse(step.OutputJson), Parse(step.InputJson));
    }

    internal static Snapshot Read(JsonElement output, JsonElement input = default) => new(
        ReadItems(FirstArray(output, "storedSteps", "steps", "plan"), Property(input, "steps")),
        ReadItems(FirstArray(output, "storedAcceptanceCriteria", "acceptanceCriteria"), Property(input, "acceptanceCriteria")));

    private static List<Item> ReadItems(JsonElement primary, JsonElement fallback)
    {
        // An explicitly empty canonical array clears a list. Never replace it with a stale request.
        var items = primary.ValueKind == JsonValueKind.Array ? primary : fallback;
        if (items.ValueKind != JsonValueKind.Array) return [];
        var result = new List<Item>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var id = Text(item, "id");
            var title = Text(item, "title");
            if (string.IsNullOrWhiteSpace(title)) title = id;
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (id.Length == 0) id = title;
            if (!ids.Add(id)) continue;
            var status = Text(item, "status");
            result.Add(new(id, title, status is "completed" or "complete" ? "completed" : status == "in_progress" ? status : "pending"));
        }
        return result;
    }

    private static JsonElement FirstArray(JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (Property(value, name) is { ValueKind: JsonValueKind.Array } array) return array;
        return default;
    }

    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var found) ? found : default;
    private static string Text(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.String } text ? text.GetString() ?? "" : "";
    private static JsonElement Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch (JsonException) { return default; }
    }
}

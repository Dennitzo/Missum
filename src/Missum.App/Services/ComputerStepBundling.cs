using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>
/// Folds a run of Computer-Use steps (observe/act and the short model comments
/// between them) into one chat receipt. The individual steps stay durable; only
/// the chat projection shows them as a single expandable "Computersteuerung" row.
/// </summary>
internal static class ComputerStepBundling
{
    internal const string GroupTool = "computer.session";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static bool IsComputerStep(string tool) => tool is "computer.observe" or "computer.act";

    /// <summary>Content offset after the last bundled step; text before it belongs to the group.</summary>
    internal static int? ContentEnd(JsonElement step) =>
        step.ValueKind == JsonValueKind.Object && step.TryGetProperty("contentEnd", out var end)
            && end.ValueKind == JsonValueKind.Number && end.TryGetInt32(out var value) ? value : null;

    internal static IEnumerable<JsonElement> Bundle(IEnumerable<JsonElement> steps, string content)
    {
        var run = new List<JsonElement>();
        var comments = new List<string>();
        foreach (var step in steps)
        {
            var tool = Text(step, "tool");
            if (IsComputerStep(tool))
            {
                run.Add(step);
                continue;
            }
            // Thinking rows and narration between two computer steps belong to the run.
            if (run.Count > 0 && tool is "assistant.reasoning" or "assistant.progress" or "assistant.narration")
            {
                if (tool == "assistant.narration" && Text(step, "detail") is { Length: > 0 } narration) comments.Add(narration);
                continue;
            }
            if (run.Count > 0)
            {
                yield return Group(run, comments, content);
                run.Clear();
                comments.Clear();
            }
            yield return step;
        }
        if (run.Count > 0) yield return Group(run, comments, content);
    }

    private static JsonElement Group(List<JsonElement> run, List<string> narration, string content)
    {
        var offsets = run.Select(step => Offset(step, content.Length)).ToArray();
        var start = offsets[0] ?? 0;
        var end = start;
        var detail = new StringBuilder();
        for (var index = 0; index < run.Count; index++)
        {
            var step = run[index];
            detail.Append(CultureInfo.InvariantCulture, $"{index + 1}. **{Describe(step)}**");
            if (Text(step, "status") is "failed" or "denied" or "cancelled" or "interrupted") detail.Append(" · fehlgeschlagen");
            detail.AppendLine();
            if (offsets[index] is { } from) end = Math.Max(end, from);
            // The model's comment written after this step, before the next one.
            if (index + 1 < run.Count && offsets[index] is { } commentStart && offsets[index + 1] is { } commentEnd && commentEnd > commentStart)
            {
                var comment = content[commentStart..commentEnd].Trim();
                if (comment.Length > 0) detail.AppendLine("   " + OneLine(comment, 400));
            }
        }
        foreach (var comment in narration) detail.AppendLine("   " + OneLine(comment, 400));
        var running = run.Any(step => Text(step, "status") is "running" or "pending");
        var status = running ? "running" : Text(run[^1], "status") is { Length: > 0 } last ? last : "completed";
        var count = run.Count;
        return JsonSerializer.SerializeToElement(new
        {
            id = "computer-group:" + Text(run[0], "id"),
            tool = GroupTool,
            status,
            label = count == 1 ? "Computersteuerung · 1 Schritt" : $"Computersteuerung · {count} Schritte",
            detail = detail.ToString().TrimEnd(),
            contentOffset = start,
            contentEnd = end,
            createdAt = Text(run[0], "createdAt"),
            updatedAt = Text(run[^1], "updatedAt"),
        }, Json);
    }

    private static string Describe(JsonElement step)
    {
        var input = Metadata(Text(step, "inputJson"));
        if (Text(step, "tool") == "computer.observe")
        {
            var output = Metadata(Text(step, "outputJson"));
            var title = Text(output, "foregroundTitle");
            return title.Length > 0 ? "Bildschirm beobachtet · " + OneLine(title, 80) : "Bildschirm beobachtet";
        }
        var kind = Text(input, "kind");
        var target = Text(input, "elementIndex") is { Length: > 0 } element ? " · Element " + element
            : Text(input, "target") is { Length: > 0 } program ? " · " + OneLine(program, 80)
            : Text(input, "x") is { Length: > 0 } x && Text(input, "y") is { Length: > 0 } y ? $" · {x}, {y}" : "";
        var extra = Text(input, "key") is { Length: > 0 } key ? " · " + key
            : Text(input, "text") is { Length: > 0 } text ? " · „" + OneLine(text, 60) + "“" : "";
        return (kind switch
        {
            "click" => "Klick", "double_click" => "Doppelklick", "right_click" => "Rechtsklick", "middle_click" => "Mittelklick",
            "drag" => "Ziehen", "scroll" => "Scrollen", "type" => "Text eingeben", "set_value" => "Wert setzen",
            "keypress" => "Taste", "focus" => "Fenster wechseln", "window" => "Fenster", "launch" => "Programm starten",
            "wait" => "Warten", "move" => "Maus bewegen", "" => "Aktion", _ => kind,
        }) + target + extra;
    }

    private static int? Offset(JsonElement step, int length) =>
        step.TryGetProperty("contentOffset", out var position) && position.ValueKind == JsonValueKind.Number
            && position.TryGetInt32(out var value) && value >= 0 ? Math.Min(value, length) : null;

    private static JsonElement Metadata(string json)
    {
        if (json.Length == 0) return default;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
            ? field.ValueKind switch
            {
                JsonValueKind.String => field.GetString() ?? "",
                JsonValueKind.Number => field.GetRawText(),
                _ => "",
            }
            : "";

    private static string OneLine(string value, int maximum)
    {
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= maximum ? line : line[..(maximum - 1)] + "…";
    }
}

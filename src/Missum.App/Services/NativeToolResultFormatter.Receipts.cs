using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.App.Services;

internal static partial class NativeToolResultFormatter
{
    // This is a display projection only. Durable receipts and the model's tool
    // results retain all evidence; closed details still load on demand.
    internal static IEnumerable<Block> Receipt(string tool, JsonElement input, JsonElement output,
        string status, string path = "", string diff = "", string detail = "")
    {
        path = First(Text(output, "path"), Text(output, "file"), Text(input, "path"), path);
        var language = First(System.IO.Path.GetExtension(path).TrimStart('.'), "text");
        var failed = status is "failed" or "denied" or "cancelled" or "interrupted"
            || Property(output, "success").ValueKind == JsonValueKind.False;
        if (tool == "math.formalProof")
        {
            var lean = Property(output, "lean");
            if (lean.ValueKind != JsonValueKind.Object) lean = output;
            var accepted = !failed && First(Text(output, "formalVerification"), Text(lean, "formalVerification")) == "KernelAccepted";
            var state = accepted ? "Erfolgreich" : failed || status == "completed" ? "Fehlgeschlagen / nicht bestätigt" : "Prüfung läuft";
            var source = First(Text(input, "source"), Text(lean, "sourceText"));
            yield return new("Lean-Beweis · " + state, source.Length > 0 ? source : "Beweisquelltext ist in diesem Beleg nicht enthalten.", "lean",
                First(path, Text(lean, "source")));
            if (!accepted)
                foreach (var block in Essentials(lean, errorsOnly: true)) yield return block;
            if (!accepted && lean.Equals(output) == false)
                foreach (var block in Essentials(output, errorsOnly: true, descend: false)) yield return block;
            yield break;
        }
        if (failed && tool is "coding.read" or "coding.write" or "coding.edit" or "coding.undo" or "coding.gitDiff" or "research.update" or "coding.list" or "files.list" or "research.code.write")
            foreach (var block in Essentials(output, errorsOnly: true)) yield return block;
        if (tool == "coding.read")
        {
            var content = Text(output, "content");
            var start = int.TryParse(First(Text(output, "startLine"), Text(input, "startLine")), out var line) ? line : 1;
            var lines = content.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
            if (Text(output, "contentFormat") != "source" && content.Length > 0
                && lines.Select((text, index) => text.StartsWith((start + index) + ": ", StringComparison.Ordinal)).All(value => value))
                content = string.Join("\n", lines.Select((text, index) => text[((start + index).ToString(System.Globalization.CultureInfo.InvariantCulture).Length + 2)..]))
                    + (content.EndsWith('\n') ? "\n" : "");
            yield return Code("Gelesen", content, language, path, start);
        }
        else if (tool is "coding.write" or "coding.edit" or "coding.undo" or "coding.gitDiff")
        {
            diff = First(diff, Text(output, "diff"), Text(Property(output, "diff"), "stdout"));
            if (diff.Length > 0)
            {
                var ranges = Regex.Matches(diff, @"(?m)^@@[^\r\n]*@@").Select(match => match.Value);
                yield return new((failed ? "Nicht angewendete Änderung" : status == "completed" ? "Änderung" : "Vorbereitete Änderung")
                    + " · Zeilen " + string.Join("; ", ranges), diff, "diff", path);
            }
            else if (Text(input, "content") is { Length: > 0 } content)
                yield return Code(failed ? "Nicht gespeicherter Inhalt" : "Dateiinhalt", content, language, path, 1);
            else
            {
                var edits = Property(input, "edits");
                var items = edits.ValueKind == JsonValueKind.Array ? edits.EnumerateArray().ToArray() : [input];
                foreach (var edit in items)
                {
                    if (Text(edit, "oldText") is { Length: > 0 } old) yield return new("Vorher · Zeilenposition nicht übermittelt", old, language, path);
                    if (Text(edit, "newText") is { Length: > 0 } next) yield return new("Nachher · Zeilenposition nicht übermittelt", next, language, path);
                }
            }
        }
        else if (tool == "research.update")
        {
            foreach (var change in Array(input, "changes"))
            {
                var data = Property(change, "data");
                var title = First(Text(data, "title"), Text(change, "id"), "Forschungsstand");
                var text = First(Text(data, "contentMarkdown"), Text(data, "statement"), Text(data, "description"));
                if (text.Length > 0) yield return new(title + (failed ? " · nicht übernommen" : ""), text, "markdown");
                else foreach (var block in Essentials(data)) yield return block with { Title = title + " · " + block.Title };
            }
        }
        else if (tool is "coding.list" or "files.list")
        {
            var names = Array(output, "entries").Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()!
                : First(Text(item, "path"), Text(item, "name"))).Where(name => name.Length > 0);
            yield return new("Dateien und Ordner", string.Join("\n", names), "text", path);
        }
        else if (tool == "research.code.write")
            yield return Code(failed ? "Nicht gespeicherter Python-Code" : "Python-Code", First(Text(input, "code"), Text(input, "content")), "python", path, 1);
        else if (tool == "coding.updatePlan")
        {
            foreach (var key in new[] { "steps", "acceptanceCriteria" })
            {
                var items = Array(input, key).Select(item => First(Text(item, "title"), Text(item, "id"))
                    + (Text(item, "status") is { Length: > 0 } state ? " · " + state : ""));
                var text = string.Join("\n", items);
                if (text.Length > 0) yield return new(key == "steps" ? "Arbeitsschritte" : "Abnahmekriterien", text, "text");
            }
            if (failed) foreach (var block in Essentials(output, errorsOnly: true)) yield return block;
        }
        else
        {
            if (tool == "coding.command")
            {
                var command = First(Text(input, "command"), (Text(input, "executable") + " "
                    + string.Join(" ", Array(input, "arguments").Select(Scalar))).Trim());
                if (command.Length > 0) yield return new("Befehl", command, "shell");
            }
            using var blocks = Essentials(output).GetEnumerator();
            if (blocks.MoveNext()) { do { yield return blocks.Current; } while (blocks.MoveNext()); }
            else
            {
                var text = First(Text(input, "command"), Text(input, "query"), Text(input, "task"), Text(input, "url"), detail);
                // Do not fall back to dumping the raw machine receipt as JSON.
                if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('[')) text = "";
                yield return new("Ergebnis", text.Length > 0 ? text : failed ? "Werkzeug fehlgeschlagen." : "Keine weitere Textausgabe.", "text");
            }
        }
        if (Property(output, "truncated").ValueKind == JsonValueKind.True || Property(output, "diffTruncated").ValueKind == JsonValueKind.True)
            yield return new("Hinweis", "Die Werkzeugausgabe ist gekürzt.", "text");
    }

    private static Block Code(string title, string text, string language, string path, int start)
    {
        var count = text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
        return new(title + (count > 0 ? $" · Zeilen {start}–{start + count - 1}" : " · keine Zeilen"), text, language, path);
    }

    private static IEnumerable<Block> Essentials(JsonElement value, bool errorsOnly = false, bool descend = true, int depth = 0)
    {
        if (depth > 6) yield break;
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) yield return new("Ergebnis", item.GetString()!, "text");
                else foreach (var block in Essentials(item, errorsOnly, true, depth + 1)) yield return block;
            yield break;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in errorsOnly ? new[] { "error", "message", "diagnostics", "stderr", "standardError", "failures" }
            : new[] { "content", "contentMarkdown", "text", "summary", "result", "stdout", "standardOutput", "stderr", "standardError", "error", "message", "diagnostics", "snippet", "statement", "failures" })
        {
            var item = Property(value, key);
            if (item.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in item.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.String) yield return new(Label(key), entry.GetString()!, "text");
                    else foreach (var block in Essentials(entry, errorsOnly, true, depth + 1)) yield return block;
            }
            else if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && seen.Add(text))
            {
                if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                {
                    JsonElement parsed;
                    try { parsed = JsonSerializer.Deserialize<JsonElement>(text); }
                    catch (JsonException) { parsed = default; }
                    if (parsed.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    { foreach (var block in Essentials(parsed, errorsOnly, true, depth + 1)) yield return block; continue; }
                }
                yield return new(First(Text(value, "title"), Text(value, "path"), Label(key)), text, key == "contentMarkdown" ? "markdown" : "text");
            }
        }
        if (!descend) yield break;
        foreach (var key in new[] { "results", "matches", "runs", "verification", "data", "result", "sections", "claims", "sources", "items" })
        {
            var item = Property(value, key);
            if (item.ValueKind == JsonValueKind.Object)
                foreach (var block in Essentials(item, errorsOnly, true, depth + 1)) yield return block;
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var entry in item.EnumerateArray())
                    foreach (var block in Essentials(entry, errorsOnly, true, depth + 1)) yield return block;
        }
    }
    private static JsonElement Property(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static string Text(JsonElement value, string key) => Property(value, key) is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } ? "" : Scalar(Property(value, key));
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static IEnumerable<JsonElement> Array(JsonElement value, string key)
    {
        if (Property(value, key) is { ValueKind: JsonValueKind.Array } array)
            foreach (var item in array.EnumerateArray()) yield return item;
    }
}

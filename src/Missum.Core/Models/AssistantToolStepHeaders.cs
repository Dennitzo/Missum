using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.Core.Models;

/// <summary>Small display-only facts. Large tool inputs, outputs and code remain independently deferred.</summary>
public sealed record ToolStepDisplayHeader(int Version = 1, string Summary = "", string FilePath = "",
    long? AddedLines = null, long? RemovedLines = null);

public static class AssistantToolStepHeaders
{
    public const int CurrentVersion = 1;
    public const int MaximumSummaryCharacters = 320;
    public const int MaximumFilePathCharacters = 1024;
    private static readonly ConditionalWeakTable<AssistantToolStep, ToolStepDisplayHeader> Headers = new();

    /// <summary>Projects current complete data without modifying the durable step or its model-context payload.</summary>
    public static ToolStepDisplayHeader Create(AssistantToolStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.DetailsDeferred && step.DisplayHeader is { Version: CurrentVersion } saved) return saved;
        // Repeated display ticks retain the same immutable receipt objects. A new
        // streaming record gets its own projection, without retaining old payloads.
        return Headers.GetValue(step, CreateComplete);
    }

    private static ToolStepDisplayHeader CreateComplete(AssistantToolStep step)
    {
        if (step.Tool is "assistant.reasoning" or "assistant.narration" or "assistant.progress")
            return new(CurrentVersion);
        using var inputDocument = ReadObject(step.InputJson);
        using var outputDocument = ReadObject(step.OutputJson);
        var input = inputDocument?.RootElement ?? default;
        var output = outputDocument?.RootElement ?? default;
        var detail = step.Detail ?? step.Explanation ?? "";
        var diff = ReadDiff(detail, output);
        var path = ReadDetailPath(detail);
        if (path.Length == 0) path = Text(output, "path", Text(output, "file"));
        if (path.Length == 0) path = Text(input, "path");
        if (path.Length == 0 && diff.Length > 0) path = DiffPath(diff);

        (long Added, long Removed)? counts = null;
        if (step.Tool is "coding.write" or "coding.edit" or "coding.undo"
            && diff.Length > 0 && !Flag(output, "diffTruncated") && !Flag(output, "isBinary"))
            counts = UnifiedDiffCounts(diff);
        return new(CurrentVersion, Summary(step.Tool, input, output, path),
            OneLine(path, MaximumFilePathCharacters), counts?.Added, counts?.Removed);
    }

    public static string Summary(string tool, JsonElement input, JsonElement output, string? fallbackPath = null) =>
        OneLine(BuildSummary(tool, input, output, fallbackPath), MaximumSummaryCharacters);

    private static string BuildSummary(string tool, JsonElement input, JsonElement output, string? fallbackPath)
    {
        if (tool == "research.update" && Property(input, "changes") is { ValueKind: JsonValueKind.Array } changes)
        {
            var count = 0;
            var first = "";
            foreach (var change in changes.EnumerateArray())
            {
                var data = Property(change, "data");
                var title = data.ValueKind != JsonValueKind.Undefined
                    ? Text(data, "title", Text(change, "id")) : Text(change, "id");
                if (title.Length == 0) continue;
                if (count++ == 0) first = title;
            }
            return count == 1 ? OneLine(first, 100) : count.ToString(CultureInfo.CurrentCulture) + " Forschungsobjekte";
        }
        if (tool == "research.read") return "Hypothesen, offene Prüfungen und Publikationsstand";
        if (tool is "subagent" or "subagent.spawn" or "subagent.wait")
            return OneLine(Text(output, "title", Text(input, "title", Text(input, "task"))), 80);

        var path = Text(output, "path", Text(output, "file", Text(input, "path")));
        if (path.Length == 0) path = fallbackPath ?? "";
        var target = path.Length > 0 ? path.Replace('\\', '/') : Text(input, "query", Text(input, "url"));
        if (tool is "coding.command" or "research.code.execute" or "research.code.test" or "research.code.benchmark")
        {
            // Command-shaped receipts use either executable/arguments or a single command string.
            var executable = Text(input, "executable");
            var command = new StringBuilder(OneLine(executable, 200));
            if (Property(input, "arguments") is { ValueKind: JsonValueKind.Array } arguments)
                foreach (var argument in arguments.EnumerateArray())
                {
                    if (command.Length >= 200) { command.Append('…'); break; }
                    if (command.Length > 0) command.Append(' ');
                    command.Append(OneLine(Scalar(argument), 200 - Math.Min(command.Length, 200)));
                }
            target = command.Length > 0 ? command.ToString() : Text(input, "command");
        }
        var facts = new List<string>();
        if (target.Length > 0) facts.Add(OneLine(target, 200));
        if (tool == "coding.read" && Property(output, "content") is { ValueKind: JsonValueKind.String } content)
        {
            var count = CountLines(content.GetString() ?? "");
            var start = long.TryParse(Text(output, "startLine", Text(input, "startLine")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
                && first > 0 && first <= long.MaxValue - count ? first : 1;
            facts.Add($"{count:N0} Zeilen gelesen" + (count > 0 ? $" ({start:N0}–{start + count - 1:N0})" : ""));
        }
        foreach (var (key, label) in new[] { ("entries", "Einträge"), ("matches", "Treffer"), ("results", "Treffer"), ("sources", "Quellen") })
            if (Property(output, key) is { ValueKind: JsonValueKind.Array } array) facts.Add($"{array.GetArrayLength():N0} {label}");
        if (Text(output, "exitCode") is { Length: > 0 } exit) facts.Add("Exitcode " + OneLine(exit, 20));
        var error = Text(output, "error");
        if (error.Length == 0) error = Text(output, "errorCode");
        if (error.Length > 0) facts.Add(OneLine(error, 100));
        if (Flag(output, "truncated")) facts.Add("Ausgabe gekürzt");
        return string.Join(" · ", facts);
    }

    private static int CountLines(string value)
    {
        var count = 0;
        var endsWithNewline = false;
        for (var index = 0; index < value.Length; index++)
        {
            endsWithNewline = value[index] is '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029';
            if (!endsWithNewline) continue;
            count++;
            if (value[index] == '\r' && index + 1 < value.Length && value[index + 1] == '\n') index++;
        }
        return count + (value.Length > 0 && !endsWithNewline ? 1 : 0);
    }

    private static string ReadDiff(string detail, JsonElement output)
    {
        var matches = Regex.Matches(detail, @"```diff\s*\n(?<diff>[\s\S]*?)(?:```|\z)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        if (matches.Count > 0 && matches[^1].Groups["diff"].Value is { Length: > 0 } last) return last;
        var diff = Property(output, "diff");
        return diff.ValueKind == JsonValueKind.String ? diff.GetString() ?? "" : Text(diff, "stdout");
    }

    private static string ReadDetailPath(string detail)
    {
        using var lines = new StringReader(detail);
        while (lines.ReadLine() is { } line)
            if (line.StartsWith("Datei:", StringComparison.Ordinal)) return line[6..].Trim();
        return "";
    }

    private static string DiffPath(string diff)
    {
        foreach (var prefix in new[] { "+++ ", "--- " })
        {
            using var lines = new StringReader(diff);
            while (lines.ReadLine() is { } line)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var path = line[prefix.Length..].Split('\t', 2)[0].Trim('"');
                if (path == "/dev/null") continue;
                return path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
            }
        }
        return "";
    }

    private static (long Added, long Removed)? UnifiedDiffCounts(string diff)
    {
        long added = 0, removed = 0, oldRemaining = 0, newRemaining = 0;
        var hasHunk = false;
        using var lines = new StringReader(diff);
        while (lines.ReadLine() is { } line)
        {
            var hunk = Regex.Match(line, @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            if (hunk.Success)
            {
                if (oldRemaining != 0 || newRemaining != 0) return null;
                if (!long.TryParse(hunk.Groups[2].Success ? hunk.Groups[2].Value : "1", NumberStyles.None, CultureInfo.InvariantCulture, out oldRemaining)
                    || !long.TryParse(hunk.Groups[4].Success ? hunk.Groups[4].Value : "1", NumberStyles.None, CultureInfo.InvariantCulture, out newRemaining)) return null;
                hasHunk = true;
                continue;
            }
            if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) return null;
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal)) continue;
            if (oldRemaining == 0 && newRemaining == 0) continue;
            if (line.StartsWith('+')) { if (newRemaining == 0) return null; added++; newRemaining--; }
            else if (line.StartsWith('-')) { if (oldRemaining == 0) return null; removed++; oldRemaining--; }
            else if (line.StartsWith(' '))
            {
                if (oldRemaining == 0 || newRemaining == 0) return null;
                oldRemaining--; newRemaining--;
            }
            else return null;
        }
        return hasHunk && oldRemaining == 0 && newRemaining == 0 ? (added, removed) : null;
    }

    private static JsonDocument? ReadObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string Text(JsonElement value, string name, string fallback = "")
    {
        var property = Property(value, name);
        return property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : Scalar(property);
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
        _ => "",
    };

    private static bool Flag(JsonElement value, string name) => Property(value, name).ValueKind == JsonValueKind.True;

    private static string OneLine(string value, int maximum)
    {
        if (maximum < 1) return "";
        var result = new StringBuilder(Math.Min(value.Length, maximum));
        var space = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character)) { space = result.Length > 0; continue; }
            if (space) { result.Append(' '); space = false; }
            result.Append(character);
            if (result.Length <= maximum) continue;
            result.Length = maximum - 1;
            if (result.Length > 0 && char.IsHighSurrogate(result[^1])) result.Length--;
            result.Append('…');
            break;
        }
        return result.ToString();
    }
}

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.Ai.Server.Core.Models;

/// <summary>
/// Projects source receipts only at the model boundary. Persistence, evidence and
/// result validation continue to use the original structured JSON receipt.
/// </summary>
internal static class CodingSourceToolContent
{
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static IReadOnlyList<LmChatMessage> Project(IReadOnlyList<LmChatMessage> messages)
    {
        var tools = new Dictionary<string, string>(StringComparer.Ordinal);
        var projected = new List<LmChatMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message.ToolCalls is { } calls)
                foreach (var call in calls) tools[call.Id] = call.Name;
            projected.Add(message.Role == "tool" && message.ToolCallId is { } id
                && tools.TryGetValue(id, out var tool)
                && tool is ClientToolNames.CodingRead or ClientToolNames.CodingSearch
                && TryFormat(message.Content, tool, out var content)
                    ? message with { Content = content } : message);
        }
        return projected;
    }

    private static bool TryFormat(string? json, string tool, out string? content)
    {
        content = json;
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var outer = document.RootElement;
            if (outer.ValueKind != JsonValueKind.Object) return false;
            var wrapped = outer.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object;
            var result = wrapped ? nested : outer;
            var output = new StringBuilder(tool).Append('\n');
            if (tool == ClientToolNames.CodingRead)
            {
                if (!result.TryGetProperty("content", out var source) || source.ValueKind != JsonValueKind.String) return false;
                var sourceText = source.GetString()!;
                var format = result.TryGetProperty("contentFormat", out var formatProperty)
                    && formatProperty.ValueKind == JsonValueKind.String ? formatProperty.GetString() : null;
                if (format is null)
                {
                    // Historical reads carried display line numbers and normalized
                    // newlines. Never treat them as new, exact source receipts.
                    var start = result.TryGetProperty("startLine", out var startProperty)
                        && startProperty.ValueKind == JsonValueKind.Number
                        && startProperty.TryGetInt32(out var number) ? number : 1;
                    var legacy = RemoveLegacyLineNumbers(sourceText, start);
                    sourceText = legacy ?? sourceText;
                    format = legacy is null ? "legacy-display" : "legacy-normalized-source";
                }
                AppendMetadata(output, outer, result, wrapped, "content");
                output.Append("sourceFormat: ").Append(format).Append('\n');
                AppendSource(output, sourceText);
            }
            else
            {
                if (!result.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array) return false;
                if (matches.EnumerateArray().Any(match => match.ValueKind != JsonValueKind.Object
                    || !match.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)) return false;
                AppendMetadata(output, outer, result, wrapped, "matches");
                output.Append("Trefferzahl: ").Append(matches.GetArrayLength()).Append('\n');
                var index = 0;
                foreach (var match in matches.EnumerateArray())
                {
                    output.Append("Treffer ").Append(++index).Append('\n');
                    output.Append("Metadaten: ").Append(SerializeWithout(match, "text")).Append('\n');
                    AppendSource(output, match.GetProperty("text").GetString()!);
                }
            }
            content = output.ToString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AppendMetadata(StringBuilder output, JsonElement outer, JsonElement result, bool wrapped, string sourceField)
    {
        if (wrapped) output.Append("Werkzeugstatus: ").Append(SerializeWithout(outer, "result")).Append('\n');
        output.Append("Metadaten: ").Append(SerializeWithout(result, sourceField)).Append('\n');
    }

    private static string SerializeWithout(JsonElement source, string omitted)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject())
            if (property.Name != omitted) metadata[property.Name] = property.Value;
        return JsonSerializer.Serialize(metadata, MetadataJson);
    }

    internal static void AppendSource(StringBuilder output, string source, string label = "Originaltext")
    {
        // Delimiters remain outside the original source. No JSON string escaping,
        // trimming, Unicode normalization or escape-sequence interpretation occurs.
        var longestRun = 0;
        var run = 0;
        foreach (var character in source)
        {
            run = character == '`' ? run + 1 : 0;
            longestRun = Math.Max(longestRun, run);
        }
        var fence = new string('`', Math.Max(3, longestRun + 1));
        output.Append(label).Append(" (Zeichen: ").Append(source.Length).Append("):\n")
            .Append(fence).Append('\n').Append(source).Append('\n').Append(fence).Append('\n');
    }

    private static string? RemoveLegacyLineNumbers(string content, int startLine)
    {
        var output = new StringBuilder(content.Length);
        var cursor = 0;
        var line = startLine;
        while (cursor < content.Length)
        {
            var prefix = line.ToString(CultureInfo.InvariantCulture) + ": ";
            if (!content.AsSpan(cursor).StartsWith(prefix, StringComparison.Ordinal)) return null;
            cursor += prefix.Length;
            var end = content.IndexOf('\n', cursor);
            if (end < 0) { output.Append(content.AsSpan(cursor)); break; }
            output.Append(content.AsSpan(cursor, end - cursor + 1));
            cursor = end + 1;
            line++;
        }
        return output.ToString();
    }
}

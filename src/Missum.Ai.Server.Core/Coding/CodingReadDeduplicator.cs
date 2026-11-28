using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Coding;

/// <summary>
/// A repeated coding.read of the same file range whose result is byte-for-byte identical to an
/// earlier read that is still unchanged in the conversation adds nothing new for the model, but
/// costs a full prefill of the whole text at the current context depth. Such a repeat is answered
/// with a short reference to the earlier result instead. Equality of the complete raw result
/// proves both that the file did not change and that the earlier copy is still in context
/// (compaction rewrites old results, which then no longer match).
/// </summary>
internal static class CodingReadDeduplicator
{
    /// <summary>Short reads are cheaper to repeat than to reference.</summary>
    internal const int MinimumCharacters = 2_000;

    internal static string Apply(IReadOnlyList<LmChatMessage> messages, LmToolCall call, string result)
    {
        if (call.Name != ClientToolNames.CodingRead || result.Length < MinimumCharacters) return result;
        var key = RangeKey(call.Arguments);
        var current = Comparable(result);
        if (key is null || current is null) return result;
        var reads = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        foreach (var message in messages)
            foreach (var earlier in message.ToolCalls ?? [])
                if (earlier.Name == ClientToolNames.CodingRead) reads[earlier.Id] = earlier;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.Role != "tool" || message.ToolCallId is not { } id || !reads.TryGetValue(id, out var earlier)) continue;
            if (RangeKey(earlier.Arguments) != key || !string.Equals(Comparable(message.Content), current, StringComparison.Ordinal)) continue;
            return JsonSerializer.Serialize(new
            {
                status = "unchanged",
                path = Path(call.Arguments),
                sameAsCallId = id,
                message = "Inhalt unverändert seit dem früheren Lesen dieses Bereichs (Aufruf " + id
                    + "); er steht weiter oben im Kontext. Nicht erneut lesen.",
            }, MissumAiProtocol.CreateJsonOptions());
        }
        return result;
    }

    /// <summary>
    /// The file data of a read: the client wraps it with a per-call proposal id, which
    /// differs on every call and must not defeat the comparison. A result without file
    /// content (an error, an earlier reference) is never deduplicated.
    /// </summary>
    internal static string? Comparable(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var data = root.TryGetProperty("result", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
            return data.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? data.GetRawText() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? RangeKey(JsonElement arguments)
    {
        if (Path(arguments) is not { } path) return null;
        return path.Replace('\\', '/') + "|" + Number(arguments, "startLine") + "|" + Number(arguments, "maximumLines");
    }

    private static string? Path(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
            ? path.GetString() : null;

    private static string Number(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetRawText() : "";
}

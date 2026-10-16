using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Parsing only for immutable historical research receipts during context compaction.</summary>
internal static class LegacyResearchContext
{
    internal const string Protocol = "section-delta-v1";
    internal const string RecoveryMarker = "[MISSUM_SCIENCE_STATE_RECOVERY]";
    internal const string VerificationRepairMarker = "[MISSUM_SCIENCE_VERIFY_REPAIR]";
    internal const string PublicationBegin = "<!-- MISSUM_PUBLICATION_BEGIN -->";
    internal const string PublicationEnd = "<!-- MISSUM_PUBLICATION_END -->";
    internal static bool TryReceipt(string? content, out JsonElement result, out bool completed)
    {
        result = default;
        completed = false;
        if (string.IsNullOrWhiteSpace(content)) return false;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var payload)
                || payload.ValueKind != JsonValueKind.Object) return false;
            result = payload.Clone();
            completed = Text(root, "status") == "completed" && Boolean(payload, "success");
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal static long Number(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var number) ? number : -1;
    internal static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;
    internal static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
}

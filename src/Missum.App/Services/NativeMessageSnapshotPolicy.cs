using System.Globalization;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Durable revisions own lifecycle state; streaming clocks only protect newer payloads.</summary>
internal static class NativeMessageSnapshotPolicy
{
    internal readonly record struct Merge(bool PreservePayload, bool PreserveLifecycle);

    internal static Merge Compare(JsonElement current, JsonElement incoming, string liveUpdatedAt)
    {
        var currentRevision = Revision(current);
        var incomingRevision = Revision(incoming);
        if (currentRevision > 0 && incomingRevision > 0 && currentRevision != incomingRevision)
            return new(currentRevision > incomingRevision, currentRevision > incomingRevision);

        var newerPayload = DateTimeOffset.TryParse(liveUpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var liveAt)
            && DateTimeOffset.TryParse(Text(incoming, "updatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var incomingAt) && liveAt > incomingAt;
        // A delayed final receipt can trail an already projected token's clock.
        // Retain that token without turning Cancelled/Failed back into Streaming.
        var terminalTransition = IsTerminal(Text(incoming, "status")) && !IsTerminal(Text(current, "status"));
        return new(newerPayload, newerPayload && !terminalTransition);
    }

    internal static bool IsTerminal(string status) => status is "completed" or "cancelled" or "interrupted" or "failed";

    private static long Revision(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var number) ? number : 0;
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
}

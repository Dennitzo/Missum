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
        var newerPayload = DateTimeOffset.TryParse(liveUpdatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var liveAt)
            && DateTimeOffset.TryParse(Text(incoming, "updatedAt"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var incomingAt) && liveAt > incomingAt;
        if (currentRevision > 0 && incomingRevision > 0 && currentRevision != incomingRevision)
        {
            if (currentRevision > incomingRevision) return new(true, true);
            // Durable revisions count commits, not SSE events. A write-behind
            // snapshot can have a newer revision while containing an older
            // acknowledged prefix. Only live-to-live snapshots use their
            // source clocks; terminal results and a new attempt remain durable.
            return new(newerPayload && IsActive(Text(current, "status")) && IsActive(Text(incoming, "status")), false);
        }
        // A delayed final receipt can trail an already projected token's clock.
        // Retain that token without turning Cancelled/Failed back into Streaming.
        var terminalTransition = IsTerminal(Text(incoming, "status")) && !IsTerminal(Text(current, "status"));
        return new(newerPayload, newerPayload && !terminalTransition);
    }

    internal static bool IsTerminal(string status) => status is "completed" or "cancelled" or "interrupted" or "failed";

    private static bool IsActive(string status) => status is "pending" or "streaming";

    private static long Revision(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("revision", out var revision) && revision.TryGetInt64(out var number) ? number : 0;
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
}

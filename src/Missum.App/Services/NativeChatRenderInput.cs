using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Visible source revision, independent of hidden reasoning and token telemetry.</summary>
internal sealed class NativeChatRenderInput(string content, JsonElement[] steps, JsonElement[] artifacts,
    string status, bool hasOlder, bool waiting, string error = "")
{
    internal string Content { get; } = content;
    internal JsonElement[] Steps { get; } = steps;
    internal JsonElement[] Artifacts { get; } = artifacts;
    internal string Status { get; } = status;
    internal bool HasOlder { get; } = hasOlder;
    internal bool Waiting { get; } = waiting;
    private string Error { get; } = error;
    private static string Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var field) ? field.ToString() : "";
    private static bool Hidden(JsonElement step) => Text(step, "tool") is
        "assistant.reasoning" or "assistant.progress" or "context.compact";

    internal bool Equivalent(NativeChatRenderInput other)
    {
        if (Content != other.Content || Status != other.Status || HasOlder != other.HasOlder
            || Waiting != other.Waiting || Error != other.Error || !Artifacts.SequenceEqual(other.Artifacts)) return false;
        // Hidden receipts may affect only the presence/position of the pending
        // tail. Never compare or copy their potentially huge reasoning body.
        var left = NativeActivityProjection.Build(Steps, Status is "streaming" or "pending", Content.Length, Waiting);
        var right = NativeActivityProjection.Build(other.Steps, other.Status is "streaming" or "pending", other.Content.Length, other.Waiting);
        return left.Count == right.Count && left.Zip(right).All(pair => pair.First.Key == pair.Second.Key
            && pair.First.Offset == pair.Second.Offset && pair.First.EndOffset == pair.Second.EndOffset
            && pair.First.Step.Equals(pair.Second.Step));
    }

    internal bool CanPublishBefore(NativeChatRenderInput newer)
    {
        if (!newer.Content.StartsWith(Content, StringComparison.Ordinal)) return false;
        var latest = newer.Steps.Where(step => !Hidden(step)).ToArray();
        var position = 0;
        foreach (var step in Steps.Where(step => !Hidden(step)))
        {
            while (position < latest.Length && Text(latest[position], "id") != Text(step, "id")) position++;
            if (position == latest.Length) return false;
            var candidate = latest[position++];
            if (Text(step, "tool") != Text(candidate, "tool")
                || Text(step, "contentOffset") != Text(candidate, "contentOffset")) return false;
            // A source replacement must not briefly republish its old text.
            if (Text(step, "tool") == "assistant.narration"
                && !Text(candidate, "detail").StartsWith(Text(step, "detail"), StringComparison.Ordinal)) return false;
        }
        return true;
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private sealed record LiveSourceMetadata(ConversationSource[] Sources);
    private static readonly ConditionalWeakTable<AssistantToolStep, LiveSourceMetadata> SourceMetadata = new();

    // Extract addresses before lazy display projection removes the tool bodies.
    // Immutable receipts reuse their compact metadata on child token snapshots.
    internal static ConversationSource[]? LiveSourcesFor(AssistantToolStep? step) => step is null
        || !(step.Tool.StartsWith("web.", StringComparison.Ordinal) || step.Tool.StartsWith("research.", StringComparison.Ordinal))
        || step.InputJson is null && step.OutputJson is null ? null
        : SourceMetadata.GetValue(step, static source =>
        {
            var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Collect(JsonElement value, int depth)
            {
                if (depth > 24 || urls.Count >= 1000) return;
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var title = Text(value, "title", Text(value, "pageTitle", Text(value, "name"))).Trim();
                    if (title.Length > 512) title = title[..512];
                    foreach (var property in value.EnumerateObject())
                        if (property.Value.ValueKind == JsonValueKind.String
                            && property.Name.Contains("url", StringComparison.OrdinalIgnoreCase)
                            && !property.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase)
                            && !property.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                            && !property.Name.Contains("icon", StringComparison.OrdinalIgnoreCase)
                            && property.Value.GetString() is { Length: <= 8192 } url
                            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                        {
                            if (!urls.TryGetValue(uri.AbsoluteUri, out var existing) || existing.Length == 0)
                                urls[uri.AbsoluteUri] = title;
                        }
                        else Collect(property.Value, depth + 1);
                }
                else if (value.ValueKind == JsonValueKind.Array)
                    foreach (var item in value.EnumerateArray()) Collect(item, depth + 1);
                else if (value.ValueKind == JsonValueKind.String && value.GetString()?.TrimStart() is { Length: > 0 } json
                    && json[0] is '{' or '[') Parse(json, depth + 1);
            }
            void Parse(string? json, int depth)
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                try { using var document = JsonDocument.Parse(json); Collect(document.RootElement, depth); }
                catch (JsonException) { }
            }
            static string Text(JsonElement value, string name, string fallback = "") => value.TryGetProperty(name, out var field)
                && field.ValueKind == JsonValueKind.String ? field.GetString() ?? fallback : fallback;
            Parse(source.OutputJson, 0); Parse(source.InputJson, 0);
            return new(urls.Select(pair => new ConversationSource(source.Id, source.Tool, pair.Value, pair.Key,
                source.UpdatedAt ?? source.CompletedAt ?? source.StartedAt ?? DateTimeOffset.MinValue)).ToArray());
        }).Sources;

    private async Task<bool> HandleSourceReadAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken token)
    {
        if (envelope.Type != "sources.list") return false;
        var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
        var sources = await Task.Run(() => conversationSnapshots.GetSourcesAsync(sessionId, token), token).ConfigureAwait(false);
        await emit("sources.snapshot", new { sessionId, sources }, envelope.RequestId).ConfigureAwait(false);
        return true;
    }
}

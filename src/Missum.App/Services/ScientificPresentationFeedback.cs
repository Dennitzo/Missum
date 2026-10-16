using System.Buffers;
using System.Text.Json;

namespace Missum.App.Services;

public sealed record ScientificPresentationSectionFailure(string Id, long Revision);
public sealed record ScientificPresentationIssue(string Code, string Message, bool ContentRepairRequired,
    IReadOnlyList<ScientificPresentationSectionFailure> Sections);
public sealed record ScientificPresentationFeedback(long Generation, long PublicationRevision,
    string PublicationStatus, ScientificPresentationIssue? PublicationIssue);

/// <summary>Appends presentation diagnostics to new receipts without changing stored research or prior model turns.</summary>
internal static class ScientificPresentationFeedbackProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static JsonElement Create(string projectId, long revision, long publicationRevision,
        ScientificPresentationFeedback? feedback, bool pending)
    {
        var current = feedback is not null && feedback.PublicationRevision == publicationRevision;
        var status = current ? feedback!.PublicationStatus : "pending";
        var issue = current ? feedback!.PublicationIssue : null;
        return JsonSerializer.SerializeToElement(new
        {
            projectId,
            status = issue is not null ? "failed" : pending ? "pending" : status,
            publication = new { status, error = issue },
            nextAction = issue is not null ? "Bearbeite die gemeldete Stelle direkt in der Publikation.md; die bisherige PDF bleibt erhalten." : null,
        }, JsonOptions);
    }

    internal static JsonElement Append(JsonElement receipt, JsonElement presentation)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in receipt.EnumerateObject())
                if (property.Name != "presentation") property.WriteTo(writer);
            writer.WritePropertyName("presentation");
            presentation.WriteTo(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    internal static ScientificPresentationIssue PublicationIssue(Exception exception)
    {
        var content = exception as ScientificPublicationContentException;
        return new(content is null ? "research.publication_render_failed" : "research.publication_content_invalid",
            Bound(exception.Message), content is not null,
            content?.Sections ?? []);
    }

    private static string Bound(string text)
    {
        const int limit = 16_000;
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..length] + "…";
    }
}

using System.Buffers;
using System.Text.Json;

namespace Missum.App.Services;

public sealed record ScientificPresentationSectionFailure(string Id, long Revision);
public sealed record ScientificPresentationIssue(string Code, string Message, bool ContentRepairRequired,
    IReadOnlyList<ScientificPresentationSectionFailure> Sections);
public sealed record ScientificPresentationFeedback(long Generation, long PublicationRevision,
    string PublicationStatus, ScientificPresentationIssue? PublicationIssue,
    string SimulationStatus, ScientificPresentationIssue? SimulationIssue);

/// <summary>Appends presentation diagnostics to new receipts without changing stored research or prior model turns.</summary>
internal static class ScientificPresentationFeedbackProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static JsonElement Create(string projectId, long revision, long publicationRevision,
        ScientificPresentationFeedback? feedback, bool pending)
    {
        // A render of another manuscript revision must never ask the model to repair this revision.
        var current = feedback is not null && feedback.PublicationRevision == publicationRevision;
        var publicationStatus = current ? feedback!.PublicationStatus : "pending";
        var simulationStatus = current ? feedback!.SimulationStatus : "pending";
        var publicationIssue = current ? feedback!.PublicationIssue : null;
        var simulationIssue = current ? feedback!.SimulationIssue : null;
        return JsonSerializer.SerializeToElement(new
        {
            protocol = "science-presentation-feedback-v1", projectId, revision, publicationRevision,
            generation = current ? feedback!.Generation : 0,
            status = publicationIssue is not null || simulationIssue is not null ? "failed"
                : pending || publicationStatus == "pending" || simulationStatus == "pending" ? "pending"
                : publicationStatus == "empty" ? "empty" : "ready",
            publication = new { status = publicationStatus, error = publicationIssue },
            simulation = new { status = simulationStatus, error = simulationIssue },
            nextAction = publicationIssue?.ContentRepairRequired == true
                ? "Die Forschungsdaten sind gespeichert, aber die PDF wurde nicht erzeugt. Lies nur die gemeldeten Abschnitte mit research.read ids und korrigiere sie mit research.update und ihren aktuellen expectedRevision-Werten. Wiederhole keine unveränderten Abschnitte."
                : publicationIssue is not null || simulationIssue is not null
                    ? "Die Forschungsdaten sind gespeichert. Dies ist ein technischer Darstellungsfehler; schreibe dafür nicht das Manuskript neu. Prüfe den aktuellen Stand mit research.deliverables.verify."
                    : pending || publicationStatus == "pending" || simulationStatus == "pending"
                        ? "Die Darstellung wird noch erzeugt; Speicherung ist kein PDF-Erfolgsbeleg. Vor Abschluss mit research.deliverables.verify prüfen; kein unverändertes research.update wiederholen."
                    : publicationStatus == "empty" ? "Noch kein ausgearbeiteter Publikationsabschnitt vorhanden. Forschung mit gezielten neuen Inhalten fortsetzen; es liegt noch kein PDF-Erfolgsbeleg vor."
                        : "Die aktuelle Darstellung wurde geprüft. Fachliche und verpflichtende Nachweise vor Abschluss weiterhin mit research.deliverables.verify prüfen.",
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

    internal static ScientificPresentationIssue SimulationIssue(string error) =>
        new("research.simulation_render_failed", Bound(error), false, []);

    private static string Bound(string text)
    {
        const int limit = 16_000;
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..length] + "…";
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Core.Research;

namespace Missum.App.Services;

internal static class ScientificResearchReviewPersistence
{
    internal static bool IsCurrent(ResearchWorkingState assessed, ScientificPresentationSnapshot? presentation,
        ResearchWorkingState current, ScientificPresentationSnapshot? currentPresentation) =>
        ScientificResearchReview.Fingerprint(assessed) == ScientificResearchReview.Fingerprint(current)
        && presentation?.Version == currentPresentation?.Version;

    internal static JsonElement RejectStale(JsonElement verification, ResearchWorkingState current)
    {
        const string diagnosis = "Der Forschungsstand oder die Darstellung wurde während der Prüfung geändert. Lies den aktuellen Stand und prüfe nach Abschluss der Darstellung erneut mit research.deliverables.verify; der ältere Prüfstand erlaubt keinen Abschluss.";
        var result = JsonNode.Parse(verification.GetRawText())!.AsObject();
        result["success"] = false;
        result["message"] = diagnosis;
        result["retryable"] = true;
        result["currentResearchRevision"] = current.Revision;
        result["currentPublicationRevision"] = current.PublicationRevision;
        if (result["research"] is JsonObject research)
        {
            research["ready"] = false;
            // Currentness is the first actionable diagnosis, regardless of other old-snapshot findings.
            if (research["missing"] is not JsonArray missing)
            {
                missing = new JsonArray();
                research["missing"] = missing;
            }
            missing.Insert(0, diagnosis);
        }
        if (result["scientificReview"] is JsonObject review) review["ready"] = false;
        return JsonSerializer.SerializeToElement(result);
    }

    internal static ResearchVerification? Create(ResearchWorkingState state, JsonElement verification)
    {
        if (!verification.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
            || !verification.TryGetProperty("scientificReview", out var review) || review.ValueKind != JsonValueKind.Object
            || !review.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True) return null;
        var fingerprint = ScientificResearchReview.Fingerprint(state);
        if (!review.TryGetProperty("stateSha256", out var hash) || hash.GetString() != fingerprint) return null;
        return new("review-" + fingerprint, state.ProjectId, "publication-review", state.ProjectId,
            "SourcesCalculationsContradictions", ScientificResearchReview.Method, "ReviewedWithEvidence",
            review.GetRawText(), DateTimeOffset.UtcNow);
    }
}

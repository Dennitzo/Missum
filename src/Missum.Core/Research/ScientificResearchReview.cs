using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Core.Research;

/// <summary>A reproducible audit of declared scientific review and its actual stored evidence, not a truth certificate.</summary>
public static class ScientificResearchReview
{
    public const string Protocol = "science-review-v1";
    public const string Method = "MissumScientificReview";
    public const string Scope = "Dokumentierter Quellen-, Rechen- und Widerspruchsabgleich im angegebenen Geltungsbereich; keine allgemeine Wahrheitsgarantie.";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AssessmentFields = ["sourceAssessment", "calculationAssessment", "contradictionAssessment", "scope"];
    private static readonly string[] ReviewedStatuses = ["completed", "supported", "verified", "refuted", "superseded", "openLimit"];

    public static ScientificResearchReviewReport Assess(ResearchWorkingState state,
        IReadOnlyList<ResearchLiteratureEntry> sources, IReadOnlyList<ResearchEvidenceRecord> evidence,
        IReadOnlyList<ResearchExperiment> experiments, IReadOnlyList<ResearchVerification> checks)
    {
        var missing = new List<string>();
        var issues = new List<ScientificResearchReviewIssue>();
        var sections = state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null
            && Text(item.Data, "status") != "withdrawn").ToArray();
        var targets = sections.SelectMany(item => Ids(item.Data, "claimIds").Concat(Ids(item.Data, "hypothesisIds")))
            .ToHashSet(StringComparer.Ordinal);
        // Claims may rely on hypotheses or other claims. Review their complete referenced dependency set once.
        var pending = new Queue<string>(targets);
        while (pending.TryDequeue(out var target))
        {
            var item = state.Items.FirstOrDefault(item => item.Id == target);
            if (item is null) continue;
            foreach (var dependency in Ids(item.Data, "claimIds").Concat(Ids(item.Data, "hypothesisIds")))
                if (targets.Add(dependency)) pending.Enqueue(dependency);
        }
        var reviewed = sections.Concat(state.Items.Where(item => targets.Contains(item.Id))).DistinctBy(item => item.Id)
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (sections.Length == 0) Add("$publication", 0, "missing_sections", "sections", "",
            "Fachliche Prüfung: Es liegt noch kein Publikationsabschnitt vor.", "Fachliche Abschnitte mit research.update anlegen.");
        foreach (var unknown in targets.Where(id => !state.Items.Any(item => item.Id == id)))
            Add(unknown, 0, "linked_item_missing", "claimIds/hypothesisIds", "",
                $"Fachliche Prüfung: Der verknüpfte Claim bzw. die Hypothese {unknown} fehlt im aktuellen Forschungsstand.",
                "Nur die fehlende Kennung lesen bzw. eine falsche Referenz korrigieren; keine vorhandenen Abschnittstexte wiederholen.");
        foreach (var item in reviewed)
        {
            var prefix = $"Fachliche Prüfung {item.Id} (Revision {item.Revision}): ";
            if (!item.Data.TryGetProperty("review", out var review) || !ValidReview(review)
                || Number(review, "itemRevision") != item.Revision)
            {
                Add(item.Id, item.Revision, review.ValueKind == JsonValueKind.Object ? "review_revision_mismatch" : "review_missing",
                    "data.review", review.ValueKind == JsonValueKind.Object ? Number(review, "itemRevision").ToString(System.Globalization.CultureInfo.InvariantCulture) : "<missing>",
                    prefix + "Quellen, Rechnungen, Widersprüche und Geltungsbereich tatsächlich prüfen und data.review für die aktuelle Objektversion dokumentieren.",
                    "Nach tatsächlicher Prüfung nur die Metadaten mit changes[].patch aktualisieren: status ausdrücklich angeben und review.itemRevision=expectedRevision+1; den vorhandenen contentMarkdown nicht neu ausgeben.");
            }
            var status = Text(item.Data, "status");
            if (status is not ("completed" or "supported" or "verified" or "refuted" or "superseded" or "openLimit"))
                Add(item.Id, item.Revision, status.Length == 0 ? "status_missing" : "status_open", "data.status", status.Length == 0 ? "<missing>" : status,
                    prefix + $"data.status ist {(status.Length == 0 ? "nicht gesetzt" : '"' + status + '"')}. Der Ansatz ist noch ungeprüft oder offen; fachlich weiterarbeiten oder eine belegte Nachweisgrenze dokumentieren.",
                    "Mit changes[].patch den tatsächlich belegten Status setzen, zusammen mit dem aktuellen review; vorhandenen Text und Referenzen erhalten. completed/supported erst nach Prüfung, verified nur mit gültigem Prüfbeleg; refuted bzw. begründetes openLimit bei Widerlegung oder Nachweisgrenzen.", ReviewedStatuses);
            if (status is "openLimit" or "superseded" && Text(item.Data, "reason").Length == 0)
                Add(item.Id, item.Revision, "reason_missing", "data.reason", "<missing>", prefix + "Die fachliche Grenze bzw. Ablösung benötigt eine konkrete Begründung.",
                    "Nur reason und einen revisionsaktuellen review per patch ergänzen.");
            var sourceIds = Ids(item.Data, "sourceIds");
            var evidenceIds = Ids(item.Data, "evidenceIds");
            var experimentIds = Ids(item.Data, "experimentIds");
            var checkIds = Ids(item.Data, "checkIds");
            if (sourceIds.Any(id => !sources.Any(source => source.WorkId == id && source.ProjectId == state.ProjectId)))
                Add(item.Id, item.Revision, "source_mismatch", "data.sourceIds", "foreign_or_missing", prefix + "Ein Quellenbezug gehört nicht zum aktuellen Forschungsprojekt.",
                    "Nur die betroffenen sourceIds/evidenceIds anhand vorhandener Projektbelege korrigieren.");
            var sourceEvidence = evidence.Any(entry => entry.ProjectId == state.ProjectId && evidenceIds.Contains(entry.Id)
                && sourceIds.Contains(entry.WorkId) && entry.ExactExcerpt.Length > 0
                && entry.ContentHash.Length == 64 && entry.ContentHash.All(Uri.IsHexDigit));
            var experimentEvidence = experiments.Any(experiment => experiment.ProjectId == state.ProjectId
                && experimentIds.Contains(experiment.Id) && MeasuredExecution(experiment));
            var checkEvidence = checks.Any(check => check.ProjectId == state.ProjectId && checkIds.Contains(check.Id)
                && check.TargetId == item.Id && check.Method != Method
                && check.Status is "Verified" or "KernelAccepted" or "SymbolicallyVerified");
            var linkedEvidence = item.Kind == "section" && (Ids(item.Data, "claimIds").Count > 0 || Ids(item.Data, "hypothesisIds").Count > 0);
            var explicitScope = Text(item.Data, "classification") is "definition" or "technical" or "hypothesis"
                && (status == "openLimit" || Text(item.Data, "assumptions").Length > 0 || Text(item.Data, "reason").Length > 0);
            if (!sourceEvidence && !experimentEvidence && !checkEvidence && !linkedEvidence && !explicitScope)
                Add(item.Id, item.Revision, "missing_evidence", "data.evidenceIds/experimentIds/checkIds", "<missing>",
                    prefix + "Es fehlen zugeordnete gelesene Quellenbelege oder echte Rechen-/Prüfbelege. Rein definitorische, technische oder hypothetische Inhalte ausdrücklich einordnen und ihre Grenzen nennen.",
                    "Vorhandene passende Belegkennungen gezielt lesen und verknüpfen; neue Rechnung nur bei fehlendem Nachweis. Keine Belege oder erfolgreichen Status erfinden.");
            if (sourceIds.Count > 0 && !sourceEvidence && !linkedEvidence)
                Add(item.Id, item.Revision, "source_excerpt_missing", "data.evidenceIds", "<missing>",
                    prefix + "Den Quellenabgleich mit evidenceIds der gelesenen Originaltextstellen belegen; ein Suchtreffer oder Quellentitel genügt nicht.",
                    "Nur die passende Originaltextstelle mit sourceIds/evidenceIds verknüpfen; bei nicht zugänglicher Quelle eine belegte Grenze statt fortgesetzter identischer Abrufe dokumentieren.");
        }
        return new(Protocol, state.ProjectId, state.Revision, state.PublicationRevision, Fingerprint(state),
            missing.Count == 0, reviewed.Select(item => item.Id).ToArray(), missing.Distinct(StringComparer.Ordinal).ToArray(), Scope, issues);

        void Add(string id, long revision, string code, string field, string currentValue, string message, string nextAction, IReadOnlyList<string>? allowedValues = null)
        {
            missing.Add(message);
            issues.Add(new(id, revision, code, field, currentValue, allowedValues ?? [], message, nextAction));
        }
    }

    public static bool ValidReview(JsonElement review) => review.ValueKind == JsonValueKind.Object
        && review.EnumerateObject().Count() == 5 && Number(review, "itemRevision") > 0
        && AssessmentFields
            .All(name => Text(review, name) is { Length: > 0 and <= 4000 } text && !string.IsNullOrWhiteSpace(text));

    public static string Fingerprint(ResearchWorkingState state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { state.ProjectId, state.Revision, state.PublicationRevision, state.Title,
            items = state.Items.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new { item.Id, item.Kind, item.Revision, item.OwnerAgentId, item.Data }) }, JsonOptions))));

    /// <summary>Call with protected execution receipts, never raw model-authored verification rows.</summary>
    public static bool HasCurrentReview(ResearchWorkingState state, IReadOnlyList<ResearchVerification> trustedChecks) =>
        trustedChecks.Any(check => check.ProjectId == state.ProjectId && check.TargetType == "publication-review"
            && check.TargetId == state.ProjectId && check.Method == Method && check.Status == "ReviewedWithEvidence"
            && CurrentReceipt(check.EvidenceJson, state));

    private static bool CurrentReceipt(string json, ResearchWorkingState state)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var report = document.RootElement;
            return Text(report, "protocol") == Protocol && Text(report, "projectId") == state.ProjectId
                && Number(report, "revision") == state.Revision && Number(report, "publicationRevision") == state.PublicationRevision
                && Text(report, "stateSha256") == Fingerprint(state) && report.TryGetProperty("ready", out var ready)
                && ready.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool MeasuredExecution(ResearchExperiment experiment)
    {
        if (experiment.VerificationStatus != "ProcessSucceeded") return false;
        try
        {
            using var document = JsonDocument.Parse(experiment.StdoutEvidence);
            return document.RootElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
                && runs.EnumerateArray().Any(run => Number(run, "exitCode") == 0 && Text(run, "runId").Length > 0
                    && run.TryGetProperty("inputHashes", out var inputs) && inputs.ValueKind == JsonValueKind.Object
                    && run.TryGetProperty("outputHashes", out var outputs) && outputs.ValueKind == JsonValueKind.Object);
        }
        catch (JsonException) { return false; }
    }

    private static HashSet<string> Ids(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal) : [];
    private static string Text(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static long Number(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number) ? number : -1;
}

public sealed record ScientificResearchReviewReport(string Protocol, string ProjectId, long Revision, long PublicationRevision,
    string StateSha256, bool Ready, IReadOnlyList<string> ReviewedItemIds, IReadOnlyList<string> Missing, string Scope,
    IReadOnlyList<ScientificResearchReviewIssue> Issues);

public sealed record ScientificResearchReviewIssue(string Id, long Revision, string Code, string Field, string CurrentValue,
    IReadOnlyList<string> AllowedValues, string Message, string NextAction);

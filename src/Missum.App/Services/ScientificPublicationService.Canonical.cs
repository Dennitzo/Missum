using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class ScientificPublicationService
{
    internal static bool HasCanonicalSections(ResearchWorkingState state) => state.Items.Any(item =>
        item.Kind == "section" && item.OwnerAgentId is null && Text(item.Data, "status") != "withdrawn"
        && !string.IsNullOrWhiteSpace(Text(item.Data, "contentMarkdown")));

    private static bool IsCanonicalDraft(ResearchWorkingState state, IReadOnlyList<ResearchVerification>? trustedChecks,
        ResearchResultSnapshot results) => !ScientificResearchReview.HasCurrentReview(state, trustedChecks ?? [])
            || ScientificPublicationEvidence.Assess(state, results.Experiments, trustedChecks ?? []).Count > 0;

    /// <summary>One-time import of historical structured text; later edits belong to the Markdown file.</summary>
    internal static string FormatCanonicalPublication(ResearchWorkingState state,
        IReadOnlyList<ResearchLiteratureEntry> works, ResearchResultSnapshot results, IReadOnlyList<ResearchVerification>? trustedChecks = null)
    {
        var sections = OrderedPublicationSections(state);
        var text = new StringBuilder().Append("# ").AppendLine(OneLine(string.IsNullOrWhiteSpace(state.Title) ? "Publikation" : state.Title))
            .AppendLine().AppendLine("**Missum · Claude Science**").AppendLine();
        var usedFigures = DeclaredPlotPaths(sections);
        var scopedExperiments = results.Experiments.Where(item => item.ProjectId == state.ProjectId).ToArray();
        var latestPlots = LatestMeasuredPlots(scopedExperiments);
        foreach (var section in sections)
        {
            var title = Text(section.Data, "title");
            text.Append("## ").AppendLine(OneLine(string.IsNullOrWhiteSpace(title) ? section.Id : title)).AppendLine()
                .AppendLine(Text(section.Data, "contentMarkdown").Trim()).AppendLine();
            if (section.Data.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array)
            {
                foreach (var unit in units.EnumerateArray())
                    text.Append("- $").Append(Text(unit, "symbol")).Append("$ ist ").Append(Text(unit, "meaning"))
                        .Append(" in $").Append(Text(unit, "unit")).AppendLine("$.");
                text.AppendLine();
            }
            AppendSectionSimulationDescriptions(text, section, state, scopedExperiments);
            AppendSectionLeanProofs(text, section, state, trustedChecks ?? []);
            if (section.Data.TryGetProperty("figureCaptions", out var figures) && figures.ValueKind == JsonValueKind.Array)
                foreach (var figure in figures.EnumerateArray())
                {
                    var path = Text(figure, "artifactPath").Replace('\\', '/');
                    if (path.Length == 0) continue;
                    text.Append("![").Append(EscapeLabel(Text(figure, "caption"))).Append("](")
                        .Append(path.Replace(" ", "%20", StringComparison.Ordinal)).AppendLine(")").AppendLine();
                }
            AppendMeasuredPlots(text, latestPlots.Where(image => SectionExperiments(section, state).Contains(image.ExperimentId)),
                usedFigures, includeHeading: false);
        }
        if (works.Count > 0)
        {
            text.AppendLine("## Literatur und Quellen").AppendLine();
            foreach (var work in works)
            {
                text.Append("- **").Append(EscapeLabel(work.Title)).Append("**. ");
                if (Uri.TryCreate(work.CanonicalUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo))
                    text.Append("[Originalquelle](").Append(uri.AbsoluteUri.Replace(")", "%29", StringComparison.Ordinal)).Append("). ");
                text.AppendLine().AppendLine();
            }
        }
        AppendOutlineMetadata(text, sections);
        return text.ToString();
    }

    private static HashSet<string> SectionSources(ResearchWorkingItem section, ResearchWorkingState state)
    {
        return SectionEvidenceItems(section, state).SelectMany(item => Ids(item.Data, "sourceIds")).ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<ResearchWorkingItem> LinkedScientificItems(ResearchWorkingItem section, ResearchWorkingState state)
    {
        var ids = Ids(section.Data, "claimIds").Concat(Ids(section.Data, "hypothesisIds")).ToHashSet(StringComparer.Ordinal);
        foreach (var claim in state.Items.Where(item => item.Kind == "claim" && ids.Contains(item.Id)).ToArray())
            ids.UnionWith(Ids(claim.Data, "hypothesisIds"));
        return state.Items.Where(item => item.Kind is "claim" or "hypothesis" && ids.Contains(item.Id));
    }

    private static string CanonicalDependencyFingerprint(PublicationSnapshot snapshot)
    {
        var state = snapshot.WorkingState!;
        var selected = state.Items.Where(item => item.Kind == "section" && Text(item.Data, "status") != "withdrawn").ToArray();
        var related = selected.SelectMany(item => SectionEvidenceItems(item, state)).DistinctBy(item => item.Id).ToArray();
        var sourceIds = related.SelectMany(item => Ids(item.Data, "sourceIds")).ToHashSet(StringComparer.Ordinal);
        var experimentIds = related.SelectMany(item => Ids(item.Data, "experimentIds")).ToHashSet(StringComparer.Ordinal);
        return Fingerprint(JsonSerializer.Serialize(new
        {
            reviewed = ScientificResearchReview.HasCurrentReview(state, snapshot.TrustedChecks ?? []),
            sources = snapshot.Works.Where(work => sourceIds.Contains(work.WorkId)).OrderBy(work => work.WorkId)
                .Select(work => new { work.WorkId, work.Title, work.CanonicalUrl }),
            experiments = snapshot.Results.Experiments.Where(experiment => experiment.ProjectId == snapshot.Project.Id
                && (experimentIds.Contains(experiment.Id) || MeasuredImageHashes(experiment).Count > 0)).OrderBy(experiment => experiment.Id)
                .Select(experiment => new { experiment.Id, experiment.StdoutEvidence, experiment.VerificationStatus }),
            proofs = (snapshot.TrustedChecks ?? []).Where(check => check.Method == "math.formalProof")
                .OrderBy(check => check.Id).Select(check => new { check.Id, check.Status, check.EvidenceJson }),
        }));
    }

    private static Dictionary<string, string> MeasuredImageHashes(ResearchExperiment experiment)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (experiment.VerificationStatus != "ProcessSucceeded") return hashes;
        try
        {
            using var document = JsonDocument.Parse(experiment.StdoutEvidence);
            if (!document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return hashes;
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("exitCode", out var code) || !code.TryGetInt32(out var exit) || exit != 0
                    || run.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind == JsonValueKind.True
                    || Text(run, "runId").Length == 0 || Text(run, "snapshotId").Length == 0
                    || !run.TryGetProperty("inputHashes", out var inputs) || inputs.ValueKind != JsonValueKind.Object
                    || !run.TryGetProperty("outputHashes", out var output) || output.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in output.EnumerateObject())
                {
                    var path = property.Name;
                    if (property.Value.ValueKind != JsonValueKind.String || !(path.StartsWith("artifacts/", StringComparison.Ordinal) || path.StartsWith("work/", StringComparison.Ordinal))
                        || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)
                        || path.Split('/').Any(part => part is "" or "." or "..")
                        || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")) continue;
                    var hash = property.Value.GetString()!;
                    if (hash.Length == 64 && hash.All(char.IsAsciiHexDigit)) hashes[path] = hash;
                }
            }
        }
        catch (JsonException) { }
        return hashes;
    }

    private static void ValidateCanonicalImages(PublicationSnapshot snapshot, ScientificPublicationImages.PreparedImages images)
    {
        var sections = snapshot.WorkingState!.Items.Where(item => item.Kind == "section" && Text(item.Data, "status") != "withdrawn").ToArray();
        var automaticHashes = AutomaticPlotHashes(snapshot.WorkingState!, snapshot.Results).ToArray();
        if (automaticHashes.Any(hash => !images.Images.Any(image => image.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))))
            throw new ScientificPublicationContentException("Die Simulationsabbildungen stimmen nicht mit den gespeicherten Ausführungsbelegen überein. Erzeuge fehlende oder veränderte Plots erneut; die vorherige PDF bleibt erhalten.",
                state: snapshot.WorkingState, sections: sections.Where(section => Ids(section.Data, "experimentIds").Count > 0)
                    .Select(section => new ScientificPresentationSectionFailure(section.Id, section.Revision)).ToArray());
        // Imported historical text keeps its original figure handling; it cannot establish a new successful verification.
        if (sections.All(section => section.Id.StartsWith("legacy-section-", StringComparison.Ordinal))) return;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        required.UnionWith(automaticHashes);
        foreach (var experiment in snapshot.Results.Experiments.Where(item => item.ProjectId == snapshot.Project.Id))
            allowed.UnionWith(MeasuredImageHashes(experiment).Values);
        foreach (var section in sections)
        {
            var referenced = snapshot.Results.Experiments.Where(experiment => experiment.ProjectId == snapshot.Project.Id
                && Ids(section.Data, "experimentIds").Contains(experiment.Id)).ToArray();
            foreach (var experiment in referenced) allowed.UnionWith(MeasuredImageHashes(experiment).Values);
            if (!section.Data.TryGetProperty("figureCaptions", out var figures) || figures.ValueKind != JsonValueKind.Array) continue;
            foreach (var figure in figures.EnumerateArray())
            {
                var experiment = referenced.FirstOrDefault(item => item.Id == Text(figure, "experimentId"));
                if (experiment is not null && MeasuredImageHashes(experiment).TryGetValue(Text(figure, "artifactPath").Replace('\\', '/'), out var hash)) required.Add(hash);
            }
        }
        if (!sections.Any(section => section.Id.StartsWith("legacy-section-", StringComparison.Ordinal))
                && images.Images.Any(image => !allowed.Contains(image.Sha256))
            || required.Any(hash => !images.Images.Any(image => image.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))))
            throw new ScientificPublicationContentException("Die Abbildungen stimmen nicht mit den gespeicherten Ausführungsbelegen überein. Prüfe figureCaptions/experimentIds des betroffenen Abschnitts und erzeuge fehlende oder veränderte Ergebnisse erneut; die vorherige PDF bleibt erhalten.",
                state: snapshot.WorkingState, sections: sections.Where(section => section.Data.TryGetProperty("figureCaptions", out _))
                    .Select(section => new ScientificPresentationSectionFailure(section.Id, section.Revision)).ToArray());
    }

    internal async Task<ScientificPublicationArtifact?> RestoreLastPublicationAsync(string projectId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = _current.Values.FirstOrDefault(item => item.ProjectId == projectId);
            if (cached is not null && await IsValidPdfAsync(cached.PdfPath, token).ConfigureAwait(false)) return cached;
            var source = await ResolveDocumentAsync(projectId, _defaultDirectory, token).ConfigureAwait(false);
            if (source is null) return null;
            var pdf = Path.ChangeExtension(source, ".pdf");
            if (!await IsValidPdfAsync(pdf, token).ConfigureAwait(false)) return null;
            return new(projectId, 0, source, pdf, false, new DateTimeOffset(File.GetLastWriteTimeUtc(pdf)),
                Fingerprint(await File.ReadAllTextAsync(source, token).ConfigureAwait(false)));
        }
        finally { _gate.Release(); }
    }

    private static bool IsContentRenderError(Exception exception) => exception.Message.Contains("KaTeX", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("mathematische Ausdrücke", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("nicht lesbare Abbildungen", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("Publikationstitel", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("Überschrift", StringComparison.OrdinalIgnoreCase);

    internal static string SectionRepairDiagnostic(ResearchWorkingState state, string error)
    {
        var targets = SectionRepairTargets(state, error);
        if (error.Contains("Publikationstitel", StringComparison.OrdinalIgnoreCase))
            return error + " Kürze nur den fachlichen Publikationstitel direkt in Publikation.md, ohne Auslassungspunkte oder Textverlust im Manuskript. Die vorherige PDF bleibt erhalten.";
        return error + (targets.Count > 0 ? " Betroffene Formel-/Abbildungsabschnitte eingrenzen: "
            + string.Join(", ", targets.Select(section => section.Id)) + "."
            : " Der fehlerhafte Abschnitt konnte nicht eindeutig zugeordnet werden; lies den aktuellen Stand und grenze die konkrete Formel oder Abbildung gezielt ein.")
            + " Korrigiere nur den fehlerhaften Abschnitt direkt in Publikation.md; die vorherige PDF bleibt erhalten.";
    }

    internal static IReadOnlyList<ScientificPresentationSectionFailure> SectionRepairTargets(ResearchWorkingState state, string error)
    {
        var sections = state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null
            && Text(item.Data, "status") != "withdrawn").ToArray();
        var headings = Regex.Matches(error, "Abschnitt \"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).ToArray();
        var expressions = Regex.Matches(error, @"Ausdruck:\s*(.*?)(?=,\s*Ursache:|;\s*Abschnitt\s|$)",
                RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => NormalizeDiagnosticWhitespace(match.Groups[1].Value.Trim())).Where(value => value.Length > 0).ToArray();
        var located = sections.Where(item =>
        {
            var content = Text(item.Data, "contentMarkdown");
            var title = Text(item.Data, "title");
            return headings.Contains(title, StringComparer.Ordinal)
                || headings.Any(heading => StripHeadingNumber(heading) == StripHeadingNumber(title))
                || (headings.Length == 0 && title.Length > 0 && error.Contains(title, StringComparison.Ordinal))
                || content.Replace("\r", "", StringComparison.Ordinal).Split('\n').Any(line =>
                    line.StartsWith('#') && headings.Contains(line.TrimStart('#').Trim(), StringComparer.Ordinal))
                || expressions.Any(expression => NormalizeDiagnosticWhitespace(content).Contains(expression, StringComparison.Ordinal));
        }).ToArray();
        return located.Select(item => new ScientificPresentationSectionFailure(item.Id, item.Revision)).ToArray();
    }

    private static string NormalizeDiagnosticWhitespace(string value) => Regex.Replace(value, @"\s+", " ",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    internal static string Text(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Number(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) ? number : 0;
    internal static IReadOnlyList<string> Ids(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
}

internal sealed class ScientificPublicationContentException(string message, Exception? innerException = null,
    ResearchWorkingState? state = null, IReadOnlyList<ScientificPresentationSectionFailure>? sections = null)
    : InvalidOperationException(message, innerException)
{
    internal long? PublicationRevision { get; } = state?.PublicationRevision;
    internal IReadOnlyList<ScientificPresentationSectionFailure> Sections { get; } = sections
        ?? (state is null ? [] : ScientificPublicationService.SectionRepairTargets(state, message));
}

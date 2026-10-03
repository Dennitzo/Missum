using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Missum.Ai.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class ScientificPublicationService
{
    private readonly ConcurrentDictionary<string, PublicationTokenSample> _publicationTokenSamples = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _recordedPublicationMetrics = new(StringComparer.Ordinal);

    internal void ObserveResearchTokens(string projectId, DateTimeOffset runStartedAt, RunEvent item)
    {
        if (_recordedPublicationMetrics.ContainsKey(projectId) || item.Data.ValueKind != JsonValueKind.Object) return;
        var data = item.Data;
        _publicationTokenSamples.AddOrUpdate(projectId, _ => Apply(new(item.RunId, runStartedAt, item.CreatedAt)),
            (_, prior) => prior.RunStartedAt > runStartedAt ? prior
                : Apply(prior.RunId == item.RunId ? prior : new(item.RunId, runStartedAt, item.CreatedAt)));

        PublicationTokenSample Apply(PublicationTokenSample prior)
        {
            if (item.CreatedAt < prior.ObservedAt) return prior;
            if (item.Type == "research.state.tokens")
                return prior with { ObservedAt = item.CreatedAt, InputTokens = Count("inputTokens"),
                    CompletedOutputTokens = Count("outputTokens"), CurrentOutputTokens = 0,
                    CachedPromptTokensLastTurn = Count("cachedPromptTokensLastTurn") };
            var state = Text(data, "state");
            if (state is "generationStarted" or "generationRetry")
                return prior with { ObservedAt = item.CreatedAt, CurrentOutputTokens = 0 };
            return Count("generatedTokens") is { } generated
                ? prior with { ObservedAt = item.CreatedAt, CurrentOutputTokens = Math.Max(prior.CurrentOutputTokens, generated) }
                : prior;
        }
        long? Count(string name) => data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var count) && count >= 0 ? count : null;
    }

    private sealed record PublicationTokenSample(string RunId, DateTimeOffset RunStartedAt, DateTimeOffset ObservedAt,
        long? InputTokens = null, long? CompletedOutputTokens = null, long CurrentOutputTokens = 0,
        long? CachedPromptTokensLastTurn = null);

    internal static bool HasCanonicalSections(ResearchWorkingState state) => state.Items.Any(item =>
        item.Kind == "section" && item.OwnerAgentId is null && Text(item.Data, "status") != "withdrawn"
        && !string.IsNullOrWhiteSpace(Text(item.Data, "contentMarkdown")));

    private static bool IsCanonicalDraft(ResearchWorkingState state) => state.Items.Any(item => item.Kind == "section"
        && (Text(item.Data, "status") is not ("completed" or "supported" or "verified" or "refuted" or "withdrawn" or "openLimit")
            || LinkedScientificItems(item, state).Any(link => Text(link.Data, "status") is "refuted" or "blocked" or "unresolved")));

    private static async Task<ResearchWorkingState> ImportLegacyManuscriptAsync(IScientificResearchStateRepository repository,
        ResearchWorkingState state, ChatMessage? manuscript, ResearchStoredReport? report,
        IReadOnlyList<ResearchLiteratureEntry> works, CancellationToken token)
    {
        var content = manuscript?.Content;
        string title;
        string body;
        if (content is null || !TryReadArticle(content, out title, out body))
        {
            // An unfinished chat manuscript must not hide a stored scientific
            // report. Historical reports also used administrative headings such
            // as "Zwischenstand", which remain disallowed for ordinary chats.
            if (report?.ReportKind != "scientificMarkdown") return state;
            content = report.ContentMarkdown;
            if (!TryReadArticle(content, out title, out body))
            {
                var reportLines = content.Replace("\r", "", StringComparison.Ordinal).Trim().Split('\n');
                var hasHeading = reportLines[0].StartsWith("# ", StringComparison.Ordinal);
                var storedHeading = hasHeading ? reportLines[0][2..].Trim() : "";
                var candidate = Regex.Replace(storedHeading, @"^Zwischenstand(?:\s+(?:der|des|zur|zum|zu))?\s*[:–—-]?\s*",
                    "", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                title = candidate != storedHeading && IsArticleTitle(candidate)
                    ? candidate : "Wissenschaftlicher Forschungsstand";
                body = CleanScientificContent(string.Join('\n', reportLines.Skip(hasHeading ? 1 : 0)));
                if (!body.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#')))
                    return state;
            }
        }
        // Split only real level-two headings outside code fences. Existing prose and formulas stay intact.
        // Replayed migration uses the same operation; subsequent section edits are authoritative.
        var changes = new List<ResearchWorkingChange>();
        var heading = "Einleitung";
        var lines = new List<string>();
        char fence = '\0';
        foreach (var line in body.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                fence = fence == '\0' ? trimmed[0] : fence == trimmed[0] ? '\0' : fence;
            if (fence == '\0' && line.StartsWith("## ", StringComparison.Ordinal))
            {
                AppendSection(); heading = line[3..].Trim();
            }
            else lines.Add(line);
        }
        AppendSection();
        if (changes.Count == 0) return state;
        var result = await repository.ApplyWorkingUpdateAsync(state.ProjectId, "legacy-manuscript-" + Fingerprint(content)[..24],
            null, title, changes, token).ConfigureAwait(false);
        return result.State;

        void AppendSection()
        {
            var scientificText = string.Join('\n', lines).Trim();
            lines.Clear();
            if (scientificText.Length == 0) return;
            var data = JsonSerializer.SerializeToElement(new { title = heading, contentMarkdown = scientificText,
                status = "unresolved", order = changes.Count,
                sourceIds = works.Where(work => !string.IsNullOrWhiteSpace(work.CanonicalUrl)
                    && scientificText.Contains(work.CanonicalUrl, StringComparison.OrdinalIgnoreCase)).Select(work => work.WorkId).ToArray() });
            changes.Add(new("legacy-section-" + (changes.Count + 1).ToString("D3", CultureInfo.InvariantCulture), "section", 0, data));
        }
    }

    internal static string FormatCanonicalPublication(ResearchWorkingState state,
        IReadOnlyList<ResearchLiteratureEntry> works, ResearchResultSnapshot results)
    {
        var sections = state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null
            && Text(item.Data, "status") != "withdrawn" && !string.IsNullOrWhiteSpace(Text(item.Data, "contentMarkdown")))
            .OrderBy(item => Number(item.Data, "order")).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (sections.Length == 0) throw new InvalidOperationException("Noch kein fachlicher Publikationsabschnitt vorhanden.");
        if (string.IsNullOrWhiteSpace(state.Title)) throw new ScientificPublicationContentException(
            "Der fachliche Publikationstitel fehlt. Ergänze nur den Titel mit research.update.");
        var text = new StringBuilder().Append("# ").AppendLine(OneLine(state.Title)).AppendLine()
            .Append("**Missum · Claude Science**  \n").Append(IsCanonicalDraft(state) ? "Arbeitsfassung" : "Forschungsstand").Append(" · Revision ")
            .AppendLine(state.PublicationRevision.ToString(CultureInfo.InvariantCulture)).AppendLine();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var citedBySection = sections.ToDictionary(section => section.Id, section => SectionSources(section, state), StringComparer.Ordinal);
        var sourceNumbers = works.Where(work => citedBySection.Values.Any(ids => ids.Contains(work.WorkId)))
            .OrderBy(work => work.WorkId, StringComparer.Ordinal).Select((work, index) => (work.WorkId, Number: index + 1))
            .ToDictionary(item => item.WorkId, item => item.Number, StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var title = Text(section.Data, "title");
            text.Append("## ").AppendLine(OneLine(string.IsNullOrWhiteSpace(title) ? section.Id : title)).AppendLine();
            var stateLabel = Text(section.Data, "status") switch
            {
                "refuted" => "Widerlegter Ansatz – zur fachlichen Einordnung erhalten.",
                "superseded" => "Durch einen späteren Ansatz ersetzt.",
                "openLimit" or "blocked" => "Offene Nachweisgrenze.",
                "supported" or "completed" or "verified" => "Dokumentierter Forschungsstand; der jeweilige Nachweisumfang gilt.",
                _ => "Vorläufiger Forschungsstand – fachlich noch nicht abschließend geprüft.",
            };
            text.Append("> ").AppendLine(stateLabel).AppendLine();
            var affected = LinkedScientificItems(section, state).Where(item => Text(item.Data, "status") is "refuted" or "blocked" or "unresolved")
                .ToArray();
            if (affected.Length > 0)
            {
                text.AppendLine("> **Einordnung offen:** Dieser Abschnitt bezieht sich auf nachträglich widerlegte oder noch ungeklärte Aussagen und muss entsprechend gelesen werden.").AppendLine();
                foreach (var item in affected)
                {
                    var statement = Text(item.Data, "statement");
                    if (statement.Length > 0) text.Append("> - ").Append(OneLine(statement)).Append(" — ").AppendLine(Status(Text(item.Data, "status")));
                }
                text.AppendLine();
            }
            var body = Text(section.Data, "contentMarkdown").Trim();
            if (!section.Id.StartsWith("legacy-section-", StringComparison.Ordinal) && Regex.IsMatch(body, @"!\[[^\r\n]*\]\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                throw new ScientificPublicationContentException($"Abschnitt {section.Id}: ordne Abbildungen über figureCaptions mit experimentId und artifactPath zu; contentMarkdown enthält nur fachlichen Text und Formeln.");
            text.AppendLine(body).AppendLine();
            if (section.Data.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array && units.GetArrayLength() > 0)
            {
                foreach (var unit in units.EnumerateArray())
                    text.Append("- $").Append(Text(unit, "symbol")).Append("$ ist ").Append(Text(unit, "meaning"))
                        .Append(" in $").Append(Text(unit, "unit")).AppendLine("$.");
                text.AppendLine();
            }
            if (section.Data.TryGetProperty("figureCaptions", out var figures) && figures.ValueKind == JsonValueKind.Array)
            {
                foreach (var figure in figures.EnumerateArray())
                {
                    var experimentId = Text(figure, "experimentId");
                    var path = Text(figure, "artifactPath").Replace('\\', '/');
                    var experiment = results.Experiments.FirstOrDefault(item => item.Id == experimentId
                        && Ids(section.Data, "experimentIds").Contains(item.Id));
                    if (experiment is null || !MeasuredImageHashes(experiment).ContainsKey(path))
                        throw new ScientificPublicationContentException($"Abschnitt {section.Id}: Abbildung {path} besitzt keinen zugeordneten erfolgreichen Ausführungsbeleg. Korrigiere nur figureCaptions/experimentIds.");
                    text.Append("![").Append(EscapeLabel(Text(figure, "caption"))).Append("](")
                        .Append(path.Replace(" ", "%20", StringComparison.Ordinal)).AppendLine(")").AppendLine();
                }
            }
            var sectionSources = citedBySection[section.Id].Where(sourceNumbers.ContainsKey).OrderBy(id => sourceNumbers[id]).ToArray();
            referenced.UnionWith(sectionSources);
            if (sectionSources.Length > 0)
                text.Append("*Quellen zu diesem Abschnitt: ")
                    .Append(string.Join(", ", sectionSources.Select(id => "[" + sourceNumbers[id].ToString(CultureInfo.InvariantCulture) + "]")))
                    .AppendLine(".*").AppendLine();
        }
        var sources = works.Where(work => referenced.Contains(work.WorkId)).OrderBy(work => work.WorkId, StringComparer.Ordinal).ToArray();
        if (sources.Length > 0)
        {
            text.AppendLine("## Literatur und Quellen").AppendLine();
            foreach (var work in sources)
            {
                text.Append('[').Append(sourceNumbers[work.WorkId]).Append("] **").Append(EscapeLabel(work.Title)).Append("**. ");
                if (Uri.TryCreate(work.CanonicalUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                    && string.IsNullOrEmpty(uri.UserInfo))
                    text.Append("[Originalquelle](").Append(uri.AbsoluteUri.Replace(")", "%29", StringComparison.Ordinal)).Append("). ");
                text.AppendLine().AppendLine();
            }
        }
        return text.ToString();
    }

    private static HashSet<string> SectionSources(ResearchWorkingItem section, ResearchWorkingState state)
    {
        var result = Ids(section.Data, "sourceIds").ToHashSet(StringComparer.Ordinal);
        foreach (var claimId in Ids(section.Data, "claimIds"))
        {
            var claim = state.Items.FirstOrDefault(item => item.Kind == "claim" && item.Id == claimId);
            if (claim is not null) result.UnionWith(Ids(claim.Data, "sourceIds"));
        }
        return result;
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
        var sourceIds = selected.SelectMany(item => Ids(item.Data, "sourceIds")).ToHashSet(StringComparer.Ordinal);
        foreach (var claimId in selected.SelectMany(item => Ids(item.Data, "claimIds")))
        {
            var claim = state.Items.FirstOrDefault(item => item.Kind == "claim" && item.Id == claimId);
            if (claim is not null) sourceIds.UnionWith(Ids(claim.Data, "sourceIds"));
        }
        var experimentIds = selected.SelectMany(item => Ids(item.Data, "experimentIds")).ToHashSet(StringComparer.Ordinal);
        return Fingerprint(JsonSerializer.Serialize(new
        {
            sources = snapshot.Works.Where(work => sourceIds.Contains(work.WorkId)).OrderBy(work => work.WorkId)
                .Select(work => new { work.WorkId, work.Title, work.CanonicalUrl }),
            experiments = snapshot.Results.Experiments.Where(experiment => experimentIds.Contains(experiment.Id)).OrderBy(experiment => experiment.Id)
                .Select(experiment => new { experiment.Id, experiment.StdoutEvidence, experiment.VerificationStatus }),
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
                    || Text(run, "runId").Length == 0 || Text(run, "snapshotId").Length == 0
                    || !run.TryGetProperty("inputHashes", out var inputs) || inputs.ValueKind != JsonValueKind.Object
                    || !run.TryGetProperty("outputHashes", out var output) || output.ValueKind != JsonValueKind.Object) continue;
                foreach (var property in output.EnumerateObject())
                {
                    var path = property.Name;
                    if (property.Value.ValueKind != JsonValueKind.String || !path.StartsWith("artifacts/", StringComparison.Ordinal)
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
        // Imported historical text keeps its original figure handling; it cannot establish a new successful verification.
        if (sections.All(section => section.Id.StartsWith("legacy-section-", StringComparison.Ordinal))) return;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
        {
            var referenced = snapshot.Results.Experiments.Where(experiment => Ids(section.Data, "experimentIds").Contains(experiment.Id)).ToArray();
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
            throw new ScientificPublicationContentException("Die Abbildungen stimmen nicht mit den gespeicherten Ausführungsbelegen überein. Prüfe figureCaptions/experimentIds des betroffenen Abschnitts und erzeuge fehlende oder veränderte Ergebnisse erneut; die vorherige PDF bleibt erhalten.");
    }

    internal async Task<ScientificPublicationArtifact?> RestoreLastPublicationAsync(string projectId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cached = _current.Values.Where(item => item.ProjectId == projectId).OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (cached is not null && File.Exists(cached.MarkdownPath) && await IsValidPdfAsync(cached.PdfPath, token).ConfigureAwait(false)) return cached;
            var project = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
            if (project is null) return null;
            var workspace = _sandbox is null ? project.WorkspacePath
                : (await _sandbox.EnsureProjectAsync(projectId, token).ConfigureAwait(false)).RootPath;
            var output = string.IsNullOrWhiteSpace(workspace) ? _defaultDirectory : Path.Combine(workspace, "publications");
            var root = Path.Combine(output, Fingerprint(projectId)[..24]);
            if (!Directory.Exists(root)) return null;
            foreach (var directory in Directory.EnumerateDirectories(root, "r*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                token.ThrowIfCancellationRequested();
                var pdf = Path.Combine(directory, "Publikation.pdf");
                var markdown = Path.Combine(directory, "Publikation.md");
                if (!File.Exists(markdown) || !await IsValidPdfAsync(pdf, token).ConfigureAwait(false)) continue;
                var revisionText = Path.GetFileName(directory).Split('-')[0][1..];
                if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)) continue;
                return new(projectId, revision, markdown, pdf, true, new DateTimeOffset(File.GetLastWriteTimeUtc(pdf)),
                    Fingerprint(await File.ReadAllTextAsync(markdown, token).ConfigureAwait(false)), SectionDelta: false);
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    private async Task RecordFirstPublicationAsync(ScientificResearchProject project,
        ScientificPublicationArtifact artifact, CancellationToken token)
    {
        var root = Directory.GetParent(Path.GetDirectoryName(artifact.PdfPath)!)!.FullName;
        var path = Path.Combine(root, "publication-metrics.json");
        if (File.Exists(path)) { _recordedPublicationMetrics.TryAdd(project.Id, 0); return; }
        var firstPdfAt = new DateTimeOffset(File.GetLastWriteTimeUtc(artifact.PdfPath));
        var firstPdfPath = artifact.PdfPath;
        // Backfill an older successful revision once if the project predates these metrics.
        // The persisted first success then stays unchanged across rendering and restarts.
        foreach (var directory in Directory.EnumerateDirectories(root, "r*", SearchOption.TopDirectoryOnly))
        {
            var candidate = Path.Combine(directory, "Publikation.pdf");
            if (!File.Exists(candidate)) continue;
            var writtenAt = new DateTimeOffset(File.GetLastWriteTimeUtc(candidate));
            if (writtenAt >= firstPdfAt || !await IsValidPdfAsync(candidate, token).ConfigureAwait(false)) continue;
            firstPdfAt = writtenAt;
            firstPdfPath = candidate;
        }
        _publicationTokenSamples.TryGetValue(project.Id, out var sample);
        var measured = sample is { CompletedOutputTokens: not null } && sample.RunStartedAt <= firstPdfAt && sample.ObservedAt <= firstPdfAt;
        var metrics = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, projectId = project.Id, firstPdfAt,
            projectCreatedAt = project.CreatedAt,
            elapsedSinceProjectCreatedMs = Math.Max(0, (firstPdfAt - project.CreatedAt).TotalMilliseconds),
            firstPdf = Path.GetRelativePath(root, firstPdfPath).Replace('\\', '/'),
            tokenMeasurement = measured && sample is { } tokens ? new
            {
                tokens.RunId, tokens.ObservedAt, tokens.InputTokens,
                generatedTokens = tokens.CompletedOutputTokens + tokens.CurrentOutputTokens,
                tokens.CachedPromptTokensLastTurn,
                source = "Gateway cumulative completed turns plus latest measured in-flight generation; main agent",
            } : null,
        });
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, metrics, new UTF8Encoding(false), token).ConfigureAwait(false);
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { } // Another renderer recorded the same project's first success.
            _recordedPublicationMetrics.TryAdd(project.Id, 0);
            _publicationTokenSamples.TryRemove(project.Id, out _);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsContentRenderError(Exception exception) => exception.Message.Contains("KaTeX", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("mathematische Ausdrücke", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("nicht lesbare Abbildungen", StringComparison.OrdinalIgnoreCase);

    private static string SectionRepairDiagnostic(ResearchWorkingState state, string error)
    {
        var sections = state.Items.Where(item => item.Kind == "section" && Text(item.Data, "status") != "withdrawn").ToArray();
        var located = sections.Where(item => Text(item.Data, "title") is { Length: > 0 } title
            && error.Contains(title, StringComparison.Ordinal)).Select(item => item.Id).ToArray();
        var candidates = located.Length > 0 ? located : sections.Where(item =>
            Text(item.Data, "contentMarkdown").Contains('$') || Text(item.Data, "contentMarkdown").Contains("![", StringComparison.Ordinal))
            .Select(item => item.Id).ToArray();
        return error + " Betroffene Formel-/Abbildungsabschnitte eingrenzen: " + string.Join(", ", candidates)
            + ". Korrigiere nur den fehlerhaften Abschnitt mit research.update; die vorherige PDF bleibt erhalten.";
    }

    internal static string Text(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static double Number(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var number) ? number : 0;
    internal static IReadOnlyList<string> Ids(JsonElement data, string property) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
}

internal sealed class ScientificPublicationContentException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

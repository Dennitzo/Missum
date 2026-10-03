using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>
/// Produces immutable, typeset publication snapshots from durable research data.
/// A new revision never replaces the PDF currently open in the native viewer.
/// </summary>
public sealed partial class ScientificPublicationService : IDisposable, IScientificPublicationProvider
{
    private readonly IScientificResearchRepository _repository;
    private readonly Func<string, CancellationToken, Task<string?>> _render;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _defaultDirectory;
    private readonly IChatRepository? _chats;
    private readonly IMissumAiRunRepository? _runs;
    private readonly IResearchSandboxService? _sandbox;
    private readonly Dictionary<string, ScientificPublicationArtifact> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentDependencies = new(StringComparer.Ordinal);

    public async Task<ScientificPublicationExportSnapshot?> EnsurePublicationAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ScientificPublicationArtifact? artifact;
        try { artifact = await EnsureCurrentAsync(projectId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException)
        {
            // Export the same last valid edition the publication view retains on a render failure.
            artifact = await RestoreLastPublicationAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (artifact is null) throw;
        }
        artifact ??= await RestoreLastPublicationAsync(projectId, cancellationToken).ConfigureAwait(false);
        return artifact is null ? null : new(artifact.ProjectId, artifact.Revision, artifact.MarkdownPath,
            artifact.PdfPath, artifact.ContentHash, artifact.IsDraft);
    }

    internal async Task<ResearchWorkingState?> GetWorkingStateAsync(string projectId, CancellationToken token, bool canonicalOnly = false)
    {
        if (canonicalOnly && (await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false)) is not { ProtocolVersion: >= 2 }) return null;
        return _repository is IScientificResearchStateRepository state
            ? await state.LoadWorkingStateAsync(projectId, token).ConfigureAwait(false) : null;
    }

    public async Task<ResearchWorkingState?> EnsureWorkingStateAsync(string projectId, CancellationToken token = default)
    {
        var snapshot = await ReadSnapshotAsync(projectId, token).ConfigureAwait(false);
        if (snapshot is null) return null;
        var state = snapshot.WorkingState;
        if (snapshot.Project.ProtocolVersion < 2 && state is not null
            && state.Items.All(item => item.Kind is "hypothesis" or "claim")
            && _repository is IScientificResearchStateRepository states)
            return await ImportLegacyManuscriptAsync(states, state, snapshot.Manuscript, snapshot.Report, snapshot.Works, token).ConfigureAwait(false);
        return state;
    }

    public ScientificPublicationService(IScientificResearchRepository repository, DocumentPdfExporter renderer,
        IChatRepository? chats = null, IMissumAiRunRepository? runs = null, IResearchSandboxService? sandbox = null)
        : this(repository, (path, token) => renderer.EnsureCurrentAsync(path, sourceChanged: true, scientificPublication: true, cancellationToken: token),
            Path.Combine(AssistantRuntimeProfile.Resolve().DataDirectory, "ResearchPublications"), chats, runs, sandbox) { }

    internal ScientificPublicationService(IScientificResearchRepository repository,
        Func<string, CancellationToken, Task<string?>> render, string defaultDirectory,
        IChatRepository? chats = null, IMissumAiRunRepository? runs = null, IResearchSandboxService? sandbox = null)
    {
        _repository = repository;
        _render = render;
        _defaultDirectory = Path.GetFullPath(defaultDirectory);
        _chats = chats;
        _runs = runs;
        _sandbox = sandbox;
    }

    public Task<ScientificPublicationArtifact?> EnsureCurrentAsync(string projectId,
        CancellationToken cancellationToken = default) => EnsureCurrentAsync(projectId, _defaultDirectory, cancellationToken);

    /// <returns>The current publication, or null if the project changed during rendering. The next refresh retries.</returns>
    public async Task<ScientificPublicationArtifact?> EnsureCurrentAsync(string projectId, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await ReadSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return null;
            // An already running protocol-one attempt keeps its streaming manuscript contract.
            // Migration is explicit at the next protocol upgrade, never a side effect of rendering.
            if (snapshot.Project.ProtocolVersion < 2) snapshot = snapshot with { WorkingState = null };
            if (string.Equals(Path.GetFullPath(outputDirectory), _defaultDirectory, StringComparison.OrdinalIgnoreCase))
            {
                var workspace = _sandbox is null ? snapshot.Project.WorkspacePath
                    : (await _sandbox.EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false)).RootPath;
                if (!string.IsNullOrWhiteSpace(workspace)) outputDirectory = Path.Combine(Path.GetFullPath(workspace), "publications");
            }
            var cacheKey = projectId + "\n" + Path.GetFullPath(outputDirectory);
            var dependencyHash = snapshot.WorkingState is null ? "" : CanonicalDependencyFingerprint(snapshot);
            if (snapshot.WorkingState is { } state)
            {
                if (!HasCanonicalSections(state)) return null;
                if (_current.TryGetValue(cacheKey, out var cached) && cached.SectionDelta
                    && cached.Revision == state.PublicationRevision && File.Exists(cached.MarkdownPath)
                    && _currentDependencies.GetValueOrDefault(cacheKey) == dependencyHash
                    && await IsValidPdfAsync(cached.PdfPath, cancellationToken).ConfigureAwait(false))
                {
                    await RecordFirstPublicationAsync(snapshot.Project, cached, cancellationToken).ConfigureAwait(false);
                    return cached;
                }
            }
            var markdown = snapshot.WorkingState is { } canonical
                ? FormatCanonicalPublication(canonical, snapshot.Works, snapshot.Results)
                : FormatPublication(snapshot.Project, snapshot.Results, snapshot.Works, snapshot.Evidence, snapshot.Report, snapshot.Manuscript);
            ScientificPublicationImages.PreparedImages? images = null;
            if (_sandbox is not null)
            {
                images = await ScientificPublicationImages.PrepareAsync(markdown, projectId, _sandbox,
                    snapshot.WorkingState is null ? PublicationRunStart(snapshot.Report) ?? snapshot.Manuscript?.CreatedAt
                        : DateTimeOffset.MinValue, cancellationToken).ConfigureAwait(false);
                if (snapshot.WorkingState is not null) ValidateCanonicalImages(snapshot, images);
                markdown = images.Markdown;
            }
            var fingerprint = PublicationFingerprint(markdown + (dependencyHash.Length == 0 ? "" : "\n" + dependencyHash));
            // Project ids originate in storage but are never accepted as filesystem paths.
            var root = Path.Combine(Path.GetFullPath(outputDirectory), Fingerprint(projectId)[..24]);
            var version = $"r{(snapshot.WorkingState?.PublicationRevision ?? snapshot.Project.Revision).ToString(CultureInfo.InvariantCulture)}-{fingerprint[..24]}";
            var directory = Path.Combine(root, version);
            var pdf = Path.Combine(directory, "Publikation.pdf");
            var source = Path.Combine(directory, "Publikation.md");
            if (await IsValidPdfAsync(pdf, cancellationToken).ConfigureAwait(false)
                && File.Exists(source) && string.Equals(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false), markdown, StringComparison.Ordinal))
            {
                _currentDependencies[cacheKey] = dependencyHash;
                var existing = Artifact(snapshot, source, pdf, fingerprint);
                await RecordFirstPublicationAsync(snapshot.Project, existing, cancellationToken).ConfigureAwait(false);
                return _current[cacheKey] = existing;
            }

            Directory.CreateDirectory(root);
            var staging = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var pendingSource = Path.Combine(staging, "Publikation.md");
                if (images is not null) await images.WriteImagesAsync(staging, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(pendingSource, markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                string? rendered;
                try { rendered = await _render(pendingSource, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (snapshot.WorkingState is not null && IsContentRenderError(exception))
                {
                    throw new ScientificPublicationContentException(SectionRepairDiagnostic(snapshot.WorkingState, exception.Message), exception);
                }
                if (rendered is null || !await IsValidPdfAsync(rendered, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("Die wissenschaftliche Publikation konnte nicht als gültiges PDF erzeugt werden.");

                // Results may arrive without a revision bump. Compare all rendered content,
                // not only the project row, before exposing a newly generated artifact.
                var current = await ReadSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (current is null || !CanPublishSnapshot(snapshot, current))
                    return null;
                cancellationToken.ThrowIfCancellationRequested();
                var pendingPdf = Path.Combine(staging, "Publikation.pdf");
                if (!string.Equals(Path.GetFullPath(rendered), pendingPdf, StringComparison.OrdinalIgnoreCase))
                    File.Copy(rendered, pendingPdf, overwrite: false);
                Directory.CreateDirectory(directory);
                if (images is not null) await images.WriteImagesAsync(directory, cancellationToken).ConfigureAwait(false);
                // The PDF is published last; a cancelled or failed render cannot replace
                // a usable previous revision, or leave a PDF paired with partial source.
                File.Move(pendingSource, source, overwrite: true);
                File.Move(pendingPdf, pdf, overwrite: true);
                _currentDependencies[cacheKey] = dependencyHash;
                var artifact = Artifact(snapshot, source, pdf, fingerprint);
                await RecordFirstPublicationAsync(snapshot.Project, artifact, cancellationToken).ConfigureAwait(false);
                return _current[cacheKey] = artifact;
            }
            finally
            {
                if (string.Equals(Path.GetDirectoryName(staging), root, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(staging).StartsWith(".pending-", StringComparison.Ordinal)
                    && Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<PublicationSnapshot?> ReadSnapshotAsync(string projectId, CancellationToken token)
    {
        var project = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is null) return null;
        var results = await _repository.LoadResultSnapshotAsync(projectId, token).ConfigureAwait(false);
        var archive = await _repository.LoadArchiveSnapshotAsync(projectId, token).ConfigureAwait(false);
        var manuscript = await ReadManuscriptAsync(project, archive.Report, token).ConfigureAwait(false);
        var state = await GetWorkingStateAsync(projectId, token).ConfigureAwait(false);
        var current = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (state is null && current != project) return null;
        return new(project, results, archive.Works, archive.Evidence, archive.Report, manuscript, state);
    }

    private static DateTimeOffset? PublicationRunStart(ResearchStoredReport? report)
    {
        if (string.IsNullOrWhiteSpace(report?.ManifestJson)) return null;
        try
        {
            using var manifest = JsonDocument.Parse(report.ManifestJson);
            return manifest.RootElement.ValueKind == JsonValueKind.Object
                && manifest.RootElement.TryGetProperty("runStartedAt", out var start)
                && start.ValueKind == JsonValueKind.String && start.TryGetDateTimeOffset(out var value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<ChatMessage?> ReadManuscriptAsync(ScientificResearchProject project, ResearchStoredReport? report, CancellationToken token)
    {
        if (_chats is null || _runs is null || report is null || string.IsNullOrWhiteSpace(report.ManifestJson)) return null;
        JsonElement manifest;
        try { manifest = JsonSerializer.Deserialize<JsonElement>(report.ManifestJson); }
        catch (JsonException) { return null; }
        if (manifest.ValueKind != JsonValueKind.Object) return null;
        var runId = manifest.TryGetProperty("runId", out var runIdValue) && runIdValue.ValueKind == JsonValueKind.String ? runIdValue.GetString() : null;
        if (string.IsNullOrWhiteSpace(runId)) return null;
        var run = manifest.TryGetProperty("localRunId", out var localId) && localId.ValueKind == JsonValueKind.String && localId.TryGetGuid(out var id)
            ? await _runs.GetAsync(id, token).ConfigureAwait(false)
            : await _runs.GetByServerRunIdAsync(runId, token).ConfigureAwait(false);
        // A retried run keeps its message id. Only the exact server attempt enrolled
        // in the current archive is allowed to supply this publication's manuscript.
        if (run is null || run.SessionId != project.SessionId || run.ServerRunId != runId) return null;
        var message = await _chats.GetMessageAsync(run.AssistantMessageId, token).ConfigureAwait(false);
        return message is { Role: ChatRole.Assistant } && message.SessionId == project.SessionId ? message : null;
    }

    private static bool CanPublishSnapshot(PublicationSnapshot snapshot, PublicationSnapshot current)
    {
        if (snapshot.WorkingState is { } state)
            return current.WorkingState?.PublicationRevision == state.PublicationRevision
                && CanonicalDependencyFingerprint(snapshot) == CanonicalDependencyFingerprint(current);
        var source = FormatPublication(snapshot.Project, snapshot.Results, snapshot.Works, snapshot.Evidence, snapshot.Report);
        var latest = FormatPublication(current.Project, current.Results, current.Works, current.Evidence, current.Report);
        if (!string.Equals(source, latest, StringComparison.Ordinal)) return false;
        if (snapshot.Manuscript is null && current.Manuscript is null) return true;
        if (snapshot.Manuscript is { } previous && current.Manuscript is { } present
            && previous.Id == present.Id && previous.Status == present.Status
            && previous.UpdatedAt == present.UpdatedAt && previous.Content == present.Content) return true;
        // A progressing answer must not starve the PDF renderer: an immutable,
        // clearly marked draft can show the earlier prefix of this exact run.
        // Replacements/retries and edits to existing text are never accepted.
        return snapshot.Manuscript is { Status: MessageStatus.Streaming or MessageStatus.Pending } prior
            && current.Manuscript is { } next && prior.Id == next.Id
            && next.Content.StartsWith(prior.Content, StringComparison.Ordinal);
    }

    internal static string FormatPublication(ScientificResearchProject project, ResearchResultSnapshot results,
        IReadOnlyList<ResearchLiteratureEntry> works, IReadOnlyList<ResearchEvidenceRecord> evidence, ResearchStoredReport? report,
        ChatMessage? manuscript = null)
    {
        var draft = IsDraft(project, report, manuscript);
        var manuscriptText = manuscript?.Content ?? (report?.ReportKind == "scientificMarkdown" ? report.ContentMarkdown : null);
        if (manuscriptText is not null && TryReadArticle(manuscriptText, out var articleTitle, out var articleBody))
            return FormatArticle(project, results, works, evidence, manuscript, draft, articleTitle, articleBody);
        var text = new StringBuilder();
        AppendPublicationHeading(text, "Wissenschaftliche Untersuchung", project, manuscript, draft);

        text.AppendLine("## Zusammenfassung").AppendLine();
        if (results.Claims.Count > 0)
        {
            foreach (var claim in results.Claims.Where(claim => !IsOperationalParagraph(claim.Statement)).Take(3))
                text.AppendLine(claim.Statement).AppendLine();
            text.Append("Aussagenstatus: ").AppendLine(Status(report?.ConclusionStatus ?? project.Status)).AppendLine();
        }
        else
            text.AppendLine(draft
                ? "Die Forschungsfrage und die bisher verfügbaren Belege werden im Folgenden dokumentiert. Eine geprüfte Ergebnissynthese liegt noch nicht vor."
                : "Der dokumentierte Bericht wird nachfolgend wiedergegeben. Darüber hinaus liegen keine separat gespeicherten, geprüften Kernaussagen vor.").AppendLine();

        text.AppendLine("## Material und Methode").AppendLine()
            .Append("Der gespeicherte Forschungsstand umfasst ").Append(works.Count).Append(" Quellen, ").Append(evidence.Count)
            .Append(" Belegstellen und ").Append(results.Experiments.Count).AppendLine(" rechnerische Untersuchungen. Quellen und Prüfstatus werden getrennt ausgewiesen.").AppendLine();
        if (results.Hypotheses.Count > 0)
        {
            text.AppendLine("### Untersuchte Hypothesen").AppendLine();
            foreach (var hypothesis in results.Hypotheses.Where(hypothesis => !IsOperationalParagraph(hypothesis.Statement)))
                text.Append("- ").Append(hypothesis.Statement).Append(" — ").AppendLine(Status(hypothesis.Status));
            text.AppendLine();
        }

        text.AppendLine("## Ergebnisse").AppendLine();
        if (!string.IsNullOrWhiteSpace(manuscript?.Content))
            text.AppendLine(RemoveReportPreamble(CleanScientificContent(manuscript.Content))).AppendLine();
        else if (report is not null && report.ReportKind != "researchProgress" && !string.IsNullOrWhiteSpace(report.ContentMarkdown))
            text.AppendLine(RemoveReportPreamble(CleanScientificContent(report.ContentMarkdown))).AppendLine();
        else if (results.Claims.Count == 0)
            text.AppendLine("Die Auswertung ist noch offen. Es wird noch kein Ergebnis behauptet.").AppendLine();
        if (results.Claims.Count > 0)
        {
            text.AppendLine("### Dokumentierte Kernaussagen").AppendLine();
            foreach (var claim in results.Claims.Where(claim => !IsOperationalParagraph(claim.Statement)))
                text.Append("- ").Append(claim.Statement).Append("  \n  **Prüfstatus:** ").AppendLine(Status(claim.ConclusionStatus));
            text.AppendLine();
        }
        // Process output and stderr are operational receipts. The manuscript
        // interprets scientific numerical results; raw tool logs remain in chat.
        if (draft && works.Count > 0)
        {
            text.AppendLine("### Bisherige Beleggrundlage").AppendLine()
                .AppendLine("Die folgenden Originalauszüge dienen der weiteren Auswertung; ihr Inhalt wird hier nicht als gesichertes Ergebnis übernommen.").AppendLine();
            foreach (var item in evidence.Take(8))
            {
                var sourceIndex = works.Select((work, index) => (work, index)).FirstOrDefault(pair => pair.work.WorkId == item.WorkId);
                if (sourceIndex.work is null) continue;
                var excerpt = item.ExactExcerpt.Length > 700 ? item.ExactExcerpt[..700] + " … [Auszug gekürzt]" : item.ExactExcerpt;
                text.Append("**Beleg [").Append(sourceIndex.index + 1).AppendLine("]**").AppendLine();
                foreach (var line in excerpt.Replace("\r", "", StringComparison.Ordinal).Split('\n')) text.Append("> ").AppendLine(line);
                text.AppendLine();
            }
        }

        text.AppendLine("## Einordnung und Grenzen").AppendLine();
        if (draft) text.AppendLine("Diese Arbeitsfassung ist unvollständig. Aussagen können sich durch zusätzliche Quellen, Gegenbeispiele und unabhängige Prüfungen ändern.").AppendLine();
        var verified = results.Verifications.Count(item => item.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
            || item.Status.Equals("KernelAccepted", StringComparison.OrdinalIgnoreCase));
        text.Append(verified).Append(" von ").Append(results.Verifications.Count).AppendLine(" gespeicherten Prüfungen sind als erfolgreich bestätigt. Numerische Simulationen illustrieren das jeweilige Modell; sie sind für sich allein kein Beweis.").AppendLine();
        if (works.Count == 0) text.AppendLine("Für diesen Stand sind noch keine Originalquellen gespeichert.").AppendLine();

        text.AppendLine("## Literatur und Quellen").AppendLine();
        for (var index = 0; index < works.Count; index++)
        {
            var work = works[index];
            text.Append('[').Append(index + 1).Append("] **").Append(EscapeLabel(work.Title)).Append("**. ");
            if (Uri.TryCreate(work.CanonicalUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo))
                text.Append("[Originalquelle](").Append(uri.AbsoluteUri.Replace(")", "%29", StringComparison.Ordinal)).Append("). ");
            text.Append("Abruf/Stand: ").Append(work.UpdatedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture))
                .Append(". Belegstatus: ").Append(Status(work.EvidenceLevel ?? work.ScreeningStatus)).AppendLine(".").AppendLine();
        }
        if (works.Count == 0) text.AppendLine("Noch keine Quellen verfügbar.").AppendLine();
        return text.ToString();
    }

    private static void AppendPublicationHeading(StringBuilder text, string title, ScientificResearchProject project, ChatMessage? manuscript, bool draft)
    {
        text.Append("# ").AppendLine(OneLine(title)).AppendLine();
        text.Append("**Missum · Claude Science**  \n")
            .Append(draft ? "Arbeitsfassung" : "Forschungsbericht").Append(" · Revision ").Append(project.Revision)
            .Append(" · ").AppendLine(PublicationUpdatedAt(project, manuscript).ToUniversalTime().ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)).AppendLine();
        text.AppendLine(draft
            ? "> **Vorläufige Publikation.** Die Untersuchung wird fortlaufend ergänzt. Gelesene Quellen, Hypothesen und numerische Ergebnisse sind keine bestätigten Schlussfolgerungen."
            : "> Dieser Bericht dokumentiert den gespeicherten Forschungsstand und ersetzt keine unabhängige wissenschaftliche Begutachtung.").AppendLine();
    }

    private static string FormatArticle(ScientificResearchProject project, ResearchResultSnapshot results,
        IReadOnlyList<ResearchLiteratureEntry> works, IReadOnlyList<ResearchEvidenceRecord> evidence, ChatMessage? manuscript,
        bool draft, string title, string body)
    {
        var text = new StringBuilder();
        AppendPublicationHeading(text, title, project, manuscript, draft);
        text.AppendLine(body.Trim()).AppendLine();
        var missingSources = works.Where(work => string.IsNullOrWhiteSpace(work.CanonicalUrl)
            || !body.Contains(work.CanonicalUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missingSources.Length > 0)
        {
            text.AppendLine("## Ergänzende Originalquellen").AppendLine();
            foreach (var work in missingSources)
            {
                text.Append("- **").Append(EscapeLabel(work.Title)).Append("**. ");
                if (Uri.TryCreate(work.CanonicalUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo))
                    text.Append("[Originalquelle](").Append(uri.AbsoluteUri.Replace(")", "%29", StringComparison.Ordinal)).Append("). ");
                text.Append("Belegstatus: ").Append(Status(work.EvidenceLevel ?? work.ScreeningStatus)).AppendLine(".");
            }
            text.AppendLine();
        }
        var verified = results.Verifications.Count(item => item.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)
            || item.Status.Equals("KernelAccepted", StringComparison.OrdinalIgnoreCase));
        text.AppendLine("---").AppendLine().Append("**Dokumentationsstand:** ").Append(works.Count).Append(" gespeicherte Quellen · ")
            .Append(evidence.Count).Append(" Belegstellen · ").Append(verified).Append(" von ").Append(results.Verifications.Count)
            .AppendLine(" Prüfungen bestätigt. Numerische Simulationen illustrieren das jeweilige Modell; sie sind für sich allein kein Beweis.");
        return text.ToString();
    }

    private static bool TryReadArticle(string content, out string title, out string body)
    {
        title = body = "";
        var lines = content.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var headings = new List<(int Index, int Level, string Title)>();
        var starts = new List<int>();
        var ends = new List<int>();
        char fence = '\0';
        var fenceLength = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fence == '\0')
                {
                    fence = line[0];
                    fenceLength = line.TakeWhile(character => character == fence).Count();
                }
                else if (line[0] == fence && line.Length >= fenceLength && line.All(character => character == fence)) fence = '\0';
                continue;
            }
            if (fence != '\0') continue;
            if (line == "<!-- MISSUM_PUBLICATION_BEGIN -->") starts.Add(index);
            if (line == "<!-- MISSUM_PUBLICATION_END -->") ends.Add(index);
            var level = line.TakeWhile(static character => character == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && line[level] == ' ')
                headings.Add((index, level, line[(level + 1)..].Trim().TrimEnd('#').TrimEnd()));
        }
        // New snapshots explicitly separate the article from chat. During
        // streaming keep the latest usable block; an incomplete new title must
        // not replace an earlier complete article with an empty document.
        foreach (var start in starts.AsEnumerable().Reverse())
        {
            var end = ends.Concat(starts).Where(index => index > start).DefaultIfEmpty(lines.Length).Min();
            var heading = headings.FirstOrDefault(item => item.Index > start && item.Index < end
                && item.Level == 1 && IsArticleTitle(item.Title));
            if (heading.Title is null) continue;
            var article = CleanScientificContent(string.Join('\n', lines[(heading.Index + 1)..end]));
            if (!article.Split('\n').Any(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))) continue;
            title = heading.Title;
            body = article;
            return true;
        }
        if (starts.Count > 0) return false;

        // Older answers placed the actual manuscript after a long research
        // narration. Prefer that explicit subsection instead of exporting the
        // surrounding chat, completed-tool receipts and subsequent recap.
        var section = headings.LastOrDefault(item => Regex.IsMatch(item.Title,
            @"^(?:Publikation|Manuskript|Wissenschaftliche Publikation)(?:\s|$|[–—:-])", RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1)));
        if (section.Title is not null)
        {
            var next = headings.FirstOrDefault(item => item.Index > section.Index && item.Level <= section.Level);
            var end = next.Title is null ? lines.Length : next.Index;
            title = headings.LastOrDefault(item => item.Index < section.Index && item.Level == 1 && IsArticleTitle(item.Title)).Title
                ?? "Wissenschaftliche Untersuchung";
            var articleLines = lines[(section.Index + 1)..end];
            if (section.Level > 1)
                articleLines = articleLines.Select((line, index) => headings.Any(heading => heading.Index == section.Index + 1 + index
                    && heading.Level > section.Level) ? line.TrimStart()[(section.Level - 1)..] : line).ToArray();
            body = CleanScientificContent(string.Join('\n', articleLines));
            return !string.IsNullOrWhiteSpace(body);
        }
        var first = headings.FirstOrDefault(item => item.Level == 1 && IsArticleTitle(item.Title));
        if (first.Title is null) return false;
        title = first.Title;
        body = CleanScientificContent(string.Join('\n', lines.Skip(first.Index + 1)));
        return !string.IsNullOrWhiteSpace(body);
    }

    private static bool IsArticleTitle(string title) => !string.IsNullOrWhiteSpace(title)
        && !IsOperationalParagraph(title) && !Regex.IsMatch(title,
            @"^(?:Deep Research|Publikation|Manuskript|Fortsetzung|Zwischenstand|Weitermachen|Erstelle|Untersuche|Analysiere)(?:\s|$|[–—:-])",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static bool IsOperationalParagraph(string paragraph) => Regex.IsMatch(paragraph,
        @"SearXNG|host\.docker\.internal|MISSUM_|web\.deepResearch|research\.code\.|SHA-?256|\b(?:HttpRequestException|TimeoutException|Traceback|Network is unreachable|too many requests|unresponsive_engines)\b|HTTP\s+(?:429|5\d\d)\b|\b(?:Schreibwerkzeug|Ausführungswerkzeug|Werkzeugaufruf|Toolaufruf|Nutzerprompt|Rechercheauftrag|Fortschrittsmeldung)\b|PDF-Darstellung wird|Missum erzeugt|(?:^|\n)\s*(?:\*\*)?(?:Nächster Schritt|Ich werde|Ich beginne|Ich fordere|Ich recherchiere|Ich erstelle|Ich führe|Hier folgt|Konkret werde ich)\b",
        RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static string CleanScientificContent(string content)
    {
        var kept = new List<string>();
        var sourceGap = false;
        var calculationGap = false;
        foreach (var paragraph in ReadMarkdownBlocks(content))
        {
            if (IsOperationalParagraph(paragraph))
            {
                sourceGap |= Regex.IsMatch(paragraph, @"nicht (?:abgerufen|gelesen|quellenbelegt)|keine Originalquelle|nicht erfolgreich|unvollständig",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                calculationGap |= Regex.IsMatch(paragraph, @"nicht ausgeführt|nicht ausführbar|kein.{0,30}research\.code\.execute",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                continue;
            }
            kept.Add(paragraph);
        }
        if (sourceGap || calculationGap)
        {
            kept.Add("## Nachweisgrenzen");
            if (sourceGap) kept.Add("Die unabhängige Quellenprüfung ist für die betroffenen Aussagen noch offen; diese gelten nicht als durch Originalbelege bestätigt.");
            if (calculationGap) kept.Add("Eine unabhängige rechnerische Bestätigung der betroffenen Ergebnisse steht noch aus.");
        }
        return string.Join("\n\n", kept);
    }

    private static IEnumerable<string> ReadMarkdownBlocks(string content)
    {
        // Keep code fences intact when removing a diagnostic block. Splitting
        // blindly at blank lines can leave half a fence and swallow later math.
        var block = new List<string>();
        char fence = '\0';
        var fenceLength = 0;
        foreach (var line in content.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (fence == '\0' && string.IsNullOrWhiteSpace(line))
            {
                if (block.Count > 0) { yield return string.Join('\n', block).Trim(); block.Clear(); }
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (fence == '\0')
                {
                    fence = trimmed[0];
                    fenceLength = trimmed.TakeWhile(character => character == fence).Count();
                }
                else if (trimmed[0] == fence && trimmed.Length >= fenceLength && trimmed.All(character => character == fence)) fence = '\0';
            }
            block.Add(line);
        }
        if (block.Count > 0) yield return string.Join('\n', block).Trim();
    }

    private static bool IsDraft(ScientificResearchProject project, ResearchStoredReport? report, ChatMessage? manuscript = null) =>
        report is null || report.ReportKind == "researchProgress" || project.Status is "planned" or "active"
        || manuscript is { Status: not MessageStatus.Completed };

    private static DateTimeOffset PublicationUpdatedAt(ScientificResearchProject project, ChatMessage? manuscript) =>
        manuscript is not null && manuscript.UpdatedAt > project.UpdatedAt ? manuscript.UpdatedAt : project.UpdatedAt;

    private static ScientificPublicationArtifact Artifact(PublicationSnapshot snapshot, string source, string pdf, string fingerprint) =>
        new(snapshot.Project.Id, snapshot.WorkingState?.PublicationRevision ?? snapshot.Project.Revision, source, pdf,
            snapshot.WorkingState is { } state ? IsCanonicalDraft(state) : IsDraft(snapshot.Project, snapshot.Report, snapshot.Manuscript),
            snapshot.WorkingState?.UpdatedAt ?? PublicationUpdatedAt(snapshot.Project, snapshot.Manuscript), fingerprint,
            snapshot.WorkingState is not null);

    private static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string PublicationFingerprint(string text) => Fingerprint("scientific-publication-v7\n" + text);
    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();
    private static string EscapeLabel(string text) => OneLine(text).Replace("*", "\\*", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);
    private static string Status(string status) => status switch
    {
        "verified" or "KernelAccepted" => "bestätigt",
        "supported" => "durch Belege gestützt",
        "provisionallySupported" => "vorläufig gestützt",
        "unresolved" or "awaitingReview" => "noch ungeprüft",
        "refuted" => "widerlegt",
        "blocked" => "Prüfung offen",
        "cancelled" => "abgebrochen",
        "failed" => "fehlgeschlagen",
        "retrievedExcerpt" => "gelesener Originalauszug, noch ungeprüft",
        "verifiedExcerpt" => "geprüfter Originalauszug",
        "fullTextExcerpt" => "Auszug aus dem Volltext",
        "active" => "in Bearbeitung",
        "planned" => "geplant",
        _ => status,
    };

    private static string RemoveReportPreamble(string report)
    {
        var lines = report.Replace("\r", "", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[0].Trim() == "# Deep Research") lines.RemoveAt(0);
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[0]) || lines[0].StartsWith("**Status:**", StringComparison.Ordinal))) lines.RemoveAt(0);
        // The report's own sections remain readable inside the results section.
        return string.Join('\n', lines.Select(line => line.StartsWith("## ", StringComparison.Ordinal) ? "#" + line : line));
    }

    private static async Task<bool> IsValidPdfAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 1024) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        return header.AsSpan().SequenceEqual("%PDF-"u8);
    }

    public void Dispose() => _gate.Dispose();

    private sealed record PublicationSnapshot(ScientificResearchProject Project, ResearchResultSnapshot Results,
        IReadOnlyList<ResearchLiteratureEntry> Works, IReadOnlyList<ResearchEvidenceRecord> Evidence, ResearchStoredReport? Report,
        ChatMessage? Manuscript, ResearchWorkingState? WorkingState = null);
}

public sealed record ScientificPublicationArtifact(string ProjectId, long Revision, string MarkdownPath, string PdfPath,
    bool IsDraft, DateTimeOffset UpdatedAt, string ContentHash, bool SectionDelta = false);

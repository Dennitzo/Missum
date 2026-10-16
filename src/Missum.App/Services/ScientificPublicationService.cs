using System.Collections.Concurrent;
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
/// Publishes the current typeset manuscript at one stable project location.
/// A failed render preserves the last usable PDF; successful renders replace it.
/// </summary>
public sealed partial class ScientificPublicationService : IDisposable, IScientificPublicationProvider
{
    private readonly IScientificResearchRepository _repository;
    private readonly Func<string, CancellationToken, Task<string?>> _render;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _defaultDirectory;
    private readonly IChatRepository? _chats;
    private readonly IMissumAiRunRepository? _runs;
    private readonly Dictionary<string, ScientificPublicationArtifact> _current = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WorkspaceDocument> _workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.Ordinal);

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

    // Read-only compatibility for importing publications created before file-based authoring.
    public Task<ResearchWorkingState?> EnsureWorkingStateAsync(string projectId, CancellationToken token = default) =>
        GetWorkingStateAsync(projectId, token);

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
        _ = sandbox; // Kept as a source-compatible constructor argument; publication rendering never creates a sandbox.
    }

    public Task<ScientificPublicationArtifact?> EnsureCurrentAsync(string projectId,
        CancellationToken cancellationToken = default) => EnsureCurrentAsync(projectId, _defaultDirectory, cancellationToken);

    /// <summary>Associates a session with its directly editable publication directory.</summary>
    public void RegisterWorkspace(string projectId, string workspace, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        if (!Regex.IsMatch(projectId, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            throw new ArgumentException("Ungültige Projektkennung.", nameof(projectId));
        var root = Path.GetFullPath(workspace);
        if (_workspaces.TryGetValue(projectId, out var previous)
            && !string.Equals(previous.Directory, root, StringComparison.OrdinalIgnoreCase)) _documents.TryRemove(projectId, out _);
        if (File.Exists(Path.Combine(root, "Publikation.md")) && _documents.TryGetValue(projectId, out var known)
            && !string.Equals(known, Path.Combine(root, "Publikation.md"), StringComparison.OrdinalIgnoreCase)) _documents.TryRemove(projectId, out _);
        _workspaces[projectId] = new(root, title);
    }

    public async Task<string> EnsureWorkspaceDocumentAsync(string projectId, string workspace, string? title = null,
        CancellationToken cancellationToken = default)
    {
        RegisterWorkspace(projectId, workspace, title);
        return await EnsureProjectDocumentAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Der Publikationsordner ist nicht verfügbar.");
    }

    public async Task<string?> EnsureProjectDocumentAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ResolveDocumentAsync(projectId, _defaultDirectory, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Renders the current Markdown file. Database state is never written back over authored text.</summary>
    public async Task<ScientificPublicationArtifact?> EnsureCurrentAsync(string projectId, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var source = await ResolveDocumentAsync(projectId, outputDirectory, cancellationToken).ConfigureAwait(false);
            if (source is null) return null;
            var raw = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var directory = Path.GetDirectoryName(source)!;
            var projectRoot = _workspaces.TryGetValue(projectId, out var registered) ? registered.Directory : directory;
            var images = await ScientificPublicationImages.PrepareDocumentAsync(PreparePublicationProse(raw), directory,
                projectRoot, cancellationToken).ConfigureAwait(false);
            var fingerprint = PublicationFingerprint(raw + "\n" + string.Join('\n', images.Images.Select(image => image.Sha256)));
            var pdf = Path.ChangeExtension(source, ".pdf");
            var artifact = new ScientificPublicationArtifact(projectId, 0, source, pdf, false,
                new DateTimeOffset(File.GetLastWriteTimeUtc(source)), fingerprint);
            var receiptPath = Path.Combine(directory, "publication.json");
            if (await IsDirectPublicationCurrentAsync(receiptPath, pdf, fingerprint, cancellationToken).ConfigureAwait(false))
                return _current[projectId] = artifact;

            // This temporary rendering directory is deleted immediately; it is not a project revision.
            // The editable source and the previous readable PDF remain in place on render failure.
            var staging = Path.Combine(directory, ".missum-publication-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                var pendingSource = Path.Combine(staging, "Publikation.md");
                await File.WriteAllTextAsync(pendingSource, images.Markdown, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                await images.WriteImagesAsync(staging, cancellationToken).ConfigureAwait(false);
                var rendered = await _render(pendingSource, cancellationToken).ConfigureAwait(false);
                if (rendered is null || !await IsValidPdfAsync(rendered, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("Die Publikation konnte nicht als gültige PDF erzeugt werden.");
                // An AI tool may replace the file while the renderer is busy. Never publish a stale render.
                if (!string.Equals(await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false), raw, StringComparison.Ordinal)) return null;
                var latestImages = await ScientificPublicationImages.PrepareDocumentAsync(PreparePublicationProse(raw), directory,
                    projectRoot, cancellationToken).ConfigureAwait(false);
                if (!latestImages.Images.Select(image => image.Sha256).SequenceEqual(images.Images.Select(image => image.Sha256))) return null;
                var preparedPdf = Path.Combine(directory, ".publication-" + Guid.NewGuid().ToString("N") + ".tmp.pdf");
                try
                {
                    File.Copy(rendered, preparedPdf);
                    File.Move(preparedPdf, pdf, overwrite: true);
                }
                finally { if (File.Exists(preparedPdf)) File.Delete(preparedPdf); }
                var receipt = JsonSerializer.Serialize(new { fingerprint,
                    markdownSha256 = Fingerprint(raw), pdfSha256 = await PublicationFileHashAsync(pdf, cancellationToken).ConfigureAwait(false) });
                var pendingReceipt = receiptPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(pendingReceipt, receipt, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    File.Move(pendingReceipt, receiptPath, overwrite: true);
                }
                finally { if (File.Exists(pendingReceipt)) File.Delete(pendingReceipt); }
                return _current[projectId] = artifact;
            }
            catch (Exception exception) when (IsContentRenderError(exception))
            {
                throw new ScientificPublicationContentException(exception.Message + " Bearbeite die betroffene Formel oder Überschrift direkt in "
                    + source + ". Die bisherige PDF bleibt erhalten.", exception);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        finally { _gate.Release(); }
    }

    private async Task<string?> ResolveDocumentAsync(string projectId, string outputDirectory, CancellationToken token)
    {
        // A deliberately removed manuscript is not silently resurrected from historical database rows.
        if (_documents.TryGetValue(projectId, out var known)) return File.Exists(known) ? known : null;
        ScientificResearchProject? project = null;
        if (!_workspaces.TryGetValue(projectId, out var workspace))
        {
            project = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
            if (project is null) return null;
            var root = project.WorkspacePath;
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetFullPath(outputDirectory), Fingerprint(projectId)[..24]);
            workspace = new(Path.GetFullPath(root), null);
            _workspaces[projectId] = workspace;
        }
        Directory.CreateDirectory(workspace.Directory);
        var source = FindExistingDocument(workspace.Directory, projectId);
        if (source is not null) { _documents[projectId] = source; return source; }
        // Respect historical project bindings even when the UI supplied the parent coding workspace.
        project ??= await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is not null)
        {
            var legacyRoot = project.WorkspacePath;
            if (!string.IsNullOrWhiteSpace(legacyRoot) && FindExistingDocument(Path.GetFullPath(legacyRoot), projectId) is { } legacySource)
            {
                _workspaces[projectId] = new(Path.GetFullPath(legacyRoot), workspace.Title);
                _documents[projectId] = legacySource;
                return legacySource;
            }
        }
        // Compatibility import happens once, only when no editable publication already exists.
        string markdown;
        if (project is not null && await ReadSnapshotAsync(projectId, token).ConfigureAwait(false) is { } snapshot)
            markdown = snapshot.WorkingState is { } state && HasCanonicalSections(state)
                ? FormatCanonicalPublication(state, snapshot.Works, snapshot.Results, snapshot.TrustedChecks)
                : FormatPublication(project, snapshot.Results, snapshot.Works, snapshot.Evidence, snapshot.Report, snapshot.Manuscript);
        else markdown = "# " + OneLine(string.IsNullOrWhiteSpace(workspace.Title) ? "Publikation" : workspace.Title) + "\n\n";
        source = Path.Combine(workspace.Directory, "Publikation.md");
        var pendingSource = Path.Combine(workspace.Directory, ".publication-source-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(pendingSource, markdown, new UTF8Encoding(false), token).ConfigureAwait(false);
            try { File.Move(pendingSource, source, overwrite: false); }
            catch (IOException) when (File.Exists(source)) { } // A coding tool created the authoritative file first.
        }
        finally { if (File.Exists(pendingSource)) File.Delete(pendingSource); }
        _documents[projectId] = source;
        return source;
    }

    private static string? FindExistingDocument(string workspace, string? projectId = null)
    {
        var direct = Path.Combine(workspace, "Publikation.md");
        if (File.Exists(direct)) return direct;
        var publications = Path.Combine(workspace, "publications");
        if (Directory.Exists(publications))
        {
            var current = Directory.EnumerateDirectories(publications).Select(directory => Path.Combine(directory, "current", "Publikation.md"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (current is not null) return current;
        }
        // Read-only compatibility until an existing project is flattened; never create this tree.
        if (projectId is not null && Regex.IsMatch(projectId, @"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var legacy = Path.Combine(workspace, "Science", projectId);
            if (Directory.Exists(legacy)) return FindExistingDocument(legacy);
        }
        return null;
    }

    internal static string PreparePublicationProse(string markdown)
    {
        // Existing explanations remain authoritative. Raw formal syntax is a project artifact,
        // not a readable proof explanation; do not invent an interpretation or claim validity.
        markdown = Regex.Replace(markdown, @"(?m)^(?<label>\*\*Missum[^\r\n]*?)(?: · Revision \d+)", "${label}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        markdown = Regex.Replace(markdown, @"(?m)^(?<label>Arbeitsfassung|Geprüfter Forschungsstand|Forschungsbericht) · Revision \d+", "${label}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            var opening = Regex.Match(lines[index], @"^[ ]{0,3}(?<fence>`{3,}|~{3,})(?<language>[^\s]*)[^\r\n]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!opening.Success) { result.AppendLine(lines[index]); continue; }
            var marker = opening.Groups["fence"].Value;
            var end = index + 1;
            for (; end < lines.Length; end++)
            {
                var trimmed = lines[end].Trim();
                if (trimmed.Length >= marker.Length && trimmed.All(character => character == marker[0])) break;
            }
            var language = opening.Groups["language"].Value;
            if (language.Equals("lean", StringComparison.OrdinalIgnoreCase) || language.Equals("lean4", StringComparison.OrdinalIgnoreCase))
                result.AppendLine("> Redaktioneller Hinweis: Der hier im Manuskript enthaltene Lean-Quelltext ist als mathematischer Beweis mit Voraussetzungen, nachvollziehbaren Zwischenschritten und Schlussfolgerung auszuarbeiten.").AppendLine();
            else if (end >= lines.Length) { result.AppendLine(string.Join('\n', lines[index..])); break; }
            else result.AppendLine(string.Join('\n', lines[index..(end + 1)]));
            index = end;
        }
        return result.ToString();
    }

    private static string EscapeProofReference(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("*", "\\*", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal);

    private static async Task<bool> IsDirectPublicationCurrentAsync(string receiptPath, string pdf, string fingerprint, CancellationToken token)
    {
        try
        {
            if (!File.Exists(receiptPath) || !await IsValidPdfAsync(pdf, token).ConfigureAwait(false)) return false;
            using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath, token).ConfigureAwait(false));
            return Text(receipt.RootElement, "fingerprint") == fingerprint
                && Text(receipt.RootElement, "pdfSha256") == await PublicationFileHashAsync(pdf, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }

    private sealed record WorkspaceDocument(string Directory, string? Title);

    private async Task<PublicationSnapshot?> ReadSnapshotAsync(string projectId, CancellationToken token)
    {
        var project = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is null) return null;
        var results = await _repository.LoadResultSnapshotAsync(projectId, token).ConfigureAwait(false);
        var archive = await _repository.LoadArchiveSnapshotAsync(projectId, token).ConfigureAwait(false);
        var manuscript = await ReadManuscriptAsync(project, archive.Report, token).ConfigureAwait(false);
        var state = await GetWorkingStateAsync(projectId, token).ConfigureAwait(false);
        var trustedChecks = _repository is IScientificResearchStateRepository states
            ? await states.LoadExecutionVerificationsAsync(projectId, token).ConfigureAwait(false) : [];
        trustedChecks = await CompleteLeanReceiptsAsync(project, trustedChecks, token).ConfigureAwait(false);
        var current = await _repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (state is null && current != project) return null;
        return new(project, results, archive.Works, archive.Evidence, archive.Report, manuscript, state, trustedChecks);
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
        text.Append(verified).Append(" von ").Append(results.Verifications.Count).AppendLine(" gespeicherten Prüfungen sind als erfolgreich bestätigt. Numerische Simulationen können die angegebenen Gleichungen anhand ihrer Berechnungsdaten prüfen; dies bestätigt nicht automatisch physikalische Modellannahmen.").AppendLine();
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
            .AppendLine(" Prüfungen bestätigt. Numerische Simulationen können die angegebenen Gleichungen anhand ihrer Berechnungsdaten prüfen; dies bestätigt nicht automatisch physikalische Modellannahmen.");
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
            snapshot.WorkingState is { } state ? IsCanonicalDraft(state, snapshot.TrustedChecks, snapshot.Results) : IsDraft(snapshot.Project, snapshot.Report, snapshot.Manuscript),
            snapshot.WorkingState?.UpdatedAt ?? PublicationUpdatedAt(snapshot.Project, snapshot.Manuscript), fingerprint,
            snapshot.WorkingState is not null);

    private static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    // Renderer-only changes refresh the current edition without changing
    // the scientific manuscript, object revisions or publication revision.
    private static string PublicationFingerprint(string text) => Fingerprint("scientific-publication-v11-mathematical-proof-exposition-linked-table-of-contents\n" + text);

    private static async Task<string> PublicationFileHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static async Task<bool> IsPublicationPairAsync(string directory, string? fingerprint, CancellationToken token)
    {
        var pdf = Path.Combine(directory, "Publikation.pdf");
        var source = Path.Combine(directory, "Publikation.md");
        var receipt = Path.Combine(directory, "publication.json");
        try
        {
            if (!File.Exists(source) || !File.Exists(receipt) || !await IsValidPdfAsync(pdf, token).ConfigureAwait(false)) return false;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, token).ConfigureAwait(false));
            var pair = document.RootElement;
            return (fingerprint is null || Text(pair, "fingerprint") == fingerprint)
                && Text(pair, "markdownSha256") == Fingerprint(await File.ReadAllTextAsync(source, token).ConfigureAwait(false))
                && Text(pair, "pdfSha256") == await PublicationFileHashAsync(pdf, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return false; }
    }
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
        ChatMessage? Manuscript, ResearchWorkingState? WorkingState = null, IReadOnlyList<ResearchVerification>? TrustedChecks = null);
}

public sealed record ScientificPublicationArtifact(string ProjectId, long Revision, string MarkdownPath, string PdfPath,
    bool IsDraft, DateTimeOffset UpdatedAt, string ContentHash, bool SectionDelta = false);

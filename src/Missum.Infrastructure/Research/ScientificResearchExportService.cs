using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Research;

namespace Missum.Infrastructure.Research;

public sealed class ScientificResearchExportService(
    IScientificResearchRepository repository,
    IDocumentFileCodec documents,
    IScientificPublicationProvider? publicationProvider = null) : IScientificResearchExportService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<ScientificResearchExportBundle> ExportAllAsync(string projectId, string workspacePath,
        CancellationToken cancellationToken = default)
    {
        var project = await repository.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project.WorkspacePath ?? "")), workspace,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Der Export-Workspace gehört nicht zum Forschungsprojekt.");
        var results = await repository.LoadResultSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
        var archive = await repository.LoadArchiveSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
        var workingState = project.ProtocolVersion >= 2 && repository is IScientificResearchStateRepository stateRepository
            ? await stateRepository.LoadWorkingStateAsync(projectId, cancellationToken).ConfigureAwait(false) : null;
        var publication = publicationProvider is null ? null
            : await publicationProvider.EnsurePublicationAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (publication is null || publication.ProjectId != projectId || !File.Exists(publication.PdfPath)
            || !File.Exists(publication.MarkdownPath))
            throw new InvalidOperationException("Noch keine gültige wissenschaftliche Publikation zum Exportieren vorhanden. Die PDF-Erstellung muss zuerst abgeschlossen sein.");
        var stamp = DateTimeOffset.UtcNow;
        var isResearchSandbox = string.Equals(Path.GetFileName(Path.GetDirectoryName(workspace)), "ResearchSandbox", StringComparison.OrdinalIgnoreCase);
        var exportRoot = isResearchSandbox
            ? Path.Combine(workspace, "artifacts", "exports")
            : Path.Combine(workspace, ".assistant", "research", projectId, "exports");
        var root = Path.Combine(exportRoot, stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        var markdown = await File.ReadAllTextAsync(publication.MarkdownPath, cancellationToken).ConfigureAwait(false);
        var files = new List<string>();
        await WriteAsync("report.md", markdown).ConfigureAwait(false);
        var title = markdown.Split('\n').FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim()
            ?? "Wissenschaftliche Untersuchung";
        await WriteAsync("report.tex", Latex(title, markdown)).ConfigureAwait(false);
        await WriteAsync("literature.csv", Csv(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.bib", BibTex(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.ris", Ris(archive.Works)).ConfigureAwait(false);
        await WriteAsync("bibliography.csl.json", JsonSerializer.Serialize(archive.Works.Select(Csl), Json)).ConfigureAwait(false);
        await WriteAsync("claim-evidence-graph.json", JsonSerializer.Serialize(new { project,
            claims = workingState is null ? JsonSerializer.SerializeToElement(results.Claims, Json)
                : JsonSerializer.SerializeToElement(workingState.Items.Where(item => item.Kind == "claim"), Json),
            archive.Evidence }, Json)).ConfigureAwait(false);
        if (workingState is not null)
            await WriteAsync("research-state.json", JsonSerializer.Serialize(workingState, Json)).ConfigureAwait(false);
        await WriteAsync("research-notebook.ipynb", Notebook(project, markdown, results.Experiments)).ConfigureAwait(false);
        await documents.WriteDocxAsync(markdown, Path.Combine(root, "report.docx"), cancellationToken).ConfigureAwait(false); files.Add("report.docx");
        await documents.WriteXlsxAsync(LiteratureTable(archive.Works), Path.Combine(root, "literature.xlsx"), cancellationToken).ConfigureAwait(false); files.Add("literature.xlsx");
        var pdfPath = Path.Combine(root, "report.pdf");
        File.Copy(publication.PdfPath, pdfPath, overwrite: false); files.Add("report.pdf");
        var figureRoot = Path.Combine(Path.GetDirectoryName(publication.MarkdownPath)!, "figures");
        if (Directory.Exists(figureRoot))
        {
            Directory.CreateDirectory(Path.Combine(root, "figures"));
            foreach (var image in Directory.EnumerateFiles(figureRoot).Where(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0) continue;
                var relative = "figures/" + Path.GetFileName(image);
                File.Copy(image, Path.Combine(root, relative), overwrite: false);
                files.Add(relative);
            }
        }
        var manifest = new
        {
            projectId, project.Revision, project.ProtocolVersion, project.Profile, project.VerificationLevel,
            publicationRevision = publication.PublicationRevision, publication.ContentHash, publication.IsDraft,
            createdAt = stamp, files = files.Order(StringComparer.Ordinal).Select(name => new
            {
                name, sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name)))),
            }),
        };
        var manifestPath = Path.Combine(root, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, Json), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        files.Add("manifest.json");
        return new(root, files, manifestPath, stamp);

        async Task WriteAsync(string name, string content)
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            files.Add(name);
        }
    }

    private static string Csv(IEnumerable<ResearchLiteratureEntry> works) =>
        "title,url,screeningStatus,evidenceLevel\r\n" + string.Join("\r\n", works.Select(work =>
            string.Join(',', Q(work.Title), Q(work.CanonicalUrl), Q(work.ScreeningStatus), Q(work.EvidenceLevel ?? "")))) + "\r\n";
    private static string Q(string value) => '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    private static string LiteratureTable(IEnumerable<ResearchLiteratureEntry> works) =>
        "Titel | URL | Screening | Evidenz\n" + string.Join('\n', works.Select(work => $"{work.Title} | {work.CanonicalUrl} | {work.ScreeningStatus} | {work.EvidenceLevel}"));
    private static string BibTex(IEnumerable<ResearchLiteratureEntry> works) => string.Join("\n\n", works.Select((work, index) =>
        $"@misc{{research{index + 1},\n  title = {{{work.Title.Replace("}", "\\}", StringComparison.Ordinal)}}},\n  url = {{{work.CanonicalUrl}}}\n}}"));
    private static string Ris(IEnumerable<ResearchLiteratureEntry> works) => string.Join("\r\n", works.Select(work =>
        $"TY  - GEN\r\nTI  - {work.Title}\r\nUR  - {work.CanonicalUrl}\r\nER  - \r\n"));
    private static object Csl(ResearchLiteratureEntry work) => new { id = work.WorkId, type = "article", title = work.Title, URL = work.CanonicalUrl };
    private static string Latex(string title, string markdown) =>
        "\\documentclass{article}\n\\usepackage[utf8]{inputenc}\n\\usepackage{hyperref}\n\\begin{document}\n\\section*{" + EscapeLatex(title) + "}\n\\begin{verbatim}\n" + markdown.Replace("\\end{verbatim}", "", StringComparison.Ordinal) + "\n\\end{verbatim}\n\\end{document}\n";
    private static string EscapeLatex(string value) => value.Replace("\\", "\\textbackslash{}", StringComparison.Ordinal).Replace("{", "\\{", StringComparison.Ordinal).Replace("}", "\\}", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("&", "\\&", StringComparison.Ordinal).Replace("#", "\\#", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    private static string Notebook(ScientificResearchProject project, string markdown, IReadOnlyList<ResearchExperiment> experiments) =>
        JsonSerializer.Serialize(new { nbformat = 4, nbformat_minor = 5, metadata = new { projectId = project.Id }, cells = new object[]
        {
            new { cell_type = "markdown", metadata = new { }, source = markdown.Split('\n').Select(static line => line + "\n") },
            new { cell_type = "markdown", metadata = new { }, source = experiments.Select(item => $"Experiment `{item.Id}`: {item.VerificationStatus}\n") },
        } }, Json);

}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed record ScientificSimulationArtifact(string Id, string Title, string ImagePath,
    string? ScriptPath, string? DataPath, string Provenance, bool IsResearchData, string Sha256);

public sealed record ScientificSimulationSnapshot(string ProjectId, long Revision, string Status,
    string Detail, IReadOnlyList<ScientificSimulationArtifact> Artifacts, DateTimeOffset UpdatedAt);

/// <summary>
/// Creates reproducible figures from persisted research data and discovers images made by the
/// scientific code tools. Automatic figures describe reported tables or the actual evidence base;
/// they never invent physical measurements or execute code extracted from a publication.
/// </summary>
public sealed partial class ScientificSimulationService(
    IScientificResearchRepository repository, IResearchSandboxService sandbox) : IDisposable
{
    private const int MaximumImages = 48;
    private const int MaximumScanEntries = 4096;
    private const long MaximumImageBytes = 24 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<ScientificSimulationSnapshot> RefreshAsync(string projectId, CancellationToken cancellationToken = default) =>
        RefreshAsync(projectId, publication: null, cancellationToken);

    public async Task<ScientificSimulationSnapshot> RefreshAsync(string projectId, ScientificPublicationArtifact? publication,
        CancellationToken cancellationToken = default)
    {
        // A report can advance while Python runs. Retry with fresh input, never publish figures
        // associated with a superseded question/run as the currently selected research.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RefreshCurrentAsync(projectId, publication, cancellationToken).ConfigureAwait(false);
            if (result.Status != "updating") return result;
        }
        return new(projectId, 0, "updating", "Die Forschungsdaten werden gerade ergänzt. Die aktuelle Darstellung wird neu berechnet.", [], DateTimeOffset.UtcNow);
    }

    private async Task<ScientificSimulationSnapshot> RefreshCurrentAsync(string projectId, ScientificPublicationArtifact? publication,
        CancellationToken cancellationToken)
    {
        ValidateId(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ScientificSimulationArtifact>();
        long revision = 0;
        string? inputFingerprint = null;
        try
        {
            var project = await repository.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
            revision = project.Revision;
            var archive = await repository.LoadArchiveSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            var initialFingerprint = ResearchFingerprint(project, archive);
            inputFingerprint = initialFingerprint;
            if (publication is not null && publication.ProjectId != project.Id)
                throw new InvalidDataException("Die Publikation gehört zu einem anderen Forschungsprojekt.");
            if (publication is not null && publication.Revision != project.Revision) return Superseded(projectId);
            var scope = RunScope(project, archive.Report);
            var result = await repository.LoadResultSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            var layout = await sandbox.EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            artifacts = await DiscoverExperimentImagesAsync(project, layout, result.Experiments, scope.StartedAt, cancellationToken).ConfigureAwait(false);
            var publicationMarkdown = publication is null ? null : await ReadPublicationMarkdownAsync(publication, cancellationToken).ConfigureAwait(false);
            var plotSource = publicationMarkdown ?? archive.Report?.ContentMarkdown ?? "";
            var sourceHash = Hash(Encoding.UTF8.GetBytes(plotSource));
            var tables = ExtractNumericTables(plotSource);
            var dataset = new
            {
                schemaVersion = 1,
                projectId,
                runId = scope.RunId,
                runStartedAt = scope.StartedAt,
                question = project.InterpretedQuestion,
                reportId = archive.Report?.Id,
                reportSha256 = Hash(Encoding.UTF8.GetBytes(archive.Report?.ContentMarkdown ?? "")),
                sourceKind = publication is null ? "researchArchive" : "publishedManuscript",
                publicationPath = publication?.MarkdownPath,
                publicationContentHash = publication?.ContentHash,
                sourceSha256 = sourceHash,
                tables,
                // These are counts of saved research records, not estimated scientific results.
                sourceCount = archive.Works.Count,
                evidenceCount = archive.Evidence.Count,
                sources = archive.Works.Select(work => new { work.WorkId, work.Title, work.CanonicalUrl }),
                evidence = archive.Evidence.Select(item => new { item.Id, item.WorkId, item.ContentHash }),
                sourceEvidenceCounts = archive.Works.Select(work => new { label = work.Title,
                    value = archive.Evidence.Count(item => item.WorkId == work.WorkId) }).Take(24).ToArray(),
            };
            var json = JsonSerializer.Serialize(dataset, JsonOptions);
            var key = Hash(Encoding.UTF8.GetBytes(PublicationPlotScript + "\n" + json))[..24];
            var relative = "simulations/publication-" + key;
            var outputRelative = "publication/" + key;
            var output = SafePath(layout.ArtifactsPath, outputRelative);
            var manifest = SafePath(output, "manifest.json");
            if (!File.Exists(manifest))
            {
                await sandbox.WriteTextAsync(projectId, relative + "/data.json", json, cancellationToken: cancellationToken).ConfigureAwait(false);
                await sandbox.WriteTextAsync(projectId, relative + "/render.py", PublicationPlotScript,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var execution = await sandbox.RunPythonAsync(projectId, relative + "/render.py",
                    ["/sandbox/work/" + relative + "/data.json", "/sandbox/artifacts/" + outputRelative],
                    timeoutSeconds: 120, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (execution.ExitCode != 0 || execution.TimedOut)
                    return await FinishAsync("failed", ExecutionError(execution)).ConfigureAwait(false);
            }
            var provenance = publication is null ? "Publikation " + archive.Report?.Id + " · Tabelle aus dem gespeicherten Bericht"
                : "Publiziertes Manuskript · Revision " + publication.Revision + " · SHA-256 " + sourceHash[..12];
            var generated = await ReadPlotManifestAsync(layout, manifest, relative, provenance, cancellationToken).ConfigureAwait(false);
            if (generated.Count == 0)
                return await FinishAsync("failed", "Die Python-Ausgabe enthält keine gültige Abbildung; die Quelldaten bleiben gespeichert.").ConfigureAwait(false);
            artifacts.AddRange(generated);
            return await FinishAsync("ready", tables.Count > 0
                ? "Python-Abbildungen aus den gespeicherten Publikationsdaten. Skript und Quelldaten bleiben nachvollziehbar."
                : "Evidenzübersicht aus gespeicherten Quellen. Fachliche Simulationen erscheinen hier, sobald die Forschung sie erzeugt.").ConfigureAwait(false);

            async Task<ScientificSimulationSnapshot> FinishAsync(string status, string detail) =>
                await IsCurrentResearchAsync(projectId, initialFingerprint, cancellationToken).ConfigureAwait(false)
                    && (publication is null || publicationMarkdown == await ReadPublicationMarkdownAsync(publication, cancellationToken).ConfigureAwait(false))
                    ? Snapshot(project, artifacts, status, detail) : Superseded(projectId);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                                         or System.ComponentModel.Win32Exception or JsonException)
        {
            if (inputFingerprint is not null && !await IsCurrentResearchAsync(projectId, inputFingerprint, cancellationToken).ConfigureAwait(false))
                return Superseded(projectId);
            return new(projectId, revision, "failed", "Python-Darstellung konnte nicht aktualisiert werden: " + exception.Message,
                artifacts, DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Runs model-selected Python in the network-isolated scientific Docker runner. The code can
    /// use MISSUM_ARTIFACTS for images; open matplotlib figures are also saved automatically.
    /// Host execution is never a fallback. The source and run record remain in the project sandbox.
    /// </summary>
    public async Task<ScientificSimulationSnapshot> RunAsync(string projectId, string pythonSource, string title,
        CancellationToken cancellationToken = default)
    {
        ValidateId(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pythonSource);
        if (pythonSource.Length > 256_000) throw new ArgumentException("Das Simulationsskript ist zu groß.", nameof(pythonSource));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = await repository.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
            var archive = await repository.LoadArchiveSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            var inputFingerprint = ResearchFingerprint(project, archive);
            var layout = await sandbox.EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N");
            var relative = "simulations/" + id;
            var sourceChange = await sandbox.WriteTextAsync(projectId, relative + "/simulation.py", pythonSource,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await sandbox.WriteTextAsync(projectId, relative + "/run.py", SimulationHarness,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var run = await sandbox.RunPythonAsync(projectId, relative + "/run.py",
                ["/sandbox/work/" + relative + "/simulation.py", "/sandbox/artifacts/simulations/" + id],
                timeoutSeconds: 300, relativeWorkingDirectory: relative, cancellationToken: cancellationToken).ConfigureAwait(false);
            var images = new List<ScientificSimulationArtifact>();
            if (run.ExitCode == 0 && !run.TimedOut)
                await AddImagesAsync(SafePath(layout.ArtifactsPath, "simulations/" + id), layout.ArtifactsPath,
                    title, SafePath(layout.WorkPath, relative + "/simulation.py"), null,
                    "Python-Simulation · " + run.RunId + " · Modellannahmen und Eingaben im Skript prüfen.", true,
                    images, cancellationToken).ConfigureAwait(false);
            var status = run.ExitCode == 0 && !run.TimedOut ? images.Count > 0 ? "ready" : "no-output" : "failed";
            var detail = status == "ready" ? "Python-Simulation abgeschlossen; Skript und Laufprotokoll gespeichert."
                : status == "no-output" ? "Python wurde ausgeführt, hat aber keine darstellbare PNG- oder JPEG-Abbildung erzeugt."
                : ExecutionError(run);
            await WriteExperimentAsync(project, title, relative, sourceChange.AfterSha256, run, images, layout, cancellationToken).ConfigureAwait(false);
            return await IsCurrentResearchAsync(projectId, inputFingerprint, cancellationToken).ConfigureAwait(false)
                ? Snapshot(project, images, status, detail) : Superseded(projectId);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteExperimentAsync(ScientificResearchProject project, string title, string relative, string sourceHash,
        ResearchSandboxRunResult run, IReadOnlyList<ScientificSimulationArtifact> artifacts,
        ResearchSandboxLayout layout, CancellationToken token)
    {
        await repository.SaveExperimentAsync(new("simulation-" + run.RunId, project.Id, ResearchSandboxService.RunnerImage,
            JsonSerializer.Serialize(new[] { SafePath(layout.WorkPath, relative + "/simulation.py") }), "[]",
            JsonSerializer.Serialize(new Dictionary<string, string> { [relative + "/simulation.py"] = sourceHash }),
            title + "\n" + run.Command, "{\"network\":\"none\",\"timeoutSeconds\":300}", run.StandardOutput,
            run.StandardError, JsonSerializer.Serialize(artifacts.Select(item => item.ImagePath)),
            run.ExitCode == 0 && !run.TimedOut ? "ProcessSucceeded" : "ProcessFailed", run.StartedAt, run.CompletedAt), token).ConfigureAwait(false);
    }

    private static async Task<List<ScientificSimulationArtifact>> DiscoverExperimentImagesAsync(
        ScientificResearchProject project, ResearchSandboxLayout layout, IReadOnlyList<ResearchExperiment> experiments,
        DateTimeOffset? notBefore, CancellationToken token)
    {
        var images = new List<ScientificSimulationArtifact>();
        foreach (var experiment in experiments.OrderByDescending(item => item.UpdatedAt))
        {
            if (experiment.VerificationStatus is "ProcessFailed" or "failed" or "cancelled") continue;
            foreach (var root in CandidateRoots(project, layout))
            {
                var scripts = JsonPaths(experiment.SourceFilesJson).Select(path => ResolveCandidate(root, path))
                    .Where(path => path is not null && File.Exists(path)).ToArray();
                foreach (var path in JsonPaths(experiment.ResultArtifactsJson))
                {
                    var candidate = ResolveCandidate(root, path);
                    if (candidate is null || !File.Exists(candidate) || !IsImage(candidate)) continue;
                    await AddImageAsync(candidate, root, Path.GetFileNameWithoutExtension(candidate), scripts.FirstOrDefault(), null,
                        "Forschungsexperiment · " + experiment.Id + " · " + experiment.VerificationStatus,
                        true, images, token, notBefore).ConfigureAwait(false);
                }
            }
        }
        // Existing research.code.execute manifests retain the code provenance but not image lists.
        // Discover only that project's generated files, never unrelated workspace images.
        if (!string.IsNullOrWhiteSpace(project.WorkspacePath) && Directory.Exists(project.WorkspacePath))
        {
            var root = SafePath(project.WorkspacePath, ".assistant/research/" + project.Id);
            await AddImagesAsync(root, project.WorkspacePath, "Forschungsabbildung", null, null,
                "Python-Forschungsbereich · " + project.Id + " · Ergebnisse und Annahmen im zugehörigen Experiment prüfen.",
                true, images, token, notBefore).ConfigureAwait(false);
        }
        await AddImagesAsync(layout.ArtifactsPath, layout.ArtifactsPath,
            "Python-Simulation", null, null, "Isolierter Python-Forschungsbereich · " + project.Id,
            true, images, token, notBefore).ConfigureAwait(false);
        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            if (image.ScriptPath is not null) continue;
            var modified = File.GetLastWriteTimeUtc(image.ImagePath);
            var experiment = experiments.Where(item => ExperimentContainsTimestamp(item, modified))
                .OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (experiment is not null && ScriptFromExperiment(experiment, layout) is { } script)
                images[index] = image with { ScriptPath = script,
                    Provenance = "Forschungsexperiment · " + experiment.Id + " · " + experiment.VerificationStatus };
        }
        return images;
    }

    private static bool ExperimentContainsTimestamp(ResearchExperiment experiment, DateTime modified)
    {
        try
        {
            if (experiment.StdoutEvidence.Length <= 256_000)
            {
                using var document = JsonDocument.Parse(experiment.StdoutEvidence);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                    return runs.EnumerateArray().Any(run => run.ValueKind == JsonValueKind.Object
                        && run.TryGetProperty("startedAt", out var start) && start.ValueKind == JsonValueKind.String
                        && start.TryGetDateTimeOffset(out var startedAt) && run.TryGetProperty("completedAt", out var end)
                        && end.ValueKind == JsonValueKind.String && end.TryGetDateTimeOffset(out var completedAt) && modified.AddSeconds(2) >= startedAt.UtcDateTime
                        && modified.AddSeconds(-2) <= completedAt.UtcDateTime);
            }
        }
        catch (JsonException) { }
        return experiment.CreatedAt.UtcDateTime <= modified.AddSeconds(2)
            && experiment.UpdatedAt.UtcDateTime >= modified.AddSeconds(-2);
    }

    private static string? ScriptFromExperiment(ResearchExperiment experiment, ResearchSandboxLayout layout)
    {
        var position = experiment.CommandText.IndexOf('{');
        if (position < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(experiment.CommandText[position..]);
            if (!doc.RootElement.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Array
                || arguments.GetArrayLength() == 0 || arguments[0].ValueKind != JsonValueKind.String) return null;
            var path = ResolveCandidate(layout.WorkPath, arguments[0].GetString()!);
            return path is not null && File.Exists(path) ? path : null;
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<string> CandidateRoots(ScientificResearchProject project, ResearchSandboxLayout layout)
    {
        yield return layout.RootPath;
        if (!string.IsNullOrWhiteSpace(project.WorkspacePath) && Directory.Exists(project.WorkspacePath))
            yield return project.WorkspacePath;
    }

    private static string[] JsonPaths(string json)
    {
        if (json.Length > 256_000) return [];
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).Take(128).ToArray() : [];
        }
        catch (JsonException) { return []; }
    }

    private static string? ResolveCandidate(string root, string path)
    {
        try { return SafePath(root, Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or ArgumentException or IOException) { return null; }
    }

    private static async Task AddImagesAsync(string directory, string root, string title, string? scriptPath,
        string? dataPath, string provenance, bool researchData, List<ScientificSimulationArtifact> images, CancellationToken token,
        DateTimeOffset? notBefore = null)
    {
        if (!Directory.Exists(directory)) return;
        var pending = new Stack<string>(); pending.Push(directory);
        var scanned = 0;
        while (pending.TryPop(out var current) && scanned < MaximumScanEntries && images.Count < MaximumImages)
        {
            token.ThrowIfCancellationRequested();
            var safe = Path.GetFullPath(current).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(SafePath(root, ".scan-marker")) : ResolveCandidate(root, current);
            if (safe is null) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(safe))
            {
                if (++scanned > MaximumScanEntries || images.Count >= MaximumImages) break;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (Path.GetFileName(entry) is not (".state" or ".venv" or "__pycache__" or "publication")) pending.Push(entry);
                }
                else if (IsImage(entry))
                    await AddImageAsync(entry, root, title + " · " + Path.GetFileNameWithoutExtension(entry),
                        scriptPath, dataPath, provenance, researchData, images, token, notBefore).ConfigureAwait(false);
            }
        }
    }

    private static async Task AddImageAsync(string path, string root, string title, string? scriptPath, string? dataPath,
        string provenance, bool researchData, List<ScientificSimulationArtifact> images, CancellationToken token,
        DateTimeOffset? notBefore = null)
    {
        if (images.Count >= MaximumImages || images.Any(item => item.ImagePath.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        path = SafePath(root, Path.GetRelativePath(root, path));
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumImageBytes) return;
        if (notBefore is { } beginning && file.LastWriteTimeUtc < beginning.UtcDateTime) return;
        await using var stream = File.OpenRead(path);
        var header = new byte[24];
        if (await stream.ReadAsync(header, token).ConfigureAwait(false) < 24) return;
        var png = header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = header[0] == 255 && header[1] == 216 && header[2] == 255;
        if (!png && !jpeg) return;
        if (png && (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) > 16000
                    || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) > 16000)) return;
        stream.Position = 0;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        images.Add(new(hash[..24], title, path, scriptPath, dataPath, provenance, researchData, hash));
    }

    private static async Task<IReadOnlyList<ScientificSimulationArtifact>> ReadPlotManifestAsync(ResearchSandboxLayout layout,
        string manifest, string relativeWork, string tableProvenance, CancellationToken token)
    {
        if (!File.Exists(manifest)) throw new IOException("Python hat kein Abbildungsverzeichnis geschrieben.");
        _ = SafePath(layout.ArtifactsPath, Path.GetRelativePath(layout.ArtifactsPath, manifest));
        if (new FileInfo(manifest).Length > 256_000) throw new InvalidDataException("Das Abbildungsverzeichnis ist zu groß.");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false));
        var artifacts = new List<ScientificSimulationArtifact>();
        foreach (var item in doc.RootElement.EnumerateArray().Take(12))
        {
            var file = item.GetProperty("file").GetString()!;
            var researchData = item.GetProperty("isResearchData").GetBoolean();
            await AddImageAsync(SafePath(Path.GetDirectoryName(manifest)!, file), layout.ArtifactsPath,
                item.GetProperty("title").GetString()!, SafePath(layout.WorkPath, relativeWork + "/render.py"),
                SafePath(layout.WorkPath, relativeWork + "/data.json"), researchData
                    ? tableProvenance + "; keine unabhängige Verifikation."
                    : "Gezählte Quellen und Belegstellen des Forschungsprojekts · keine fachliche Simulation.",
                researchData, artifacts, token).ConfigureAwait(false);
        }
        return artifacts;
    }

    private static async Task<string> ReadPublicationMarkdownAsync(ScientificPublicationArtifact publication, CancellationToken token)
    {
        var fullPath = Path.GetFullPath(publication.MarkdownPath);
        var safe = SafePath(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath));
        if (!Path.GetExtension(safe).Equals(".md", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Die Publikationsquelle ist keine Markdown-Datei.");
        if (new FileInfo(safe).Length > 8 * 1024 * 1024)
            throw new InvalidDataException("Die Publikationsquelle überschreitet die Darstellungsgrenze von 8 MiB.");
        return await File.ReadAllTextAsync(safe, token).ConfigureAwait(false);
    }

    internal static IReadOnlyList<ScientificNumericTable> ExtractNumericTables(string markdown)
    {
        var result = new List<ScientificNumericTable>();
        var lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index + 2 < lines.Length && result.Count < 6; index++)
        {
            if (!lines[index].Contains('|') || !TableSeparator().IsMatch(lines[index + 1])) continue;
            var headers = Cells(lines[index]);
            if (headers.Length is < 2 or > 12) continue;
            var rows = new List<string[]>();
            var cursor = index + 2;
            for (; cursor < lines.Length && rows.Count < 200 && lines[cursor].Contains('|'); cursor++)
            {
                var row = Cells(lines[cursor]);
                if (row.Length == headers.Length) rows.Add(row);
            }
            index = cursor - 1;
            if (rows.Count < 2) continue;
            var columns = new List<ScientificNumericColumn>();
            for (var column = 1; column < headers.Length; column++)
            {
                var values = new List<double>();
                foreach (var row in rows)
                {
                    if (!TryNumber(row[column], out var value)) break;
                    values.Add(value);
                }
                if (values.Count == rows.Count) columns.Add(new(headers[column], values));
            }
            if (columns.Count > 0) result.Add(new("Tabelle " + (result.Count + 1), headers[0], rows.Select(row => row[0]).ToArray(), columns));
        }
        return result;
    }

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
    private static bool TryNumber(string text, out double value)
    {
        var normalized = text.Trim().Replace("−", "-", StringComparison.Ordinal);
        // Accept decimal comma only when no decimal point exists; mixed/grouped notation stays unplotted.
        if (normalized.Contains(',') && !normalized.Contains('.')) normalized = normalized.Replace(',', '.');
        value = 0;
        return NumericValue().IsMatch(normalized) && double.TryParse(normalized, NumberStyles.Float,
            CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static (string? RunId, DateTimeOffset? StartedAt) RunScope(ScientificResearchProject project, ResearchStoredReport? report)
    {
        if (report is null || report.ProjectId != project.Id || string.IsNullOrWhiteSpace(report.ManifestJson)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(report.ManifestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("runId", out var run)
                || run.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(run.GetString())) return (null, null);
            if (root.TryGetProperty("projectId", out var owner) && (owner.ValueKind != JsonValueKind.String || owner.GetString() != project.Id))
                throw new InvalidDataException("Der Simulationsbericht gehört zu einem anderen Forschungsprojekt.");
            return (run.GetString(), root.TryGetProperty("runStartedAt", out var start) && start.ValueKind == JsonValueKind.String
                && start.TryGetDateTimeOffset(out var timestamp) ? timestamp : null);
        }
        catch (JsonException) { return (null, null); }
    }

    private static string ResearchFingerprint(ScientificResearchProject project,
        (IReadOnlyList<ResearchLiteratureEntry> Works, IReadOnlyList<ResearchEvidenceRecord> Evidence, ResearchStoredReport? Report) archive) =>
        Hash(JsonSerializer.SerializeToUtf8Bytes(new { project, archive.Works, archive.Evidence, archive.Report }, JsonOptions));

    private async Task<bool> IsCurrentResearchAsync(string projectId, string fingerprint, CancellationToken token)
    {
        var project = await repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        if (project is null) return false;
        var archive = await repository.LoadArchiveSnapshotAsync(projectId, token).ConfigureAwait(false);
        var current = await repository.GetProjectAsync(projectId, token).ConfigureAwait(false);
        return project == current && ResearchFingerprint(project, archive) == fingerprint;
    }

    private static ScientificSimulationSnapshot Superseded(string projectId) => new(projectId, 0, "updating",
        "Der Forschungsstand hat sich geändert. Die Python-Darstellung wird mit den aktuellen Daten erneuert.", [], DateTimeOffset.UtcNow);
    private static ScientificSimulationSnapshot Snapshot(ScientificResearchProject project,
        IReadOnlyList<ScientificSimulationArtifact> artifacts, string status, string detail) =>
        new(project.Id, project.Revision, status, detail, artifacts, DateTimeOffset.UtcNow);
    private static string ExecutionError(ResearchSandboxRunResult result) => result.TimedOut
        ? "Die Python-Darstellung wurde nach Ablauf des Zeitlimits beendet."
        : "Python-Darstellung fehlgeschlagen (Exit " + result.ExitCode + "): "
          + (result.StandardError.Length > 1600 ? result.StandardError[^1600..] : result.StandardError);

    internal static string SafePath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            throw new UnauthorizedAccessException("Ungültiger Simulationspfad.");
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(parent, relative));
        if (!path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Simulationspfad verlässt das Forschungsprojekt.");
        // Inspect every ancestor, including an existing parent above a not-yet-created output folder.
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Verknüpfungen sind für Forschungsabbildungen nicht zulässig.");
        return path;
    }

    private static void ValidateId(string projectId)
    {
        if (!SafeIdentifier().IsMatch(projectId)) throw new ArgumentException("Ungültiges Forschungsprojekt.", nameof(projectId));
    }
    public void Dispose() => _gate.Dispose();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
    [GeneratedRegex("^\\s*\\|?\\s*:?-{3,}:?\\s*(\\|\\s*:?-{3,}:?\\s*)+\\|?\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TableSeparator();
    [GeneratedRegex("^[+-]?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericValue();

    internal sealed record ScientificNumericColumn(string Label, IReadOnlyList<double> Values);
    internal sealed record ScientificNumericTable(string Title, string XLabel, IReadOnlyList<string> Labels,
        IReadOnlyList<ScientificNumericColumn> Series);

    internal const string SimulationHarness = """
        import os, sys, pathlib, runpy
        os.environ['MPLCONFIGDIR'] = '/tmp/missum-matplotlib'
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        target = pathlib.Path(sys.argv[2])
        target.mkdir(parents=True, exist_ok=True)
        os.environ['MISSUM_ARTIFACTS'] = str(target)
        def save_figures(*args, **kwargs):
            for number in plt.get_fignums():
                plt.figure(number).savefig(target / ('figure-%02d.png' % number), dpi=160, bbox_inches='tight')
        plt.show = save_figures
        runpy.run_path(sys.argv[1], run_name='__main__')
        save_figures()
        print('Missum: Python-Abbildungen gespeichert in ' + str(target))
        """;

    internal const string PublicationPlotScript = """
        import os, sys, json, pathlib, math
        os.environ['MPLCONFIGDIR'] = '/tmp/missum-matplotlib'
        import matplotlib
        matplotlib.use('Agg')
        import matplotlib.pyplot as plt
        data = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
        target = pathlib.Path(sys.argv[2]); target.mkdir(parents=True, exist_ok=True)
        plt.rcParams.update({'font.family': 'DejaVu Sans', 'font.size': 11,
            'axes.spines.top': False, 'axes.spines.right': False, 'axes.titleweight': 'bold',
            'figure.facecolor': '#faf9fc', 'axes.facecolor': '#faf9fc', 'text.color': '#24212b',
            'axes.labelcolor': '#50485e', 'xtick.color': '#50485e', 'ytick.color': '#50485e'})
        colors = ['#8057c7', '#27888b', '#cc8644', '#6b78bd', '#b55d85', '#56814c']
        manifest = []
        for index, table in enumerate(data['tables']):
            series = table['series'][:6]
            fig, axes = plt.subplots(len(series), 1, figsize=(9, max(3.6, 2.9*len(series))), squeeze=False)
            for number, column in enumerate(series):
                axis = axes[number][0]
                try:
                    positions = [float(str(label).replace(',', '.').replace('−', '-')) for label in table['labels']]
                    if not all(math.isfinite(value) for value in positions): raise ValueError('nonfinite axis')
                except ValueError:
                    positions = list(range(len(table['labels'])))
                axis.plot(positions, column['values'], color=colors[number % len(colors)], marker='o', markersize=4, linewidth=1.8)
                stride = max(1, len(positions)//12)
                axis.set_xticks(positions[::stride], [str(x)[:32] for x in table['labels'][::stride]], rotation=25, ha='right')
                axis.set_ylabel(column['label']); axis.set_xlabel(table['xLabel'])
                axis.grid(axis='y', color='#ddd7e5', linewidth=.7, alpha=.7)
                axis.set_title(column['label'], loc='left', pad=12, fontsize=12)
            fig.suptitle('Publikationsdaten · ' + table['title'], x=.08, ha='left', fontsize=15)
            fig.text(.08, .008, 'Quelle: gespeicherte Berichtstabelle. Verbindungslinien dienen nur der Orientierung.', fontsize=9, color='#71697d')
            fig.tight_layout(rect=(0,.03,1,.95))
            name = 'table-%02d.png' % (index+1); fig.savefig(target/name, dpi=160); plt.close(fig)
            manifest.append({'file':name, 'title':table['title'] + ' · ' + ', '.join(s['label'] for s in series), 'isResearchData':True})
        if not manifest:
            fig, axis = plt.subplots(figsize=(9,4.8))
            counts = [data['sourceCount'], data['evidenceCount']]
            axis.barh(['Quellen', 'Belegstellen'], counts, color=colors[:2], height=.5)
            axis.set_xlim(0, max(1, max(counts))*1.15)
            axis.set_xlabel('Gespeicherte Einträge'); axis.set_title('Evidenzbasis der Publikation', loc='left', pad=20)
            axis.xaxis.set_major_locator(matplotlib.ticker.MaxNLocator(integer=True))
            for y, value in enumerate(counts): axis.text(value + .02*max(1,max(counts)), y, str(value), va='center')
            axis.spines['left'].set_visible(False); axis.tick_params(axis='y', length=0)
            fig.text(.09,.025,'Forschungsmetadaten · keine fachliche Simulation oder unabhängige Evidenzprüfung.',fontsize=9,color='#71697d')
            fig.tight_layout(rect=(0,.06,1,1)); fig.savefig(target/'evidence.png',dpi=160); plt.close(fig)
            manifest.append({'file':'evidence.png','title':'Evidenzbasis · Quellen und Belegstellen','isResearchData':False})
        (target/'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False),encoding='utf-8')
        print(json.dumps({'plots':len(manifest),'reportId':data['reportId']}))
        """;
}

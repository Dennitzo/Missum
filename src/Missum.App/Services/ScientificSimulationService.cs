using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed record ScientificSimulationArtifact(string Id, string Title, string ImagePath,
    string? ScriptPath, string? DataPath, string Provenance, bool IsResearchData, string Sha256,
    ScientificExecutionEvidence? Execution = null);

public sealed record ScientificExecutionEvidence(string ProjectRoot, string ExperimentRecordId, string RunId,
    string ExecutedScriptPath, string ScriptSha256, string SnapshotId, IReadOnlyDictionary<string, string> InputHashes,
    IReadOnlyDictionary<string, string> OutputHashes, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);

public sealed record ScientificSimulationSnapshot(string ProjectId, long Revision, string Status,
    string Detail, IReadOnlyList<ScientificSimulationArtifact> Artifacts, DateTimeOffset UpdatedAt);

/// <summary>
/// Runs explicitly requested Python simulations and restores actual figures made by the
/// scientific code tools. Refreshing this view never generates charts from publication metadata.
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
        return await RefreshCurrentAsync(projectId, publication, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScientificSimulationSnapshot> RefreshCurrentAsync(string projectId, ScientificPublicationArtifact? publication,
        CancellationToken cancellationToken)
    {
        ValidateId(projectId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var artifacts = new List<ScientificSimulationArtifact>();
        long revision = 0;
        try
        {
            var project = await repository.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Das Forschungsprojekt wurde nicht gefunden.");
            revision = project.Revision;
            if (publication is not null && publication.ProjectId != project.Id)
                throw new InvalidDataException("Die Publikation gehört zu einem anderen Forschungsprojekt.");
            var result = await repository.LoadResultSnapshotAsync(projectId, cancellationToken).ConfigureAwait(false);
            var layout = await sandbox.EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            // Refresh only discovers real Python output. It never derives a chart
            // from a publication, counts evidence, writes a script or starts Python.
            artifacts = await DiscoverExperimentImagesAsync(project, layout, result.Experiments, notBefore: null, cancellationToken).ConfigureAwait(false);
            var restored = new List<ScientificSimulationArtifact>();
            await RestoreLastVisualizationAsync(project, layout, restored, cancellationToken).ConfigureAwait(false);
            artifacts = artifacts.Concat(restored).DistinctBy(item => item.ImagePath, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => File.GetLastWriteTimeUtc(item.ImagePath)).Take(MaximumImages).ToList();
            var snapshot = Snapshot(project, artifacts, artifacts.Count > 0 ? "ready" : "empty", "");
            if (artifacts.Count > 0)
            {
                var manifest = SafePath(layout.RootPath, "last-visualization.json");
                var temporary = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, manifest, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return snapshot;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException
                                         or System.ComponentModel.Win32Exception or JsonException)
        {
            return new(projectId, revision, "failed", "Python-Darstellung konnte nicht geladen werden: " + exception.Message,
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
            images = images.Select(image => image with
            {
                Provenance = "Forschungsexperiment · simulation-" + run.RunId + " · ProcessSucceeded",
                Execution = ExecutionFromRun(layout, "simulation-" + run.RunId, run),
            }).ToList();
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
            JsonSerializer.Serialize(run.InputHashes ?? new Dictionary<string, string> { [relative + "/simulation.py"] = sourceHash }),
            title + "\n" + run.Command, "{\"network\":\"none\",\"timeoutSeconds\":300}", run.SnapshotId is null
                ? run.StandardOutput : JsonSerializer.Serialize(new { runs = new[] { run } }, JsonOptions),
            run.StandardError, JsonSerializer.Serialize(artifacts.Select(item => item.ImagePath)),
            run.ExitCode == 0 && !run.TimedOut ? "ProcessSucceeded" : "ProcessFailed", run.StartedAt, run.CompletedAt), token).ConfigureAwait(false);
    }

    private static async Task<List<ScientificSimulationArtifact>> DiscoverExperimentImagesAsync(
        ScientificResearchProject project, ResearchSandboxLayout layout, IReadOnlyList<ResearchExperiment> experiments,
        DateTimeOffset? notBefore, CancellationToken token)
    {
        var images = new List<ScientificSimulationArtifact>();
        foreach (var experiment in experiments.Where(IsSuccessfulPythonExperiment).OrderByDescending(item => item.UpdatedAt))
        {
            if (MeasuredExecutions(experiment, layout) is { } measured)
            {
                foreach (var execution in measured.OrderByDescending(item => item.CompletedAt))
                    foreach (var pair in execution.OutputHashes)
                    {
                        var candidate = ResolveCandidate(layout.RootPath, pair.Key);
                        if (candidate is null || !File.Exists(candidate) || !IsImage(candidate)) continue;
                        var script = ResolveCandidate(layout.RootPath, execution.ExecutedScriptPath);
                        if (script is null || !File.Exists(script)) continue;
                        var before = images.Count;
                        await AddImageAsync(candidate, layout.RootPath, Path.GetFileNameWithoutExtension(candidate), script, null,
                            "Forschungsexperiment · " + experiment.Id + " · " + experiment.VerificationStatus,
                            true, images, token, notBefore).ConfigureAwait(false);
                        if (images.Count > before)
                            images[^1] = images[^1] with { Sha256 = pair.Value, Execution = execution };
                    }
                continue;
            }
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
            var experiment = experiments.Where(item => MeasuredExecutions(item, layout) is null && ExperimentContainsTimestamp(item, modified))
                .OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            if (experiment is not null && IsSuccessfulPythonExperiment(experiment) && ScriptFromExperiment(experiment, layout) is { } script)
                images[index] = image with { ScriptPath = script,
                    Provenance = "Forschungsexperiment · " + experiment.Id + " · " + experiment.VerificationStatus };
        }
        return images.Where(image => image.ScriptPath is not null
            && image.Provenance.StartsWith("Forschungsexperiment · ", StringComparison.Ordinal)).ToList();
    }

    private static bool IsSuccessfulPythonExperiment(ResearchExperiment experiment) =>
        experiment.VerificationStatus == "ProcessSucceeded" && (experiment.Id.StartsWith("simulation-", StringComparison.Ordinal)
            || experiment.CommandText.StartsWith("research.code.execute ", StringComparison.Ordinal));

    private static ScientificExecutionEvidence? ExecutionFromRun(ResearchSandboxLayout layout, string recordId, ResearchSandboxRunResult run) =>
        run.ExecutedScriptPath is { Length: > 0 } script && run.ScriptSha256 is { Length: > 0 } hash
        && run.SnapshotId is { Length: > 0 } snapshot && run.InputHashes is not null && run.OutputHashes is not null
            ? new(layout.RootPath, recordId, run.RunId, script, hash, snapshot, run.InputHashes, run.OutputHashes, run.StartedAt, run.CompletedAt) : null;

    private static List<ScientificExecutionEvidence>? MeasuredExecutions(ResearchExperiment experiment, ResearchSandboxLayout layout)
    {
        try
        {
            using var document = JsonDocument.Parse(experiment.StdoutEvidence);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("runs", out var runs)
                || runs.ValueKind != JsonValueKind.Array) return null;
            var measured = new List<ScientificExecutionEvidence>();
            var hasMeasured = false;
            foreach (var item in runs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("snapshotId", out var snapshot) || snapshot.ValueKind != JsonValueKind.String) continue;
                hasMeasured = true;
                var run = item.Deserialize<ResearchSandboxRunResult>(JsonOptions);
                if (run is { ExitCode: 0, TimedOut: false } && ExecutionFromRun(layout, experiment.Id, run) is { } evidence)
                    measured.Add(evidence);
            }
            return hasMeasured ? measured : null;
        }
        catch (JsonException) { return null; }
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
        // Former automatic publication/evidence previews are not Python simulations.
        if (Path.GetRelativePath(root, path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("publication", StringComparison.OrdinalIgnoreCase))) return;
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

    private static async Task RestoreLastVisualizationAsync(ScientificResearchProject project, ResearchSandboxLayout layout,
        List<ScientificSimulationArtifact> images, CancellationToken token)
    {
        var manifest = SafePath(layout.RootPath, "last-visualization.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 256_000) return;
        ScientificSimulationSnapshot? saved;
        try { saved = JsonSerializer.Deserialize<ScientificSimulationSnapshot>(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false), JsonOptions); }
        catch (JsonException) { return; }
        if (saved?.ProjectId != project.Id || saved.Artifacts is null) return;
        foreach (var artifact in saved.Artifacts.Where(item => item is { IsResearchData: true }
            && !string.IsNullOrWhiteSpace(item.ImagePath)).Take(MaximumImages))
        {
            foreach (var root in CandidateRoots(project, layout))
            {
                var image = ResolveCandidate(root, artifact.ImagePath);
                if (image is null || !File.Exists(image) || !IsImage(image)) continue;
                var before = images.Count;
                await AddImageAsync(image, root, artifact.Title,
                    artifact.ScriptPath is null ? null : ResolveCandidate(root, artifact.ScriptPath),
                    artifact.DataPath is null ? null : ResolveCandidate(root, artifact.DataPath),
                    artifact.Provenance, true, images, token).ConfigureAwait(false);
                if (images.Count > before)
                {
                    // Restoring a preview never turns changed files into fresh process evidence.
                    if (images[^1].Sha256 != artifact.Sha256) images.RemoveAt(images.Count - 1);
                    else images[^1] = images[^1] with { Execution = artifact.Execution };
                }
            }
        }
    }

    private static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
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
        "Der Forschungsstand hat sich geändert. Dieser Python-Lauf wird nicht als aktuelles Ergebnis dargestellt.", [], DateTimeOffset.UtcNow);
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

}

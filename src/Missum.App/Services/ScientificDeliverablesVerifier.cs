using System.Security.Cryptography;
using System.Text.Json;

namespace Missum.App.Services;

/// <summary>Checks real files behind the current presentation, without treating generated prose as an execution receipt.</summary>
internal static class ScientificDeliverablesVerifier
{
    internal static async Task<JsonElement> VerifyAsync(string projectId, ScientificPresentationSnapshot? snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publication = snapshot?.Publication;
        var publicationReady = snapshot?.PublicationError is null && publication is not null && publication.ProjectId == projectId
            && await HasHeaderAsync(publication.PdfPath, "%PDF-"u8.ToArray(), 1024, cancellationToken).ConfigureAwait(false)
            && File.Exists(publication.MarkdownPath);
        var sourceHash = publicationReady ? await HashAsync(publication!.MarkdownPath, cancellationToken).ConfigureAwait(false) : null;
        var publicationError = snapshot?.PublicationError ?? (publicationReady ? null : publication is null
            ? "Noch kein wissenschaftlicher Manuskriptstand als PDF vorhanden."
            : publication.ProjectId != projectId ? "Die Publikation gehört zu einem anderen Forschungsprojekt."
            : !File.Exists(publication.MarkdownPath) ? "Die Markdownquelle der Publikation fehlt."
            : "Die PDF-Datei fehlt oder besitzt keinen gültigen PDF-Dateikopf.");
        var files = new List<object>();
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var simulation = snapshot?.Simulation;
        if (snapshot?.SimulationError is null && simulation?.ProjectId == projectId && simulation.Status == "ready")
        {
            foreach (var image in simulation.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A publication evidence chart, a missing file or a changed PNG is not a successful research execution.
                if (!image.IsResearchData || !image.Provenance.StartsWith("Forschungsexperiment · ", StringComparison.Ordinal))
                { rejected.Add("Die Abbildung ist keinem erfolgreichen Python-Experiment zugeordnet."); continue; }
                if (string.IsNullOrWhiteSpace(image.ScriptPath) || !File.Exists(image.ScriptPath))
                { rejected.Add("Das zugehörige Python-Skript fehlt."); continue; }
                if (!await IsImageAsync(image.ImagePath, cancellationToken).ConfigureAwait(false))
                { rejected.Add("Die Abbildung fehlt oder liegt nicht als gültige PNG-/JPEG-Datei vor."); continue; }
                var hash = await HashAsync(image.ImagePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, image.Sha256, StringComparison.OrdinalIgnoreCase))
                { rejected.Add("Die Abbildung wurde seit dem gespeicherten Experiment verändert (SHA-256 stimmt nicht überein)."); continue; }
                var experimentRecordId = image.Provenance.Split(" · ", StringSplitOptions.None).ElementAtOrDefault(1);
                if (image.Execution is { } execution)
                {
                    var error = await VerifyExecutionAsync(execution, image, experimentRecordId, cancellationToken).ConfigureAwait(false);
                    if (error is not null) { rejected.Add(error); continue; }
                    var script = ScientificSimulationService.SafePath(execution.ProjectRoot, execution.ExecutedScriptPath);
                    files.Add(new { path = image.ImagePath, sha256 = hash, experimentRecordId,
                        artifactPath = Path.GetRelativePath(execution.ProjectRoot, image.ImagePath).Replace('\\', '/'),
                        lastModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(image.ImagePath)), scriptPath = script,
                        scriptSha256 = await HashAsync(script, cancellationToken).ConfigureAwait(false),
                        executedScriptPath = execution.ExecutedScriptPath, runId = execution.RunId, snapshotId = execution.SnapshotId,
                        inputHashes = execution.InputHashes, outputHashes = execution.OutputHashes,
                        startedAt = execution.StartedAt, completedAt = execution.CompletedAt });
                    continue;
                }
                files.Add(new { path = image.ImagePath, sha256 = hash, experimentRecordId,
                    lastModifiedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(image.ImagePath)), scriptPath = image.ScriptPath,
                    scriptSha256 = await HashAsync(image.ScriptPath, cancellationToken).ConfigureAwait(false) });
            }
        }
        var simulationReady = files.Count > 0;
        var simulationError = snapshot?.SimulationError ?? (simulationReady ? null : rejected.Count > 0
            ? string.Join(" ", rejected.Take(3)) : "Noch keine darstellbare Abbildung aus einem erfolgreichen Python-Experiment vorhanden.");
        return JsonSerializer.SerializeToElement(new
        {
            success = publicationReady && simulationReady,
            projectId,
            message = publicationReady && simulationReady ? "PDF-Publikation und tatsächlich ausgeführte Python-Abbildungen sind vorhanden."
                : string.Join(" ", new[] { publicationError, simulationError }.Where(static error => !string.IsNullOrWhiteSpace(error))),
            publication = new { ready = publicationReady, pdfPath = publicationReady ? publication!.PdfPath : null,
                sourceSha256 = sourceHash, error = publicationError },
            simulation = new { ready = simulationReady, executed = simulationReady, artifacts = files,
                error = simulationError },
            retryable = true,
        });
    }

    private static async Task<string?> VerifyExecutionAsync(ScientificExecutionEvidence execution,
        ScientificSimulationArtifact image, string? recordId, CancellationToken token)
    {
        const string invalid = "Der gemessene Python-Ausführungsbeleg oder sein eingefrorener Eingabestand ist ungültig.";
        if (execution.ExperimentRecordId != recordId || string.IsNullOrWhiteSpace(execution.RunId)
            || execution.SnapshotId != "execution-" + execution.RunId || execution.StartedAt > execution.CompletedAt
            || execution.InputHashes is null || execution.OutputHashes is null
            || execution.InputHashes.Count is < 1 or > 4096 || execution.OutputHashes.Count is < 1 or > 4096
            || !execution.InputHashes.TryGetValue(execution.ExecutedScriptPath, out var scriptHash)
            || scriptHash != execution.ScriptSha256) return invalid;
        try
        {
            var artifactPath = Path.GetRelativePath(execution.ProjectRoot, image.ImagePath).Replace('\\', '/');
            if (!execution.OutputHashes.TryGetValue(artifactPath, out var outputHash) || outputHash != image.Sha256) return invalid;
            foreach (var pair in execution.InputHashes)
            {
                if (!CanonicalPath(pair.Key, input: true) || !Sha256(pair.Value)) return invalid;
                var frozen = ScientificSimulationService.SafePath(execution.ProjectRoot, "snapshots/" + execution.SnapshotId + "/frozen/" + pair.Key);
                if (!File.Exists(frozen) || await HashAsync(frozen, token).ConfigureAwait(false) != pair.Value) return invalid;
                var current = ScientificSimulationService.SafePath(execution.ProjectRoot, pair.Key);
                var expectedCurrent = execution.OutputHashes.TryGetValue(pair.Key, out var after) ? after : pair.Value;
                if (!File.Exists(current) || await HashAsync(current, token).ConfigureAwait(false) != expectedCurrent)
                    return "Eine Eingabedatei wurde seit dem zugeordneten Python-Experiment verändert (SHA-256 stimmt nicht überein).";
            }
            foreach (var pair in execution.OutputHashes)
            {
                if (!CanonicalPath(pair.Key, input: false) || !Sha256(pair.Value)) return invalid;
                var output = ScientificSimulationService.SafePath(execution.ProjectRoot, pair.Key);
                if (!File.Exists(output) || await HashAsync(output, token).ConfigureAwait(false) != pair.Value)
                    return "Eine Ergebnisdatei wurde seit dem zugeordneten Python-Experiment verändert (SHA-256 stimmt nicht überein).";
            }
            var script = ScientificSimulationService.SafePath(execution.ProjectRoot, execution.ExecutedScriptPath);
            if (!execution.ExecutedScriptPath.StartsWith("work/", StringComparison.Ordinal)
                || await HashAsync(script, token).ConfigureAwait(false) != execution.ScriptSha256) return invalid;
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(image.ImagePath));
            if (modified.AddSeconds(2) < execution.StartedAt || modified.AddSeconds(-2) > execution.CompletedAt) return invalid;
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return invalid; }
    }

    private static bool CanonicalPath(string path, bool input)
    {
        if (path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) return false;
        var parts = path.Split('/');
        return parts.Length > 1 && parts.All(part => part.Length > 0 && part is not ("." or ".."))
            && (input ? parts[0] is "work" or "inputs" or "env" : parts[0] is "work" or "artifacts" or "notebooks");
    }

    private static bool Sha256(string? hash) => hash is { Length: 64 } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task<bool> IsImageAsync(string path, CancellationToken token)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            return await HasHeaderAsync(path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 24, token).ConfigureAwait(false);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return await HasHeaderAsync(path, new byte[] { 255, 216, 255 }, 16, token).ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> HasHeaderAsync(string path, byte[] expected, long minimumBytes, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (stream.Length < minimumBytes) return false;
            var header = new byte[expected.Length];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            return header.AsSpan().SequenceEqual(expected);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}

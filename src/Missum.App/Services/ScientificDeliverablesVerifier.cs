using System.Security.Cryptography;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>Checks real files behind the current presentation, without treating generated prose as an execution receipt.</summary>
internal static class ScientificDeliverablesVerifier
{
    internal static Task<JsonElement> VerifyAsync(string projectId, ScientificPresentationSnapshot? snapshot,
        CancellationToken cancellationToken = default) => VerifyAsync(projectId, snapshot, null, null, cancellationToken);

    internal static async Task<JsonElement> VerifyAsync(string projectId, ScientificPresentationSnapshot? snapshot,
        ResearchWorkingState? state, ResearchResultSnapshot? receipts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var publication = snapshot?.Publication;
        var publicationReady = snapshot?.PublicationError is null && publication is not null && publication.ProjectId == projectId
            && (state is null || state.ProjectId == projectId && publication.SectionDelta && publication.Revision == state.PublicationRevision
                && ScientificPublicationService.HasCanonicalSections(state))
            && await HasHeaderAsync(publication.PdfPath, "%PDF-"u8.ToArray(), 1024, cancellationToken).ConfigureAwait(false)
            && File.Exists(publication.MarkdownPath);
        var sourceHash = publicationReady ? await HashAsync(publication!.MarkdownPath, cancellationToken).ConfigureAwait(false) : null;
        var publicationError = snapshot?.PublicationError ?? (publicationReady ? null : publication is null
            ? "Noch kein wissenschaftlicher Manuskriptstand als PDF vorhanden."
            : publication.ProjectId != projectId ? "Die Publikation gehört zu einem anderen Forschungsprojekt."
            : state is not null && (!publication.SectionDelta || publication.Revision != state.PublicationRevision)
                ? "Die PDF entspricht noch nicht der aktuellen Publikationsrevision."
            : !File.Exists(publication.MarkdownPath) ? "Die Markdownquelle der Publikation fehlt."
            : "Die PDF-Datei fehlt oder besitzt keinen gültigen PDF-Dateikopf.");
        var files = new List<object>();
        var interactiveFiles = new List<object>();
        var verifiedFigureExperiments = new HashSet<string>(StringComparer.Ordinal);
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var simulation = snapshot?.Simulation;
        if (snapshot?.SimulationError is null && simulation?.ProjectId == projectId && simulation.Status == "ready")
        {
            foreach (var image in simulation.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (image.Kind == "interactive")
                {
                    if (await VerifyInteractiveArtifactAsync(projectId, image, cancellationToken).ConfigureAwait(false) is { } artifact)
                        interactiveFiles.Add(artifact);
                    else rejected.Add("Die interaktive Simulation fehlt, ist verändert oder besitzt keinen gültigen projektgebundenen HTML-/JavaScript-Quellbeleg.");
                    continue;
                }
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
                    verifiedFigureExperiments.Add(execution.ExperimentRecordId);
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
        var executed = files.Count > 0;
        var interactiveReady = interactiveFiles.Count > 0;
        var activeRequirements = state?.Items.Where(item => item.Kind == "requirement"
            && Required(item.Data) && Text(item.Data, "status") is not ("withdrawn" or "openLimit")).ToArray() ?? [];
        // Legacy receipts retain the Python/figure gate. Canonical requirements can
        // explicitly ask for an interactive source artifact instead of a process proof.
        var executionRequired = state is null || activeRequirements.Any(item => IsFigureRequirement(item.Data)
            && (!IsInteractiveRequirement(item.Data) || RequiresScientificExecution(item.Data)));
        var interactiveRequired = activeRequirements.Any(item => IsInteractiveRequirement(item.Data));
        var simulationRequired = executionRequired || interactiveRequired;
        var simulationReady = (!executionRequired || executed) && (!interactiveRequired || interactiveReady)
            && (executed || interactiveReady || !simulationRequired);
        var simulationError = snapshot?.SimulationError ?? (simulationReady ? null : rejected.Count > 0
            ? string.Join(" ", rejected.Take(3)) : interactiveRequired && !interactiveReady
                ? "Noch keine gültige projektgebundene HTML-/JavaScript-Simulation vorhanden."
                : "Noch keine darstellbare Abbildung aus einem erfolgreichen Python-Experiment vorhanden.");
        var missing = state is null ? new List<string>()
            : await ResearchMissingAsync(state, receipts, publication, verifiedFigureExperiments, interactiveReady, cancellationToken).ConfigureAwait(false);
        if (!publicationReady) missing.Add(publicationError ?? "Die Publikation entspricht noch nicht dem aktuellen fachlichen Stand.");
        if (simulationRequired && !simulationReady) missing.Add(simulationError ?? "Die erforderliche Darstellung fehlt.");
        var ready = publicationReady && (!simulationRequired || simulationReady) && missing.Count == 0;
        return JsonSerializer.SerializeToElement(new
        {
            success = ready,
            projectId,
            message = ready ? "Der aktuelle Publikationsstand und die ausdrücklich erforderlichen Nachweise liegen vor."
                : string.Join(" ", missing),
            research = state is null ? null : new { protocol = "section-delta-v1", revision = state.Revision,
                publicationRevision = state.PublicationRevision, ready, missing },
            publication = new { ready = publicationReady, pdfPath = publicationReady ? publication!.PdfPath : null,
                sourceSha256 = sourceHash, revision = publication?.Revision ?? 0, error = publicationError },
            simulation = new { required = simulationRequired, ready = simulationReady, executed, executionRequired,
                interactiveRequired, interactiveReady, artifacts = files, interactiveArtifacts = interactiveFiles,
                error = simulationRequired ? simulationError : null },
            retryable = true,
        });
    }

    private static async Task<List<string>> ResearchMissingAsync(ResearchWorkingState state, ResearchResultSnapshot? receipts,
        ScientificPublicationArtifact? publication, HashSet<string> verifiedFigureExperiments, bool interactiveReady, CancellationToken token)
    {
        var missing = new List<string>();
        var requirements = state.Items.Where(item => item.Kind == "requirement" && Required(item.Data)
            && Text(item.Data, "status") != "withdrawn").ToArray();
        if (requirements.Length == 0) missing.Add("Die fachlichen Ziele des Auftrags sind noch nicht als erforderliche Arbeitsergebnisse dokumentiert.");
        foreach (var requirement in requirements)
        {
            var status = Text(requirement.Data, "status");
            var experiments = ScientificPublicationService.Ids(requirement.Data, "experimentIds");
            if (status == "openLimit" && Text(requirement.Data, "reason").Length > 0
                && (ScientificPublicationService.Ids(requirement.Data, "sourceIds").Count > 0
                    || ScientificPublicationService.Ids(requirement.Data, "evidenceIds").Count > 0
                    || receipts?.Experiments.Any(experiment => experiments.Contains(experiment.Id) && HasMeasuredReceipt(experiment, requireSuccess: false)) == true)) continue;
            if (status is not ("completed" or "supported" or "verified"))
            {
                missing.Add($"Arbeitsergebnis {requirement.Id}: noch offen; nächsten fachlichen Schritt oder begründete Nachweisgrenze dokumentieren.");
                continue;
            }
            if (IsInteractiveRequirement(requirement.Data))
            {
                if (!interactiveReady)
                    missing.Add($"Arbeitsergebnis {requirement.Id}: der aktuelle projektgebundene HTML-/JavaScript-Quellbeleg für die interaktive Simulation fehlt.");
                if (!RequiresScientificExecution(requirement.Data)) continue;
            }
            var formal = HasMethod(requirement.Data, "lean", "formal", "formalproof");
            var symbolic = HasMethod(requirement.Data, "symbolic", "symbolisch", "symbolische", "sympy", "smt");
            if (formal || symbolic || HasMethod(requirement.Data, "python", "numeric", "numerical", "numerisch", "numerische", "numerik")
                || IsFigureRequirement(requirement.Data))
            {
                var accepted = false;
                foreach (var experiment in receipts?.Experiments ?? [])
                {
                    if (!experiments.Contains(experiment.Id) || experiment.VerificationStatus != "ProcessSucceeded"
                        || !HasMeasuredReceipt(experiment)) continue;
                    if (IsFigureRequirement(requirement.Data) && !verifiedFigureExperiments.Contains(experiment.Id)) continue;
                    if (formal && (!experiment.CommandText.StartsWith("math.formalProof ", StringComparison.Ordinal)
                        || !HasKernelReceipt(experiment))) continue;
                    if (symbolic && !(experiment.CommandText.StartsWith("math.symbolic ", StringComparison.Ordinal)
                        || experiment.CommandText.StartsWith("math.smt ", StringComparison.Ordinal)
                        || experiment.CommandText.StartsWith("research.code.execute ", StringComparison.Ordinal))) continue;
                    if (publication is not null && await VerifyExperimentFilesAsync(experiment, publication.PdfPath, token).ConfigureAwait(false))
                    { accepted = true; break; }
                }
                if (!accepted)
                    missing.Add($"Arbeitsergebnis {requirement.Id}: ein zugeordneter erfolgreicher Ausführungsbeleg fehlt.");
            }
        }
        // A completed field is a workflow declaration, not certification of a scientific claim.
        // Only the referenced, durable execution records satisfy machine-checkable requirements.
        return missing;
    }

    private static bool HasMeasuredReceipt(ResearchExperiment experiment, bool requireSuccess = true)
    {
        try
        {
            using var receipt = JsonDocument.Parse(experiment.StdoutEvidence);
            if (!receipt.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return false;
            return runs.EnumerateArray().Any(run => run.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var value) && (!requireSuccess || value == 0)
                && run.TryGetProperty("runId", out var id) && id.ValueKind == JsonValueKind.String
                && run.TryGetProperty("inputHashes", out var inputs) && inputs.ValueKind == JsonValueKind.Object
                && run.TryGetProperty("outputHashes", out var outputs) && outputs.ValueKind == JsonValueKind.Object);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static bool HasKernelReceipt(ResearchExperiment experiment)
    {
        try
        {
            using var document = JsonDocument.Parse(experiment.StdoutEvidence);
            return Text(document.RootElement, "formalVerification") == "KernelAccepted";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private static async Task<bool> VerifyExperimentFilesAsync(ResearchExperiment experiment, string pdfPath, CancellationToken token)
    {
        // The publication lives below <project>/publications/<project hash>/<revision>/Publikation.pdf.
        var root = new DirectoryInfo(Path.GetDirectoryName(pdfPath)!).Parent?.Parent?.Parent?.FullName;
        if (root is null) return false;
        try
        {
            using var document = JsonDocument.Parse(experiment.StdoutEvidence);
            var runs = document.RootElement.GetProperty("runs");
            foreach (var run in runs.EnumerateArray())
            {
                if (!run.TryGetProperty("exitCode", out var code) || !code.TryGetInt32(out var exit) || exit != 0
                    || run.TryGetProperty("timedOut", out var timeout) && timeout.ValueKind == JsonValueKind.True) return false;
                var runId = Text(run, "runId");
                if (runId.Length == 0 || Text(run, "snapshotId") != "execution-" + runId) return false;
                var script = Text(run, "executedScriptPath");
                var scriptHash = Text(run, "scriptSha256");
                var inputs = run.GetProperty("inputHashes");
                var outputs = run.GetProperty("outputHashes");
                if (inputs.ValueKind != JsonValueKind.Object || outputs.ValueKind != JsonValueKind.Object
                    || !inputs.TryGetProperty(script, out var measuredScript) || measuredScript.GetString() != scriptHash
                    || !Sha256(scriptHash) || !script.StartsWith("work/", StringComparison.Ordinal)) return false;
                foreach (var input in inputs.EnumerateObject())
                {
                    var hash = input.Value.ValueKind == JsonValueKind.String ? input.Value.GetString() : null;
                    if (!CanonicalPath(input.Name, input: true) || !Sha256(hash)) return false;
                    var frozen = ScientificSimulationService.SafePath(root, "snapshots/execution-" + runId + "/frozen/" + input.Name);
                    var current = ScientificSimulationService.SafePath(root, input.Name);
                    if (!File.Exists(frozen) || await HashAsync(frozen, token).ConfigureAwait(false) != hash || !File.Exists(current)) return false;
                    var expected = outputs.TryGetProperty(input.Name, out var changed) && changed.ValueKind == JsonValueKind.String
                        ? changed.GetString() : hash;
                    if (await HashAsync(current, token).ConfigureAwait(false) != expected) return false;
                }
                foreach (var output in outputs.EnumerateObject())
                {
                    var hash = output.Value.ValueKind == JsonValueKind.String ? output.Value.GetString() : null;
                    if (!CanonicalPath(output.Name, input: false) || !Sha256(hash)) return false;
                    var path = ScientificSimulationService.SafePath(root, output.Name);
                    if (!File.Exists(path) || await HashAsync(path, token).ConfigureAwait(false) != hash) return false;
                }
            }
            return runs.GetArrayLength() > 0;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return false; }
    }

    private static bool IsFigureRequirement(JsonElement data) =>
        HasMethod(data, "simulation", "plot", "figure", "graph", "graphviz", "abbildung", "visualization", "visualisierung");

    private static bool IsInteractiveRequirement(JsonElement data)
    {
        var text = string.Join(" ", Text(data, "method"), Text(data, "title"), Text(data, "statement"));
        return System.Text.RegularExpressions.Regex.IsMatch(text,
            @"\b(?:interactive|interaktiv\p{L}*|animation\p{L}*|animated|animiert\p{L}*|realtime|real[- ]time|echtzeit\p{L}*)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private static bool RequiresScientificExecution(JsonElement data) => HasMethod(data,
        "python", "numeric", "numerical", "numerisch", "numerische", "numerik", "lean", "formal", "formalproof",
        "symbolic", "symbolisch", "symbolische", "sympy", "smt");

    private static async Task<object?> VerifyInteractiveArtifactAsync(string projectId, ScientificSimulationArtifact artifact, CancellationToken token)
    {
        if (artifact.IsResearchData || artifact.Execution is not null || artifact.ContentType != "text/html"
            || string.IsNullOrWhiteSpace(artifact.ProjectRoot) || !ScientificSimulationHtml.IsHtmlPath(artifact.ImagePath)
            || Path.GetFileName(Path.TrimEndingDirectorySeparator(artifact.ProjectRoot)) != projectId) return null;
        try
        {
            var relative = Path.GetRelativePath(artifact.ProjectRoot, artifact.ImagePath).Replace('\\', '/');
            if (!(relative.StartsWith("work/", StringComparison.Ordinal) || relative.StartsWith("artifacts/", StringComparison.Ordinal))
                || relative.Split('/').Any(part => part.Equals("publication", StringComparison.OrdinalIgnoreCase))) return null;
            var path = ScientificSimulationService.SafePath(artifact.ProjectRoot, relative);
            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > ScientificSimulationHtml.MaximumBytes) return null;
            var source = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            var hash = Convert.ToHexStringLower(SHA256.HashData(source));
            if (hash != artifact.Sha256 || !ScientificSimulationHtml.IsInteractiveDocument(System.Text.Encoding.UTF8.GetString(source))) return null;
            return new { kind = "interactive", projectId, path, artifactPath = relative, sha256 = hash,
                sourceArtifact = true, executed = false, contentType = "text/html" };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool HasMethod(JsonElement data, params string[] choices)
    {
        var tokens = System.Text.RegularExpressions.Regex.Split(Text(data, "method").ToLowerInvariant(), @"[^\p{L}\p{N}]+",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return tokens.Any(token => choices.Contains(token, StringComparer.Ordinal));
    }

    private static bool Required(JsonElement data) => data.ValueKind != JsonValueKind.Object
        || !data.TryGetProperty("required", out var value) || value.ValueKind != JsonValueKind.False;
    private static string Text(JsonElement data, string property) => ScientificPublicationService.Text(data, property);

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

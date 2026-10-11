using System.Security.Cryptography;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

internal static partial class ScientificDeliverablesVerifier
{
    private const int MaximumQuantitativeArtifactBytes = 1024 * 1024;

    private static bool RequiresQuantitativeValidation(JsonElement data) =>
        data.TryGetProperty("quantitativeValidation", out _)
        || HasMethod(data, "numeric", "numerical", "numerisch", "numerische", "numerik")
        || HasMethod(data, "simulation") && (!IsInteractiveRequirement(data) || IsExactScientificMethod(data))
        || HasMethod(data, "python") && !IsExactScientificMethod(data);

    private static bool IsExactScientificMethod(JsonElement data) => HasMethod(data,
        "symbolic", "symbolisch", "symbolische", "sympy", "smt", "lean", "formal", "formalproof");

    private static async Task<IReadOnlyList<string>> VerifyQuantitativeArtifactAsync(JsonElement definition,
        ResearchExperiment experiment, string pdfPath, CancellationToken token)
    {
        var artifactPath = Text(definition, "artifactPath");
        var root = new DirectoryInfo(Path.GetDirectoryName(pdfPath)!).Parent?.Parent?.Parent?.FullName;
        if (root is null || !CanonicalPath(artifactPath, input: false)
            || !(artifactPath.StartsWith("work/", StringComparison.Ordinal) || artifactPath.StartsWith("artifacts/", StringComparison.Ordinal)))
            return ["quantitativeValidation.artifactPath ist kein sicherer projektgebundener Messdateipfad."];
        try
        {
            using var receipt = JsonDocument.Parse(experiment.StdoutEvidence);
            var measuredHashes = receipt.RootElement.GetProperty("runs").EnumerateArray()
                .Where(run => run.GetProperty("exitCode").GetInt32() == 0
                    && run.GetProperty("outputHashes").TryGetProperty(artifactPath, out var hash) && hash.ValueKind == JsonValueKind.String)
                .Select(run => run.GetProperty("outputHashes").GetProperty(artifactPath).GetString())
                .ToArray();
            if (measuredHashes.Length == 0)
                return [$"Die Messdatei {artifactPath} fehlt in outputHashes des zugeordneten erfolgreichen Experiments {experiment.Id}."];
            var path = ScientificSimulationService.SafePath(root, artifactPath);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            if (stream.Length is <= 0 or > MaximumQuantitativeArtifactBytes)
                return [$"Die Messdatei {artifactPath} ist leer oder überschreitet die Grenze von 1 MiB."];
            var bytes = new byte[(int)stream.Length];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            var currentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!measuredHashes.Contains(currentHash, StringComparer.Ordinal))
                return [$"Die Messdatei {artifactPath} wurde seit dem zugeordneten Experiment verändert (SHA-256 stimmt nicht überein)."];
            using var artifact = JsonDocument.Parse(bytes);
            return ScientificQuantitativeValidation.Assess(definition, artifact.RootElement);
        }
        catch (JsonException)
        {
            return [$"Die Messdatei {artifactPath} oder der Ausführungsbeleg enthält kein gültiges JSON."];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return [$"Die Messdatei {artifactPath} ist nicht als unveränderter Output des zugeordneten Experiments verfügbar."];
        }
    }

    private static void AddUncoveredNumericalSections(ResearchWorkingState state, ResearchResultSnapshot? receipts,
        ResearchWorkingItem[] requirements, List<string> missing)
    {
        foreach (var section in state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null
            && Text(item.Data, "status") != "withdrawn" && Text(item.Data, "classification") is not ("technical" or "definition")))
        {
            // Use explicit scientific links and execution records, never keywords guessed from article prose.
            var experimentIds = ScientificPublicationService.Ids(section.Data, "experimentIds").ToHashSet(StringComparer.Ordinal);
            var pending = new Queue<string>(ScientificPublicationService.Ids(section.Data, "claimIds")
                .Concat(ScientificPublicationService.Ids(section.Data, "hypothesisIds")));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (pending.TryDequeue(out var id))
            {
                if (!seen.Add(id)) continue;
                var linked = state.Items.FirstOrDefault(item => item.Id == id);
                if (linked is null) continue;
                experimentIds.UnionWith(ScientificPublicationService.Ids(linked.Data, "experimentIds"));
                foreach (var dependency in ScientificPublicationService.Ids(linked.Data, "claimIds")
                    .Concat(ScientificPublicationService.Ids(linked.Data, "hypothesisIds"))) pending.Enqueue(dependency);
            }
            foreach (var experiment in receipts?.Experiments ?? [])
            {
                if (experiment.ProjectId != state.ProjectId || !experimentIds.Contains(experiment.Id)
                    || !(experiment.CommandText.StartsWith("research.code.execute ", StringComparison.Ordinal)
                        || experiment.CommandText.StartsWith("python ", StringComparison.Ordinal))) continue;
                // Python can carry an exact symbolic derivation. Its linked required goal is still
                // checked by the method/kernel and current-file gates in ResearchMissingAsync.
                if (requirements.Any(requirement => ScientificPublicationService.Ids(requirement.Data, "experimentIds").Contains(experiment.Id)
                    && (IsExactScientificMethod(requirement.Data) && !RequiresQuantitativeValidation(requirement.Data)
                        || RequiresQuantitativeValidation(requirement.Data)
                            && requirement.Data.TryGetProperty("quantitativeValidation", out var definition)
                            && ScientificQuantitativeValidation.ValidateDefinition(definition) is null))) continue;
                missing.Add($"Publikationsabschnitt {section.Id}: Das Rechenexperiment {experiment.Id} benötigt ein zugeordnetes erforderliches numerisches Arbeitsergebnis "
                    + "mit quantitativeValidation (Referenz, Genauigkeits- und Konvergenzgrenzen sowie gegebenenfalls Stichprobenunsicherheit). "
                    + "Bei einer rein exakten Herleitung stattdessen ein zugeordnetes erforderliches symbolisches oder formales Arbeitsergebnis mit passendem Prüfbeleg verwenden. "
                    + "Ein erfolgreicher Prozess oder eine rein qualitative Übereinstimmung erfüllt kein quantitatives Ziel.");
            }
        }
    }
}

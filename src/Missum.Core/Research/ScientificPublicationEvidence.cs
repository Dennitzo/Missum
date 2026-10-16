using System.Text.Json;

namespace Missum.Core.Research;

/// <summary>Publication completeness is an acceptance condition, not a reason to hide a readable draft.</summary>
public static class ScientificPublicationEvidence
{
    public static IReadOnlyList<ScientificResearchReviewIssue> Assess(ResearchWorkingState state,
        IReadOnlyList<ResearchExperiment> experiments, IReadOnlyList<ResearchVerification> checks)
    {
        var issues = new List<ScientificResearchReviewIssue>();
        var sections = state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null && Text(item.Data, "status") != "withdrawn").ToArray();
        var contexts = sections.ToDictionary(section => section.Id, section => Related(section, state), StringComparer.Ordinal);
        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var experiment in experiments.Where(item => item.ProjectId == state.ProjectId && item.VerificationStatus == "ProcessSucceeded")
            .OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            try
            {
                using var receipt = JsonDocument.Parse(experiment.StdoutEvidence);
                if (receipt.RootElement.ValueKind != JsonValueKind.Object || !receipt.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) continue;
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.ValueKind != JsonValueKind.Object || !run.TryGetProperty("exitCode", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var exit) || exit != 0
                        || Text(run, "runId").Length == 0 || Text(run, "snapshotId").Length == 0
                        || run.TryGetProperty("timedOut", out var timeout) && timeout.ValueKind == JsonValueKind.True
                        || !run.TryGetProperty("inputHashes", out var inputs) || inputs.ValueKind != JsonValueKind.Object
                        || !run.TryGetProperty("outputHashes", out var outputs) || outputs.ValueKind != JsonValueKind.Object) continue;
                    foreach (var output in outputs.EnumerateObject())
                    {
                        var path = output.Name;
                        if (!(path.StartsWith("work/", StringComparison.Ordinal) || path.StartsWith("artifacts/", StringComparison.Ordinal))
                            || path.Contains(':') || path.Contains('\\') || path.Any(char.IsControl) || path.Split('/').Any(part => part is "" or "." or "..")
                            || Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg")
                            || output.Value.ValueKind != JsonValueKind.String || output.Value.GetString() is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit)) continue;
                        latest.TryAdd(path, experiment.Id);
                    }
                }
            }
            catch (JsonException) { }
        }
        foreach (var (path, experimentId) in latest)
        {
            var assigned = sections.Where(section => contexts[section.Id].Any(item => Ids(item.Data, "experimentIds").Contains(experimentId))).ToArray();
            if (assigned.Length == 0)
            {
                Add("$publication", state.PublicationRevision, "simulation_topic_missing", "experimentIds", experimentId,
                    $"Die Simulation {experimentId} ({path}) ist noch keinem fachlichen Abschnitt zugeordnet. Ordne experimentIds dem passenden Thema zu und ergänze simulationDescriptions sowie figureCaptions.");
                continue;
            }
            if (!assigned.Any(section => Descriptions(contexts[section.Id], "simulationDescriptions").Any(description =>
                Text(description, "experimentId") == experimentId && Text(description, "equations").Length > 0 && Text(description, "description").Length > 0)))
                Add(assigned[0].Id, assigned[0].Revision, "simulation_explanation_missing", "simulationDescriptions", experimentId,
                    $"Abschnitt {assigned[0].Id}: Ergänze für Simulation {experimentId} simulationDescriptions mit experimentId, equations und description: verwendete Gleichungen, Annahmen, Parameter, Ergebnis, Vergleich und Grenzen.");
        }
        foreach (var section in sections)
        {
            var related = contexts[section.Id];
            var checkIds = related.SelectMany(item => Ids(item.Data, "checkIds")).ToHashSet(StringComparer.Ordinal);
            var experimentIds = related.SelectMany(item => Ids(item.Data, "experimentIds")).ToHashSet(StringComparer.Ordinal);
            foreach (var check in checks.Where(check => checkIds.Contains(check.Id)))
            {
                if (!ScientificLeanEvidence.TryRead(check, state.ProjectId, out var proof) || proof is null) continue;
                if (!experimentIds.Contains(proof.ExperimentId))
                    Add(section.Id, section.Revision, "lean_experiment_missing", "experimentIds", proof.ExperimentId,
                        $"Abschnitt {section.Id}: Ordne zum Lean-Beleg {check.Id} auch seinen echten Versuch {proof.ExperimentId} unter experimentIds zu.");
                if (!Descriptions(related, "proofDescriptions").Any(description => Text(description, "checkId") == check.Id && Text(description, "description").Length > 0))
                    Add(section.Id, section.Revision, "lean_explanation_missing", "proofDescriptions", check.Id,
                        $"Abschnitt {section.Id}: Ergänze proofDescriptions mit checkId={check.Id} und description zu verständlicher Aussage, Voraussetzungen, Beweisidee und Grenzen.");
            }
        }
        return issues;

        void Add(string id, long revision, string code, string field, string value, string message) =>
            issues.Add(new(id, revision, code, "data." + field, value, [], message,
                message + " Nur die betroffenen Metadaten ändern, vorhandenen Text erhalten; danach research.deliverables.verify erneut ausführen."));
    }

    private static ResearchWorkingItem[] Related(ResearchWorkingItem section, ResearchWorkingState state)
    {
        var selected = new List<ResearchWorkingItem> { section };
        var seen = new HashSet<string>(StringComparer.Ordinal) { section.Id };
        var pending = new Queue<string>(Ids(section.Data, "claimIds").Concat(Ids(section.Data, "hypothesisIds")));
        while (pending.TryDequeue(out var id))
        {
            if (!seen.Add(id)) continue;
            var item = state.Items.FirstOrDefault(item => item.Id == id && item.Kind is "claim" or "hypothesis");
            if (item is null) continue;
            selected.Add(item);
            foreach (var dependency in Ids(item.Data, "claimIds").Concat(Ids(item.Data, "hypothesisIds"))) pending.Enqueue(dependency);
        }
        return selected.ToArray();
    }
    private static IEnumerable<JsonElement> Descriptions(IEnumerable<ResearchWorkingItem> items, string field) => items
        .Where(item => item.Data.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array)
        .SelectMany(item => item.Data.GetProperty(field).EnumerateArray());
    private static IEnumerable<string> Ids(JsonElement data, string field) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var values)
        && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!) : [];
    private static string Text(JsonElement data, string field) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

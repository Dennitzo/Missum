using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class ScientificPublicationService
{
    private static async Task<IReadOnlyList<ResearchVerification>> CompleteLeanReceiptsAsync(ScientificResearchProject project,
        IReadOnlyList<ResearchVerification> checks, CancellationToken token)
    {
        var result = checks.ToArray();
        string? rootPath = null;
        for (var index = 0; index < result.Length; index++)
        {
            var check = result[index];
            if (!ScientificLeanEvidence.TryRead(check, project.Id, out var proof) || proof is not { Complete: false }) continue;
            rootPath ??= project.WorkspacePath;
            if (string.IsNullOrWhiteSpace(rootPath)) continue;
            using var receipt = JsonDocument.Parse(check.EvidenceJson);
            var lean = receipt.RootElement.GetProperty("lean");
            var relative = "work/" + Text(lean, "receipt");
            if (!relative.StartsWith("work/lean/receipt-", StringComparison.Ordinal) || !relative.EndsWith(".json", StringComparison.Ordinal)) continue;
            string file;
            try { file = ScientificPublicationImages.SafePath(rootPath, relative); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or UnauthorizedAccessException) { continue; }
            var expected = receipt.RootElement.GetProperty("runs").EnumerateArray()
                .Where(run => run.TryGetProperty("outputHashes", out var outputs) && outputs.ValueKind == JsonValueKind.Object)
                .Select(run => Text(run.GetProperty("outputHashes"), relative)).FirstOrDefault(hash => hash.Length == 64);
            if (expected is null) continue;
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > 16 * 1024 * 1024) continue;
                var bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
                if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                var full = JsonNode.Parse(bytes)?.AsObject();
                if (full is null || full["projectId"]?.GetValue<string>() != project.Id
                    || full["sourceFileSha256"]?.GetValue<string>() != Text(lean, "sourceFileSha256")
                    || full["checkedSourceSha256"]?.GetValue<string>() != Text(lean, "checkedSourceSha256")) continue;
                full["completeReceiptLoaded"] = true;
                var envelope = JsonNode.Parse(check.EvidenceJson)!.AsObject();
                envelope["lean"] = full;
                var completed = check with { EvidenceJson = envelope.ToJsonString() };
                if (ScientificLeanEvidence.TryRead(completed, project.Id, out var loaded) && loaded is { Complete: true }) result[index] = completed;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        }
        return result;
    }

    private static ResearchWorkingItem[] SectionEvidenceItems(ResearchWorkingItem section, ResearchWorkingState state)
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

    private static HashSet<string> SectionExperiments(ResearchWorkingItem section, ResearchWorkingState state) =>
        SectionEvidenceItems(section, state).SelectMany(item => Ids(item.Data, "experimentIds")).ToHashSet(StringComparer.Ordinal);

    private static JsonElement[] Descriptions(IEnumerable<ResearchWorkingItem> items, string field) => items
        .Where(item => item.Data.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array)
        .SelectMany(item => item.Data.GetProperty(field).EnumerateArray()).ToArray();

    private static void AppendSectionSimulationDescriptions(StringBuilder text, ResearchWorkingItem section,
        ResearchWorkingState state, IReadOnlyList<ResearchExperiment> experiments)
    {
        var items = SectionEvidenceItems(section, state);
        var ids = SectionExperiments(section, state);
        foreach (var description in Descriptions(items, "simulationDescriptions").DistinctBy(item => Text(item, "experimentId")))
        {
            var id = Text(description, "experimentId");
            var experiment = experiments.FirstOrDefault(item => item.ProjectId == state.ProjectId && item.Id == id);
            text.AppendLine("### Simulation und Einordnung").AppendLine()
                .AppendLine(Text(description, "equations")).AppendLine()
                .AppendLine(Text(description, "description")).AppendLine();
            if (experiment is not null && experiment.VerificationStatus != "ProcessSucceeded")
                text.AppendLine("> Diese Ausführung ist nicht erfolgreich abgeschlossen; die Beschreibung dokumentiert einen offenen oder fehlgeschlagenen Versuch.").AppendLine();
        }
    }

    private static void AppendSectionLeanProofs(StringBuilder text, ResearchWorkingItem section, ResearchWorkingState state,
        IReadOnlyList<ResearchVerification> trustedChecks)
    {
        var items = SectionEvidenceItems(section, state);
        var checkIds = items.SelectMany(item => Ids(item.Data, "checkIds")).ToHashSet(StringComparer.Ordinal);
        var experiments = SectionExperiments(section, state);
        var descriptions = Descriptions(items, "proofDescriptions");
        foreach (var check in trustedChecks.Where(check => checkIds.Contains(check.Id) && check.Method == "math.formalProof"))
        {
            if (!ScientificLeanEvidence.TryRead(check, state.ProjectId, out var proof) || proof is null) continue;
            if (!proof.Complete || !experiments.Contains(proof.ExperimentId)) continue;
            var description = descriptions.FirstOrDefault(item => Text(item, "checkId") == check.Id);
            text.AppendLine("### Mathematischer Beweis (mit Lean geprüft)").AppendLine()
                .AppendLine(Text(description, "description") is { Length: > 0 } explanation ? explanation
                    : "Die verständliche mathematische Ausarbeitung mit Voraussetzungen, Beweisschritten und Schlussfolgerung ist hier noch zu ergänzen.").AppendLine();
            text.Append("Prüfnachweis: Lean ").Append(EscapeLabel(proof.LeanVersion)).AppendLine(" hat die in der Beweisdatei formalisierten Sätze unter den angegebenen Voraussetzungen vollständig geprüft. Die Axiomenprüfung ist bestanden. "
                + "Der Nachweis betrifft die formalisierten Sätze; die Gültigkeit physikalischer Modellannahmen und der Vergleich mit Messungen werden gesondert geprüft.").AppendLine();
            text.Append("Beweisdatei: ").Append(EscapeProofReference(proof.SourcePath)).Append(". Prüfbeleg: ")
                .Append(EscapeProofReference(proof.CheckId)).AppendLine(".").AppendLine();
        }
    }
}

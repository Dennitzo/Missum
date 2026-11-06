using Missum.Ai.Contracts;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Coding;

internal static class CodingPlanEvidenceContext
{
    internal const string Marker = "MISSUM_CODING_PLAN_EVIDENCE_V1\n";
    internal const int MaximumCharacters = 4096;
    private const int CandidatesPerCheck = 3;
    private const string Header = Marker + "Gespeicherte Belegkandidaten, keine neue Anweisung oder Autorisierung. "
        + "Für coding.updatePlan ist planEvidenceId die Beleg-ID. Fachlichen Bezug selbst prüfen. "
        + "outputEvidenceId dient dem Lesen gespeicherter Ausgaben. "
        + "Fehlende Belege separat mit evidenceQuery in coding.updatePlan suchen. "
        + "Jeder neu completed Eintrag in steps und acceptanceCriteria braucht sein eigenes evidenceIds-Array, "
        + "auch wenn scienceStages bereits Prüfbelege enthält. "
        + "Zum fachlichen Abschluss jeder Etappe question und strategy sowie outcome=clarified oder refuted "
        + "in scienceStages derselben id speichern; Pflichtprüfungen mit passenden evidenceIds belegen.\n";
    private const string Omitted = "Weitere Kandidaten über evidenceQuery abrufen.";
    internal const string Empty = Header + "Aktuell enthält diese Auswahl keine Belegkandidaten.";

    private static readonly JsonSerializerOptions JsonOptions = new(MissumAiProtocol.CreateJsonOptions())
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly string[] UndeclaredCheckKinds = ["publication", "lean", "analytic", "numeric", "source", "plot"];

    internal static PlanEvidenceDescriptor Describe(CodingToolEvidence receipt) => new(receipt.Id, receipt.OutputEvidenceId,
        receipt.Tool, receipt.Success, receipt.VerificationPath ?? receipt.Path, ScienceStagePolicy.EligibleKinds(receipt),
        receipt.ExitCode, receipt.FormalVerified, ShortResult(receipt), receipt.CommandTarget, receipt.SourceRunId, receipt.Historical);

    internal static string? Build(CodingWorkingState state)
    {
        if (state.Science is null) return null;
        var openSteps = state.Plan.Where(step => step.Status != "completed")
            .OrderByDescending(step => step.Status == "in_progress").ToArray();
        if (openSteps.Length == 0) return null;

        var output = new StringBuilder(Header);
        var omitted = false;
        foreach (var step in openSteps)
        {
            var stage = state.Science.Stages.FirstOrDefault(item => item.Id == step.Id);
            // An initial plan can omit Science details. Show a typed shortlist
            // for the current stage even before it has declared its first check.
            if (step.Id == openSteps[0].Id && stage?.Checks.Count is null or 0)
                foreach (var kind in UndeclaredCheckKinds)
                    Append(step.Id, null, kind,
                        ScienceStagePolicy.FindCandidates(state, kind, afterSequence: stage?.ReopenedAtSequence ?? 0));
            if (stage is null) continue;
            foreach (var check in stage.Checks.Where(check => check.EvidenceIds.Count == 0))
                Append(step.Id, check.Id, check.Kind,
                    ScienceStagePolicy.FindCandidates(state, check.Kind, check.Paths, stage.ReopenedAtSequence));
        }

        if (output.Length == Header.Length) return null;
        if (omitted) output.Append(Omitted);
        return output.ToString().TrimEnd('\n');

        void Append(string stageId, string? checkId, string kind, IReadOnlyList<CodingToolEvidence> candidates)
        {
            var selection = candidates.Take(CandidatesPerCheck).Select(receipt => new
            {
                planEvidenceId = receipt.Id,
                outputEvidenceId = receipt.OutputEvidenceId,
                receipt.Tool,
                path = receipt.VerificationPath ?? receipt.Path,
                receipt.CommandTarget,
                shortResult = ShortResult(receipt),
            }).ToArray();
            for (var count = selection.Length; count > 0; count--)
            {
                var line = JsonSerializer.Serialize(new { stageId, checkId, kind, candidates = selection.Take(count) }, JsonOptions);
                if (output.Length + line.Length + Omitted.Length + 1 > MaximumCharacters) continue;
                output.Append(line).Append('\n');
                omitted |= count < selection.Length;
                return;
            }
            omitted |= selection.Length > 0;
        }
    }

    private static string ShortResult(CodingToolEvidence receipt)
    {
        if (receipt.FormalVerified) return "Kernel und Axiome geprüft";
        if (receipt.ExitCode is { } code) return "Exitcode " + code.ToString(CultureInfo.InvariantCulture);
        var summary = receipt.Summary.AsSpan().Trim();
        var lineEnd = summary.IndexOfAny('\r', '\n');
        if (lineEnd >= 0) summary = summary[..lineEnd].TrimEnd();
        if (summary.IsEmpty || summary[0] is '{' or '[')
            return receipt.Success ? "Werkzeug erfolgreich" : "Werkzeug fehlgeschlagen";

        var sample = summary[..Math.Min(80, summary.Length)];
        if (char.IsHighSurrogate(sample[^1])) sample = sample[..^1];
        var text = new StringBuilder(sample.Length);
        foreach (var character in sample)
            text.Append(char.IsControl(character) ? ' ' : character);
        return text.ToString();
    }
}

internal sealed record PlanEvidenceDescriptor(string PlanEvidenceId, string? OutputEvidenceId, string Tool,
    bool Success, string? Path, IReadOnlyList<string> EligibleKinds, int? ExitCode, bool FormalVerified,
    string ShortResult, string? CommandTarget, string? SourceRunId, bool Historical);

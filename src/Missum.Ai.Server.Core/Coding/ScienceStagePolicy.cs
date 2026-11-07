using System.Text.Json;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Coding;

internal sealed record PlanValidationIssue(string Code, string Field, string? StageId = null, string? CheckId = null,
    string? Kind = null, IReadOnlyList<string>? Paths = null, string? Message = null);

internal sealed class PlanValidationException(string message, params PlanValidationIssue[] issues)
    : ArgumentException(message)
{
    public IReadOnlyList<PlanValidationIssue> Issues { get; } = issues;
}

internal static class ScienceStagePolicy
{
    internal static CodingWorkingState Apply(CodingWorkingState before, CodingWorkingState updated, JsonElement args)
    {
        if (before.Science is null)
        {
            if (args.TryGetProperty("scienceStages", out _)) throw new ArgumentException("scienceStages steht ausschließlich mit aktiviertem Science-Skill zur Verfügung.");
            return updated;
        }
        var stages = before.Science.Stages.ToList();
        if (args.TryGetProperty("scienceStages", out var supplied))
        {
            if (supplied.ValueKind != JsonValueKind.Array || supplied.GetArrayLength() is < 1 or > 16) Fail("scienceStages benötigt 1–16 Änderungen.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in supplied.EnumerateArray())
            {
                Fields(item, ["id", "question", "strategy", "dependsOn", "checks", "openQuestions", "blockers", "outcome"]);
                var id = Text(item, "id", 80);
                if (!ids.Add(id) || !updated.Plan.Any(step => step.Id == id))
                    Reject($"Science-Etappe {id}: eindeutige vorhandene steps-ID erforderlich; steps darf im selben Aufruf angelegt werden.",
                        new PlanValidationIssue("invalid_stage_id", "scienceStages.id", id));
                var prior = stages.FirstOrDefault(stage => stage.Id == id);
                var question = item.TryGetProperty("question", out _) ? Text(item, "question", 800) : prior?.Question ?? "";
                var strategy = item.TryGetProperty("strategy", out _) ? Text(item, "strategy", 1200) : prior?.Strategy ?? "";
                if (prior is not null && updated.Plan.Single(step => step.Id == id).Status == "completed"
                    && before.Plan.Any(step => step.Id == id && step.Status == "completed")
                    && (prior.Question != question || prior.Strategy != strategy))
                    Reject($"Etappe {id}: geänderte Leitfrage oder Prüfstrategie benötigt zuerst einen begründeten Rücksprung.",
                        new PlanValidationIssue("stage_reopen_required", "steps.status", id));
                var checks = prior?.Checks.ToList() ?? [];
                if (item.TryGetProperty("checks", out var suppliedChecks))
                {
                    if (suppliedChecks.ValueKind != JsonValueKind.Array || suppliedChecks.GetArrayLength() > 8) Fail("checks benötigt höchstens acht Einträge.");
                    var checkIds = new HashSet<string>();
                    foreach (var check in suppliedChecks.EnumerateArray())
                    {
                        Fields(check, ["id", "kind", "required", "paths"]);
                        var checkId = Text(check, "id", 80); var kind = Text(check, "kind", 20);
                        if (!checkIds.Add(checkId) || kind is not ("source" or "analytic" or "lean" or "numeric" or "plot" or "publication")) Fail("Check benötigt eindeutige id und gültigen kind.");
                        if (!check.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("Check.required muss true oder false sein.");
                        var old = checks.FirstOrDefault(value => value.Id == checkId);
                        if (old is { Required: true } && (!required.GetBoolean() || old.Kind != kind)) Fail($"Pflichtprüfung {id}/{checkId} darf nicht stillschweigend abgeschwächt oder ersetzt werden.");
                        var paths = Strings(check, "paths", 16, 1024, old?.Paths ?? []).Select(NormalizeProjectPath).ToArray();
                        if (paths.Any(path => path is null))
                            Reject("Prüfpfade müssen relative Projektpfade ohne .. oder .git sein.",
                                new PlanValidationIssue("invalid_check_paths", "scienceStages.checks.paths", id, checkId, kind));
                        var normalizedPaths = paths.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        checks.RemoveAll(value => value.Id == checkId);
                        checks.Add(new(checkId, kind, required.GetBoolean(), normalizedPaths));
                    }
                }
                if (checks.Count > 8) Fail("Eine Etappe darf höchstens acht gespeicherte Prüfungen enthalten.");
                var outcome = item.TryGetProperty("outcome", out _) ? Text(item, "outcome", 20) : prior?.Outcome ?? "open";
                if (outcome is not ("open" or "clarified" or "refuted"))
                    Reject("outcome muss open, clarified oder refuted sein.",
                        new PlanValidationIssue("invalid_stage_outcome", "scienceStages.outcome", id));
                if (checks.SelectMany(check => check.Paths).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 16) Fail("Eine Etappe darf höchstens 16 unterschiedliche Prüfdateien binden; in Teilaufgaben zerlegen.");
                var stage = new ScienceStage(id, question, strategy, Strings(item, "dependsOn", 16, 80, prior?.DependsOn ?? []), checks,
                    Strings(item, "openQuestions", 12, 600, prior?.OpenQuestions ?? []), Strings(item, "blockers", 12, 600, prior?.Blockers ?? []), outcome,
                    DetailsPending: string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(strategy));
                stages.RemoveAll(value => value.Id == id); stages.Add(stage);
            }
        }
        // Planning can precede investigation. Keep the complete user-facing plan
        // immediately, while explicitly recording which scientific details have
        // not yet been chosen. No question, strategy or required check is invented.
        foreach (var step in updated.Plan.Where(step => !stages.Any(stage => stage.Id == step.Id)))
            stages.Add(new(step.Id, "", "", [], [], [], [], DetailsPending: true));
        foreach (var stage in stages)
        {
            if (stage.DependsOn.Any(id => id == stage.Id || !stages.Any(other => other.Id == id))) Fail($"Etappe {stage.Id}: ungültige Abhängigkeit.");
            Visit(stage.Id, []);
        }
        void Visit(string id, HashSet<string> visited)
        {
            if (!visited.Add(id)) Fail("Etappenabhängigkeiten enthalten einen Zyklus.");
            foreach (var parent in stages.Single(stage => stage.Id == id).DependsOn) Visit(parent, new(visited));
        }
        var reopened = before.Plan.Where(old => old.Status == "completed" && updated.Plan.Any(now => now.Id == old.Id && now.Status != "completed")).Select(step => step.Id).ToHashSet();
        if (reopened.Count > 0 && !args.TryGetProperty("explanation", out _))
            Reject("Rücksprung benötigt eine konkrete explanation.",
                reopened.Select(id => new PlanValidationIssue("missing_reopen_explanation", "explanation", id)).ToArray());
        var affected = new HashSet<string>(reopened);
        bool grew;
        do { grew = false; foreach (var stage in stages) if (stage.DependsOn.Any(affected.Contains)) grew |= affected.Add(stage.Id); } while (grew);
        if (affected.Count > 0)
        {
            updated = updated with { Plan = updated.Plan.Select(step => affected.Contains(step.Id) && !reopened.Contains(step.Id) ? step with { Status = "pending" } : step).ToArray() };
            stages = stages.Select(stage => affected.Contains(stage.Id) ? stage with { Outcome = "open" } : stage).ToList();
        }
        updated = updated with { Science = before.Science with { Stages = stages } };
        var issues = new List<PlanValidationIssue>();
        foreach (var stage in stages)
        {
            var step = updated.Plan.Single(step => step.Id == stage.Id);
            if (step.Status is "in_progress" or "completed" && stage.DependsOn.Any(id => updated.Plan.Single(step => step.Id == id).Status != "completed"))
                issues.Add(new("stage_dependencies_open", "scienceStages.dependsOn", stage.Id,
                    Message: $"Etappe {stage.Id}: abhängige Etappe ist noch nicht freigegeben."));
            if (step.Status != "completed") continue;
            if (stage.DetailsPending || string.IsNullOrWhiteSpace(stage.Question) || string.IsNullOrWhiteSpace(stage.Strategy))
                issues.AddRange(new[] { (Field: "question", Value: stage.Question), (Field: "strategy", Value: stage.Strategy) }
                        .Where(item => string.IsNullOrWhiteSpace(item.Value))
                        .Select(item => new PlanValidationIssue("missing_stage_details", "scienceStages." + item.Field, stage.Id,
                            Message: $"Etappe {stage.Id}: question oder strategy fehlt. Sende Leitfrage und Prüfstrategie ausdrücklich in scienceStages erneut mit.")));
            if (stage.Outcome == "open" || stage.Blockers.Count > 0)
                issues.Add(new("stage_outcome_open", stage.Blockers.Count > 0 ? "scienceStages.blockers" : "scienceStages.outcome", stage.Id,
                    Message: stage.Blockers.Count > 0
                        ? $"Etappe {stage.Id}: dokumentierte Blockaden verhindern completed. Halte die offene Etappe pending oder bearbeite die Blockaden."
                        : $"Etappe {stage.Id}: Für completed ist scienceStages.outcome ausdrücklich clarified oder refuted erforderlich. Ist das Ergebnis offen, bleibt die Etappe pending."));
        }
        if (issues.Count > 0)
            Reject(string.Join(" ", issues.Select(issue => issue.Message).Where(message => message is not null).Distinct()), issues.ToArray());
        return updated;

    }

    internal static string? NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) return null;
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part == ".." || part.Equals(".git", StringComparison.OrdinalIgnoreCase))) return null;
        var relative = string.Join('/', parts.Where(part => part != "."));
        return relative.Length == 0 ? null : relative;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject(string message, params PlanValidationIssue[] issues) => throw new PlanValidationException(
        message + " Das gesamte Planupdate wurde abgelehnt.", issues.Select(issue => issue with { Message = issue.Message ?? message }).ToArray());

    internal static CodingToolEvidence BindReceipt(CodingWorkingState state, CodingToolEvidence receipt, JsonElement result)
    {
        var proof = FindProof(result);
        var verified = proof.ValueKind == JsonValueKind.Object && TextOrNull(proof, "formalVerification") == "KernelAccepted" && TextOrNull(proof, "axiomAudit") == "passed";
        var proofPath = verified ? ResolveProofPath(result, proof) : null;
        return receipt with { Success = receipt.Success && !(receipt.Tool is "math.numeric" or "math.symbolic" or "math.smt" && HasFailedMathProcess(result)),
            FormalVerified = receipt.Tool == "math.formalProof" && verified,
            VerificationPath = proofPath };
    }

    private static bool HasFailedMathProcess(JsonElement value, int depth = 0)
    {
        if (depth > 6 || value.ValueKind != JsonValueKind.Object) return false;
        if (TextOrNull(value, "verificationStatus") == "ProcessFailed"
            || value.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
            || value.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var code) && code != 0)
            return true;
        if (value.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array
            && runs.EnumerateArray().Any(run => HasFailedMathProcess(run, depth + 1))) return true;
        foreach (var name in new[] { "result", "data" })
            if (value.TryGetProperty(name, out var nested) && HasFailedMathProcess(nested, depth + 1)) return true;
        return false;
    }

    internal static CodingWorkingState ImportDelegatedEvidence(CodingWorkingState state, JsonElement trustedResult)
    {
        if (trustedResult.ValueKind != JsonValueKind.Object
            || !trustedResult.TryGetProperty("completionEvidence", out var list) || list.ValueKind != JsonValueKind.Array
            || !trustedResult.TryGetProperty("runId", out var run) || run.ValueKind != JsonValueKind.String) return state;
        var calls = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        var child = CodingWorkingState.Create("Delegierte Belege") with { Science = new(ScienceSkill.Profile, []) };
        foreach (var entry in list.EnumerateArray())
        {
            var message = entry.Deserialize<LmChatMessage>(Missum.Ai.Contracts.MissumAiProtocol.CreateJsonOptions());
            if (message is null) continue;
            foreach (var call in message.ToolCalls ?? []) calls[call.Id] = call;
            if (message.Role == "tool" && message.ToolCallId is { } id && calls.TryGetValue(id, out var completed))
                child = CodingWorkingStateReducer.ObserveToolResult(child, completed, message.Content ?? "{}");
        }
        var existing = state.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var additions = child.Evidence.Where(item => item.Success && !existing.Contains(item.Id)).Select(item => item with
        { SourceRunId = run.GetString(), Historical = true, Sequence = state.Sequence + 1 }).ToArray();
        if (additions.Length == 0) return state;
        return state with { Evidence = CodingWorkingStateReducer.KeepEvidence(state.Evidence.Concat(additions)), Sequence = state.Sequence + 1 };

    }
    private static JsonElement FindProof(JsonElement value, int depth = 0)
    {
        if (depth > 6 || value.ValueKind != JsonValueKind.Object) return default;
        if (value.TryGetProperty("formalVerification", out _) && value.TryGetProperty("axiomAudit", out _)) return value;
        foreach (var name in new[] { "result", "lean", "proof", "data" })
            if (value.TryGetProperty(name, out var nested) && FindProof(nested, depth + 1) is { ValueKind: JsonValueKind.Object } found) return found;
        return default;
    }
    private static string? ResolveProofPath(JsonElement result, JsonElement proof, int depth = 0)
    {
        var source = TextOrNull(proof, "source")?.Replace('\\', '/');
        if (source is null || depth > 6) return source;
        if (result.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "result", "data" })
                if (result.TryGetProperty(name, out var nested)) return ResolveProofPath(nested, proof, depth + 1);
        }
        return source;
    }
    private static string? TextOrNull(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Text(JsonElement item, string name, int maximum) => TextOrNull(item, name) is { Length: > 0 } text && text.Length <= maximum && !string.IsNullOrWhiteSpace(text) ? text : throw new ArgumentException($"{name} benötigt 1–{maximum} Zeichen.");
    private static IReadOnlyList<string> Strings(JsonElement item, string name, int count, int length, IReadOnlyList<string> fallback)
    {
        if (!item.TryGetProperty(name, out var array)) return fallback;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > count) throw new ArgumentException($"{name} benötigt ein Array mit höchstens {count} Einträgen.");
        var values = array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text && text.Length <= length ? text : throw new ArgumentException($"{name} enthält ungültigen Text.")).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) Fail($"{name} enthält doppelte Einträge.");
        return values;
    }
    private static void Fields(JsonElement item, string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(field => !allowed.Contains(field.Name, StringComparer.Ordinal))) Fail("Unbekanntes Science-Feld oder ungültiges Objekt.");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new ArgumentException(message + " Das gesamte Planupdate wurde abgelehnt.");
}

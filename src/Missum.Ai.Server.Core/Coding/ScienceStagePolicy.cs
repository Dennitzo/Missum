using System.Text.Json;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Coding;

internal static class ScienceStagePolicy
{
    internal static CodingWorkingState Apply(CodingWorkingState before, CodingWorkingState updated, JsonElement args, bool verifyReceipts = true)
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
                if (!ids.Add(id) || !updated.Plan.Any(step => step.Id == id)) Fail($"Science-Etappe {id}: eindeutige vorhandene steps-ID erforderlich; steps darf im selben Aufruf angelegt werden.");
                var prior = stages.FirstOrDefault(stage => stage.Id == id);
                var question = item.TryGetProperty("question", out _) ? Text(item, "question", 800) : prior?.Question ?? "";
                var strategy = item.TryGetProperty("strategy", out _) ? Text(item, "strategy", 1200) : prior?.Strategy ?? "";
                if (prior is not null && updated.Plan.Single(step => step.Id == id).Status == "completed"
                    && before.Plan.Any(step => step.Id == id && step.Status == "completed")
                    && (prior.Question != question || prior.Strategy != strategy))
                    Fail($"Etappe {id}: geänderte Leitfrage oder Prüfstrategie benötigt zuerst einen begründeten Rücksprung.");
                var checks = prior?.Checks.ToList() ?? [];
                if (item.TryGetProperty("checks", out var suppliedChecks))
                {
                    if (suppliedChecks.ValueKind != JsonValueKind.Array || suppliedChecks.GetArrayLength() > 8) Fail("checks benötigt höchstens acht Einträge.");
                    var checkIds = new HashSet<string>();
                    foreach (var check in suppliedChecks.EnumerateArray())
                    {
                        Fields(check, ["id", "kind", "required", "evidenceIds", "paths"]);
                        var checkId = Text(check, "id", 80); var kind = Text(check, "kind", 20);
                        if (!checkIds.Add(checkId) || kind is not ("source" or "analytic" or "lean" or "numeric" or "plot" or "publication")) Fail("Check benötigt eindeutige id und gültigen kind.");
                        if (!check.TryGetProperty("required", out var required) || required.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail("Check.required muss true oder false sein.");
                        var old = checks.FirstOrDefault(value => value.Id == checkId);
                        if (old is { Required: true } && (!required.GetBoolean() || old.Kind != kind)) Fail($"Pflichtprüfung {id}/{checkId} darf nicht stillschweigend abgeschwächt oder ersetzt werden.");
                        var evidence = Strings(check, "evidenceIds", 3, 100, old?.EvidenceIds ?? []).Select(id =>
                            CodingWorkingStateReducer.ResolveEvidence(before, id)?.Id ?? throw new ArgumentException($"Unbekannte evidenceId {id}; keine IDs erfinden.")).ToArray();
                        var paths = Strings(check, "paths", 16, 1024, old?.Paths ?? []);
                        if (paths.Any(path => Path.IsPathRooted(path) || path.Contains(':') || path.Replace('\\', '/').Split('/').Any(part => part is ".." or ".git"))) Fail("Prüfpfade müssen relative Projektpfade ohne .. sein.");
                        checks.RemoveAll(value => value.Id == checkId);
                        checks.Add(new(checkId, kind, required.GetBoolean(), evidence, paths.Select(path => path.Replace('\\', '/')).ToArray()));
                    }
                }
                if (checks.Count > 8) Fail("Eine Etappe darf höchstens acht gespeicherte Prüfungen enthalten.");
                var outcome = item.TryGetProperty("outcome", out _) ? Text(item, "outcome", 20) : prior?.Outcome ?? "open";
                if (outcome is not ("open" or "clarified" or "refuted")) Fail("outcome muss open, clarified oder refuted sein.");
                if (checks.SelectMany(check => check.Paths).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 16) Fail("Eine Etappe darf höchstens 16 unterschiedliche Prüfdateien binden; in Teilaufgaben zerlegen.");
                var stage = new ScienceStage(id, question, strategy, Strings(item, "dependsOn", 16, 80, prior?.DependsOn ?? []), checks,
                    Strings(item, "openQuestions", 12, 600, prior?.OpenQuestions ?? []), Strings(item, "blockers", 12, 600, prior?.Blockers ?? []), outcome, prior?.ReopenedAtSequence ?? 0,
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
        if (reopened.Count > 0 && !args.TryGetProperty("explanation", out _)) Fail("Rücksprung benötigt eine konkrete explanation.");
        var affected = new HashSet<string>(reopened);
        bool grew;
        do { grew = false; foreach (var stage in stages) if (stage.DependsOn.Any(affected.Contains)) grew |= affected.Add(stage.Id); } while (grew);
        if (affected.Count > 0)
        {
            updated = updated with { Plan = updated.Plan.Select(step => affected.Contains(step.Id) && !reopened.Contains(step.Id) ? step with { Status = "pending", EvidenceIds = [] } : step).ToArray() };
            stages = stages.Select(stage => affected.Contains(stage.Id) ? stage with
            { Outcome = "open", ReopenedAtSequence = updated.Sequence, Checks = stage.Checks.Select(check => check with { EvidenceIds = [] }).ToArray() } : stage).ToList();
        }
        updated = updated with { Science = before.Science with { Stages = stages } };
        foreach (var stage in stages)
        {
            var step = updated.Plan.Single(step => step.Id == stage.Id);
            if (step.Status is "in_progress" or "completed" && stage.DependsOn.Any(id => updated.Plan.Single(step => step.Id == id).Status != "completed")) Fail($"Etappe {stage.Id}: abhängige Etappe ist noch nicht freigegeben.");
            if (step.Status != "completed")
            {
                // Assigning a receipt is already a verifiable claim. Do not
                // acknowledge an invalid kind as saved, then reject it only on
                // closure; that produced misleading review/update loops.
                if (verifyReceipts)
                    foreach (var check in stage.Checks.Where(check => check.EvidenceIds.Count > 0))
                        ValidateCheck(updated, stage, check);
                continue;
            }
            if (stage.DetailsPending || string.IsNullOrWhiteSpace(stage.Question) || string.IsNullOrWhiteSpace(stage.Strategy))
                Fail($"Etappe {stage.Id}: question oder strategy ist noch nicht festgelegt. "
                    + "Ergänze die fehlende Leitfrage und Prüfstrategie ausdrücklich in scienceStages, bevor die Etappe completed wird.");
            if (stage.Outcome == "open" || stage.Blockers.Count > 0) Fail($"Etappe {stage.Id}: offene/blockierte Etappe nicht als completed melden; pending mit blockers verwenden.");
            if (verifyReceipts)
                foreach (var check in stage.Checks.Where(check => check.Required || check.EvidenceIds.Count > 0))
                    ValidateCheck(updated, stage, check);
        }
        return updated;
    }

    internal static void ValidateCheck(CodingWorkingState state, ScienceStage stage, ScienceCheck check)
    {
        if (check.EvidenceIds.Count == 0) Fail($"Etappe {stage.Id}: Pflichtprüfung {check.Id} hat keine evidenceIds.");
        foreach (var id in check.EvidenceIds)
        {
            var receipt = CodingWorkingStateReducer.ResolveEvidence(state, id);
            if (receipt is not { Success: true } || !Matches(check.Kind, receipt))
            {
                var candidates = state.Evidence.Where(item => item.Success && Matches(check.Kind, item) && item.Sequence > stage.ReopenedAtSequence
                    && (check.Kind != "publication" || check.Paths.Count == 0 || item.Path is { } path && check.Paths.Any(expected => CodingWorkingStateReducer.SamePath(expected, path))))
                    .TakeLast(3).Select(item => new { id = item.Id, tool = item.Tool, path = item.Path }).ToArray();
                Fail($"Etappe {stage.Id}, Check {check.Id}: Ersetze evidenceIds, nicht nur paths oder phase. {id} stammt von {receipt?.Tool ?? "unbekannt"} und ist kein erfolgreicher {check.Kind}-Nachweis. "
                    + (candidates.Length > 0 ? "Vorhandene passende Werkzeugbelege (fachliche Zuordnung selbst prüfen): " + JsonSerializer.Serialize(candidates)
                        : "Kein passender erfolgreicher Beleg vorhanden; die gewählte Prüfung tatsächlich ausführen und ihre Beleg-ID verwenden.")
                    + (check.Kind == "publication" && receipt?.Tool == "coding.command"
                        ? " Ein erfolgreicher PDF-Befehl bestätigt den Prozess, aber noch nicht den Publikationsinhalt. Lies oder bearbeite die tatsächlich betroffene Publikation und binde deren Beleg; keinen beliebigen Prozess als Publikationsprüfung umdeuten."
                        : ""));
            }
            if (receipt!.Sequence <= stage.ReopenedAtSequence) Fail($"Etappe {stage.Id}: {check.Id} benötigt einen neuen Nachweis nach dem Rücksprung.");
            if (check.Kind == "publication" && check.Paths.Count > 0
                && (receipt.Path is null || !check.Paths.Any(path => CodingWorkingStateReducer.SamePath(path, receipt.Path))))
                Fail($"Etappe {stage.Id}: {check.Id}, Beleg {id} gehört nicht zur angegebenen Publikation.");
        }
    }

    private static bool Matches(string kind, CodingToolEvidence receipt) => kind switch
    {
        "lean" => receipt.Tool == "math.formalProof" && receipt.FormalVerified,
        "numeric" => receipt.Tool is "math.numeric" or "math.evaluate" || receipt.Tool == "coding.command" && receipt.ExitCode == 0,
        "analytic" => receipt.Tool is "math.symbolic" or "math.smt" or "math.evaluate" || receipt.Tool == "coding.command" && receipt.ExitCode == 0 || receipt.Tool == "math.formalProof" && receipt.FormalVerified,
        "source" => receipt.Tool is "web.fetch" or "coding.read" or "documents.readPages",
        "plot" => receipt.Tool == "coding.command" && receipt.ExitCode == 0 || receipt.Tool == "image.generate",
        "publication" => receipt.Tool is "coding.edit" or "coding.write" or "document.create" or "coding.read",
        _ => false,
    };

    internal static CodingWorkingState Revalidate(CodingWorkingState state)
    {
        if (state.Science is null) return state;
        var invalid = new HashSet<string>();
        foreach (var stage in state.Science.Stages.Where(stage => state.Plan.Any(step => step.Id == stage.Id && step.Status == "completed")))
        {
            if (stage.DetailsPending || string.IsNullOrWhiteSpace(stage.Question) || string.IsNullOrWhiteSpace(stage.Strategy))
                invalid.Add(stage.Id);
            foreach (var check in stage.Checks.Where(check => check.Required || check.EvidenceIds.Count > 0))
            {
                try { ValidateCheck(state, stage, check); }
                catch (ArgumentException) { invalid.Add(stage.Id); }
            }
        }
        bool grew;
        do { grew = false; foreach (var stage in state.Science.Stages) if (stage.DependsOn.Any(invalid.Contains)) grew |= invalid.Add(stage.Id); } while (grew);
        if (invalid.Count == 0) return state;
        return state with
        {
            Plan = state.Plan.Select(step => invalid.Contains(step.Id) ? step with { Status = "pending", EvidenceIds = [] } : step).ToArray(),
            Science = state.Science with { Stages = state.Science.Stages.Select(stage => invalid.Contains(stage.Id) ? stage with
            { Outcome = "open", ReopenedAtSequence = state.Sequence, Checks = stage.Checks.Select(check => check with { EvidenceIds = [] }).ToArray() } : stage).ToArray() },
        };
    }

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
        if (state.Science is null || trustedResult.ValueKind != JsonValueKind.Object
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
        return state with { Evidence = CodingWorkingStateReducer.KeepEvidence(state, state.Evidence.Concat(additions)), Sequence = state.Sequence + 1 };

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

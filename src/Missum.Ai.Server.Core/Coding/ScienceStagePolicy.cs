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
                var question = item.TryGetProperty("question", out _) || prior is null ? Text(item, "question", 800) : prior.Question;
                var strategy = item.TryGetProperty("strategy", out _) || prior is null ? Text(item, "strategy", 1200) : prior.Strategy;
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
                    Strings(item, "openQuestions", 12, 600, prior?.OpenQuestions ?? []), Strings(item, "blockers", 12, 600, prior?.Blockers ?? []), outcome, prior?.ReopenedAtSequence ?? 0);
                stages.RemoveAll(value => value.Id == id); stages.Add(stage);
            }
        }
        if (updated.Plan.Any(step => !stages.Any(stage => stage.Id == step.Id))) Fail("Jeder Science-Planschritt benötigt scienceStages mit derselben id, question und strategy; beide Listen gemeinsam senden.");
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
                        ValidateCheck(updated, stage, check, requireFreshReads: false);
                continue;
            }
            if (stage.Outcome == "open" || stage.Blockers.Count > 0) Fail($"Etappe {stage.Id}: offene/blockierte Etappe nicht als completed melden; pending mit blockers verwenden.");
            if (verifyReceipts)
                foreach (var check in stage.Checks.Where(check => check.Required || check.EvidenceIds.Count > 0))
                    ValidateCheck(updated, stage, check, requireFreshReads: !before.Plan.Any(old => old.Id == stage.Id && old.Status == "completed"));
        }
        return updated;
    }

    internal static void ValidateCheck(CodingWorkingState state, ScienceStage stage, ScienceCheck check, bool requireFreshReads = true)
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
                        : "Kein passender erfolgreicher Beleg vorhanden; die gewählte Prüfung tatsächlich ausführen und ihre Beleg-ID verwenden."));
            }
            if (receipt!.Sequence <= stage.ReopenedAtSequence) Fail($"Etappe {stage.Id}: {check.Id} benötigt einen neuen Nachweis nach dem Rücksprung.");
            if (check.Kind is "numeric" or "plot" or "publication" && check.Paths.Count == 0) Fail($"Prüfung {check.Id}: paths muss die tatsächlich geprüften Eingabedateien beziehungsweise die Publikation nennen.");
            // Publication edits form a sequence of document versions. Earlier
            // edits remain provenance, while a current edit or explicit review
            // must cover each document. Numeric/proof receipts stay conjunctive.
            if (check.Kind == "publication") continue;
            var paths = check.Paths.Concat(receipt.VerificationPath is { } proofPath ? [proofPath] : Array.Empty<string>())
                .Concat(receipt.Tool is "coding.read" or "math.formalProof" && receipt.Path is { } sourcePath ? [sourcePath] : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var current = state.Science?.Files?.FirstOrDefault(pair => CodingWorkingStateReducer.SamePath(pair.Key, path)).Value;
                var hashes = receipt!.FileHashes;
                var bound = hashes?.FirstOrDefault(pair => CodingWorkingStateReducer.SamePath(pair.Key, path)).Value;
                if (!requireFreshReads && current is null) continue;
                if (current is null || requireFreshReads && current.NeedsRead || bound is null || !string.Equals(current.Sha256, bound, StringComparison.OrdinalIgnoreCase))
                    Fail($"Etappe {stage.Id}: {check.Id}, Datei {path} ist ungeprüft/verändert. Datei gezielt lesen und die passende Prüfung für den aktuellen Stand nachweisen.");
            }
        }
        if (check.Kind == "publication")
            foreach (var path in check.Paths)
            {
                var current = state.Science?.Files?.FirstOrDefault(pair => CodingWorkingStateReducer.SamePath(pair.Key, path)).Value;
                if (current is null || current.Sha256 is null || requireFreshReads && current.NeedsRead)
                    Fail($"Etappe {stage.Id}: {check.Id}, Datei {path} benötigt eine aktuelle Lesebestätigung.");
                bool CoversCurrentFile(CodingToolEvidence evidence) => evidence.Path is { } ownPath
                    && CodingWorkingStateReducer.SamePath(ownPath, path)
                    && string.Equals(current.Sha256, evidence.Sha256
                        ?? evidence.FileHashes?.FirstOrDefault(pair => CodingWorkingStateReducer.SamePath(pair.Key, path)).Value,
                        StringComparison.OrdinalIgnoreCase);
                if (check.EvidenceIds.Select(id => CodingWorkingStateReducer.ResolveEvidence(state, id)!).Any(CoversCurrentFile)) continue;
                var candidates = state.Evidence.Where(item => item.Success && Matches("publication", item)
                    && item.Sequence > stage.ReopenedAtSequence && CoversCurrentFile(item)).TakeLast(3).Select(item => item.Id).ToArray();
                Fail($"Etappe {stage.Id}: {check.Id}, Datei {path}: Die angegebenen Belege betreffen keinen aktuellen Publikationsstand. "
                    + (candidates.Length > 0 ? "Verwende die aktuelle Beleg-ID in evidenceIds: " + string.Join(", ", candidates)
                        : "Publikation gezielt lesen oder bearbeiten und die Beleg-ID dieses aktuellen Dateistands verwenden."));
            }
    }

    private static bool Matches(string kind, CodingToolEvidence receipt) => kind switch
    {
        "lean" => receipt.Tool == "math.formalProof" && receipt.FormalVerified,
        "numeric" => receipt.Tool == "coding.command" && receipt.ExitCode == 0,
        "analytic" => receipt.Tool is "math.evaluate" || receipt.Tool == "coding.command" && receipt.ExitCode == 0 || receipt.Tool == "math.formalProof" && receipt.FormalVerified,
        "source" => receipt.Tool is "web.fetch" or "coding.read" or "documents.readPages",
        "plot" => receipt.Tool == "coding.command" && receipt.ExitCode == 0 || receipt.Tool == "image.generate",
        "publication" => receipt.Tool is "coding.edit" or "coding.write" or "document.create" or "coding.read",
        _ => false,
    };

    internal static CodingWorkingState Revalidate(CodingWorkingState state, bool requireFreshReads = false)
    {
        if (state.Science is null) return state;
        var invalid = new HashSet<string>();
        foreach (var stage in state.Science.Stages.Where(stage => state.Plan.Any(step => step.Id == stage.Id && step.Status == "completed")))
            foreach (var check in stage.Checks.Where(check => check.Required || check.EvidenceIds.Count > 0))
            {
                try { ValidateCheck(state, stage, check, requireFreshReads); }
                catch (ArgumentException) { invalid.Add(stage.Id); }
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
        var paths = state.Science?.Stages.Where(stage => state.Plan.Any(step => step.Id == stage.Id && step.Status == "in_progress"))
            .SelectMany(stage => stage.Checks).SelectMany(check => check.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        foreach (var file in state.ActiveFiles) paths.Add(file.Path);
        var hashes = state.Science?.Files?.Where(pair => pair.Value.Sha256 is not null && paths.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value.Sha256!, StringComparer.OrdinalIgnoreCase)
            ?? state.ActiveFiles.Where(file => file.Sha256 is not null).ToDictionary(file => file.Path, file => file.Sha256!, StringComparer.OrdinalIgnoreCase);
        var proof = FindProof(result);
        var verified = proof.ValueKind == JsonValueKind.Object && TextOrNull(proof, "formalVerification") == "KernelAccepted" && TextOrNull(proof, "axiomAudit") == "passed";
        var proofPath = verified ? ResolveProofPath(result, proof) : null;
        if (proofPath is { } path && TextOrNull(proof, "sourceFileSha256") is { Length: 64 } sha)
            hashes[path] = sha;
        if (receipt.Path is { } ownPath && receipt.Sha256 is { } ownSha) hashes[ownPath] = ownSha;
        return receipt with { FileHashes = hashes, FormalVerified = receipt.Tool == "math.formalProof" && verified
            && proofPath is not null && TextOrNull(proof, "sourceFileSha256") is { Length: 64 }, VerificationPath = proofPath };
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
        var files = new Dictionary<string, ScienceFile>(state.Science.Files ?? new Dictionary<string, ScienceFile>(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in child.Science!.Files ?? new Dictionary<string, ScienceFile>()) files[pair.Key] = pair.Value with { NeedsRead = true };
        return state with { Evidence = CodingWorkingStateReducer.KeepEvidence(state, state.Evidence.Concat(additions)), Sequence = state.Sequence + 1, Science = state.Science with { Files = files } };
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
            // The sandbox reports source relative to /sandbox/work; its typed
            // input hash manifest uses workspace-relative work/lean/... paths.
            if (result.TryGetProperty("inputHashes", out var inputs) && inputs.ValueKind == JsonValueKind.Object)
                foreach (var input in inputs.EnumerateObject())
                {
                    var path = input.Name.Replace('\\', '/');
                    if ((path.Equals(source, StringComparison.OrdinalIgnoreCase) || path.EndsWith("/" + source, StringComparison.OrdinalIgnoreCase))
                        && input.Value.ValueKind == JsonValueKind.String && input.Value.GetString() == TextOrNull(proof, "sourceFileSha256")) return path;
                }
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

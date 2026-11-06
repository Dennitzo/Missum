using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.Ai.Server.Core.Coding;

internal static class CodingWorkingStateTools
{
    internal const string PlanTool = "coding.updatePlan";
    internal const string EvidenceIdHint = "Übernimm die planEvidenceId aus dem Werkzeugergebnis oder den passenden Planbelegen. outputEvidenceId dient zum Lesen der Ausgabe. Prüfe Prüfart und Pfad.";
    internal const string EvidenceQueryHint = "Nur lesend, allein senden: stageId und checkId gemeinsam oder kind mit optionalem path. Für die nächste Seite beforeSequence und beforeId aus nextCursor übernehmen.";

    internal static IReadOnlyList<AgentToolSpec> CreateTools() =>
    [
        Tool(PlanTool, "Speichere einen knappen Arbeitsstand für den aktuellen Lauf. Für Codex- und Science-Hauptläufe ist ein nichtleerer Plan der erste Werkzeugschritt. Neue Etappen fortlaufend ab eins als Phase 1: Inhalt, Phase 2: Inhalt benennen; bestehende gespeicherte Originaltitel unverändert übernehmen. Aktualisiere zeitnah nach jeder Phase oder jedem Meilenstein. Mehrere bereits erledigte Etappen dürfen im selben Aufruf abgeschlossen werden. Aktiviere höchstens eine nächste Etappe. steps und acceptanceCriteria sind getrennte Listen mit jeweils höchstens 16 Einträgen und höchstens einem in_progress. Beide Listen dürfen im selben Aufruf gemeinsam angelegt oder geändert werden; das Update ist atomar. Sende nur neue oder tatsächlich geänderte Einträge, jeweils mit id und title; status ist optional und beginnt bei neuen Einträgen sicher mit pending. storedSteps und storedAcceptanceCriteria geben immer den vollständigen geordneten Plan einschließlich erledigter Etappen zurück; verwende diese vollständige Fassung für weitere Planung und Darstellungen. title hat 1–400 Zeichen; bei bestehenden Einträgen den gespeicherten Originaltitel exakt unverändert mitsenden, insbesondere acceptanceCriteria niemals umdeuten. Der mitgelieferte aktuelle Arbeitsstand ist maßgeblich: Neue unabhängige Aufträge starten mit leeren Listen; offene Science-Pläne derselben Sitzung bleiben bei Fortsetzung erhalten. Nicht genannte Punkte/Felder bleiben innerhalb des Laufs erhalten. Neu completed erfordert 1–3 passende erfolgreiche planEvidenceId-Werte in evidenceIds. Verwende die Belege aus dem Werkzeugergebnis oder dem aktuellen Planbeleg-Nachtrag. Weitere Belege kannst du mit evidenceQuery separat und rein lesend abrufen. facts und rejectedHypotheses brauchen echte Belege (auch Fehlerbelege möglich); je Liste bleiben die letzten 12 Befunde aktiv, ältere im Journal. explanation wird nur im Aufrufjournal festgehalten. phase=final erklärt keine Arbeit für erledigt. Fehler lehnen das gesamte Update ab; korrigiere die genannten Felder und sende die gewünschten Änderungen beider Listen erneut gemeinsam. Planung ersetzt keine Ausführung.",
            """{"steps":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Immer erforderlich; gespeicherten Originaltitel exakt unverändert mitsenden, Kriterien niemals umdeuten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Optional: neue Einträge beginnen ohne status als pending; bei bestehenden Einträgen bleibt der gespeicherte Status erhalten. Neu completed braucht echte erfolgreiche evidenceIds."},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100}}},"required":["id","title"]}},"acceptanceCriteria":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Immer erforderlich; gespeicherten Originaltitel exakt unverändert mitsenden, Kriterien niemals umdeuten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Optional: neue Einträge beginnen ohne status als pending; bei bestehenden Einträgen bleibt der gespeicherte Status erhalten. Neu completed braucht echte erfolgreiche evidenceIds."},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100}}},"required":["id","title"]}},"facts":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100},"minItems":1}},"required":["text","evidenceIds"]}},"rejectedHypotheses":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600},"evidenceIds":{"type":"array","maxItems":3,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":100},"minItems":1}},"required":["text","evidenceIds"]}},"explanation":{"type":"string","minLength":1,"maxLength":2000},"nextStep":{"type":"string","minLength":1,"maxLength":1000},"phase":{"type":"string","enum":["planning","exploration","editing","review","final","error_recovery","unknown"]}}""", []),
    ];

    internal static void Validate(JsonElement args)
    {
        // New entries need an explicit title; an omitted status starts pending.
        // Existing status-only deltas still retain their stored item headers.
        if (args.ValueKind != JsonValueKind.Object || !args.EnumerateObject().Any()) throw new ArgumentException("updatePlan benötigt mindestens ein Arbeitsstand-Feld als Objekt.");
        if (args.TryGetProperty("steps", out var steps)) ValidatePlan(steps, "steps");
        if (args.TryGetProperty("acceptanceCriteria", out var criteria)) ValidatePlan(criteria, "acceptanceCriteria");
        if (args.TryGetProperty("facts", out var facts)) ValidateFacts(facts);
        if (args.TryGetProperty("rejectedHypotheses", out var rejected)) ValidateFacts(rejected);
        if (args.TryGetProperty("explanation", out var explanation)) Text(explanation, 1, 2000);
        if (args.TryGetProperty("nextStep", out var next)) Text(next, 1, 1000);
        if (args.TryGetProperty("phase", out var phase) && (phase.ValueKind != JsonValueKind.String
            || phase.GetString() is not ("planning" or "exploration" or "editing" or "review" or "final" or "error_recovery" or "unknown")))
            throw new ArgumentException("Unknown working phase.");
    }

    internal static bool IsEvidenceQuery(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("evidenceQuery", out _);

    internal static JsonElement QueryEvidence(CodingWorkingState state, JsonElement arguments)
    {
        var query = ReadEvidenceQuery(state, arguments);
        var matches = ScienceStagePolicy.FindCandidates(state, query.Kind, query.Paths, query.AfterSequence)
            .Where(receipt => (query.BeforeSequence is null || receipt.Sequence < query.BeforeSequence
                    || receipt.Sequence == query.BeforeSequence && string.CompareOrdinal(receipt.Id, query.BeforeId) > 0)
                && (query.Path is null || string.Equals(
                    ScienceStagePolicy.NormalizeProjectPath(receipt.VerificationPath ?? receipt.Path), query.Path, StringComparison.OrdinalIgnoreCase)))
            .Take(11).ToArray();
        return EvidenceQueryReceipt(state, matches);
    }

    internal static PlanEvidenceQuery ReadEvidenceQuery(CodingWorkingState state, JsonElement arguments)
    {
        var query = arguments.GetProperty("evidenceQuery");
        if (arguments.EnumerateObject().Count() != 1 || query.ValueKind != JsonValueKind.Object
            || query.EnumerateObject().Any(field => field.Name is not ("stageId" or "checkId" or "kind" or "path" or "beforeSequence" or "beforeId")))
            throw new ArgumentException("Sende evidenceQuery als einzigen Arbeitsplanparameter. " + EvidenceQueryHint);
        var stageId = ReadText("stageId", 80);
        var checkId = ReadText("checkId", 80);
        var kind = ReadText("kind", 20);
        var path = ReadText("path", 1024);
        var beforeId = ReadText("beforeId", 100);
        long? beforeSequence = null;
        if (query.TryGetProperty("beforeSequence", out var cursor))
        {
            if (cursor.ValueKind != JsonValueKind.Number || !cursor.TryGetInt64(out var sequence) || sequence < 1)
                throw new ArgumentException("beforeSequence muss eine positive Zahl aus nextCursor sein.");
            beforeSequence = sequence;
        }
        if ((beforeId is null) != (beforeSequence is null))
            throw new ArgumentException("beforeSequence und beforeId gemeinsam aus nextCursor übernehmen.");
        IReadOnlyList<string>? paths = null;
        var afterSequence = 0L;
        if (stageId is not null && checkId is not null && kind is null && path is null)
        {
            var stage = state.Science?.Stages.FirstOrDefault(stage => stage.Id == stageId);
            var check = stage?.Checks.FirstOrDefault(check => check.Id == checkId)
                ?? throw new ArgumentException("Der Prüfpunkt ist im gespeicherten Plan nicht vorhanden. Verwende für einen neuen Prüfpunkt kind und optional path.");
            kind = check.Kind;
            paths = check.Paths;
            afterSequence = stage!.ReopenedAtSequence;
        }
        else if (stageId is not null || checkId is not null || kind is not ("source" or "analytic" or "lean" or "numeric" or "plot" or "publication" or "work"))
            throw new ArgumentException(EvidenceQueryHint);
        var normalizedPath = ScienceStagePolicy.NormalizeProjectPath(path);
        if (path is not null && normalizedPath is null)
            throw new ArgumentException("path benötigt einen relativen Projektpfad ohne '..'.");

        return new(kind == "work" ? null : kind, paths, normalizedPath, afterSequence, beforeSequence, beforeId);

        string? ReadText(string name, int maximum)
        {
            if (!query.TryGetProperty(name, out var value)) return null;
            Text(value, 1, maximum);
            return value.GetString();
        }
    }

    internal static JsonElement EvidenceQueryReceipt(CodingWorkingState state, IReadOnlyList<CodingToolEvidence> matches)
    {
        const int pageSize = 10;
        // Several imported results may share a sequence. Include a stable ID in
        // the page cursor so the next page can resume within that group.
        return JsonSerializer.SerializeToElement(new
        {
            success = true, evidenceLookup = true, changed = false, stateRevision = state.Sequence,
            candidates = matches.Take(pageSize).Select(CodingPlanEvidenceContext.Describe),
            nextCursor = matches.Count > pageSize
                ? new { beforeSequence = matches[pageSize - 1].Sequence, beforeId = matches[pageSize - 1].Id } : null,
        }, MissumAiProtocol.CreateJsonOptions());
    }

    internal static JsonElement RejectedPlan(CodingWorkingState state, JsonElement arguments, ArgumentException error)
    {
        var issues = error is PlanEvidenceValidationException validation
            ? validation.Issues
            : [new PlanEvidenceIssue("invalid_update", "updatePlan")];
        return JsonSerializer.SerializeToElement(new
        {
            success = false, changed = false, errorCode = "agent.invalid_state_action", error = error.Message,
            stateRevision = state.Sequence, storedSteps = state.Plan, storedAcceptanceCriteria = state.AcceptanceCriteria,
            scienceStages = state.Science?.Stages,
            rejectedFields = arguments.ValueKind == JsonValueKind.Object ? arguments.EnumerateObject().Select(field => field.Name).ToArray() : [],
            rejectedScienceChanges = arguments.ValueKind == JsonValueKind.Object
                && arguments.TryGetProperty("scienceStages", out var stages) && stages.ValueKind == JsonValueKind.Array
                ? stages.EnumerateArray().Where(stage => stage.ValueKind == JsonValueKind.Object).Take(16).Select(stage => new
                {
                    stageId = CodingWorkingStateReducer.Text(stage, "id"),
                    fields = stage.EnumerateObject().Select(field => field.Name).Where(field => field != "id").ToArray(),
                }) : null,
            retry = "Das Update wurde vollständig abgelehnt. Korrigiere die Prüfpunkte und sende alle weiterhin gewünschten Änderungen im selben Aufruf erneut, auch die noch ungespeicherten Leitfragen und Prüfstrategien.",
            validationIssues = issues.Select(issue => new
            {
                issue.Code, issue.Field, issue.StageId, issue.CheckId, issue.Kind, issue.Paths, issue.RejectedIds,
                message = issue.Message ?? error.Message,
                candidates = issue.Field.EndsWith("evidenceIds", StringComparison.Ordinal)
                    ? ScienceStagePolicy.FindCandidates(state, issue.Kind, issue.Paths, issue.AfterSequence)
                        .Take(3).Select(CodingPlanEvidenceContext.Describe) : null,
            }),
        }, MissumAiProtocol.CreateJsonOptions());
    }

    private static void ValidatePlan(JsonElement steps, string section)
    {
        if (steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() is < 1 or > 16) throw new ArgumentException("Plans must contain one to sixteen items.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var active = 0;
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each plan step must be an object.");
            foreach (var field in step.EnumerateObject())
                if (field.Name is not ("id" or "title" or "status" or "evidenceIds")) throw new ArgumentException("Unknown plan step field.");
            var id = Required(step, "id"); Text(id, 1, 80);
            if (step.TryGetProperty("title", out var title)
                && (title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) || title.GetString()!.Length > 400))
                throw new ArgumentException($"{section}, ID {id.GetRawText()}: title wurde mitgesendet, ist aber ungültig. "
                    + "Verwende 1–400 Zeichen. Bei einer bereits gespeicherten ID darf title ganz entfallen; null oder leer bedeutet nicht ausgelassen. Das gesamte Update wurde abgelehnt.");
            if (!ids.Add(id.GetString()!)) throw new ArgumentException("Plan IDs must be unique.");
            if (step.TryGetProperty("status", out var status))
            {
                if (status.ValueKind != JsonValueKind.String || status.GetString() is not ("pending" or "in_progress" or "completed"))
                    throw new ArgumentException("Unknown plan status.");
                if (status.GetString() == "in_progress" && ++active > 1) throw new ArgumentException($"{section}: Höchstens ein Eintrag darf in_progress sein. Schließe den bisherigen Schritt im selben Update ab oder setze ihn auf pending.");
            }
            // The reducer checks new IDs, completed transitions and the merged plan.
            // An already completed item can retain its previously verified evidence.
            ValidateEvidence(step, false);
        }
    }

    internal static JsonElement CreatePlanReceipt(CodingWorkingState state, JsonElement arguments)
    {
        // A call can contain a single status delta. Its receipt must still carry
        // the complete ordered plan, so both the model and native clients can
        // render the canonical plan without replacing it with the last delta.
        var options = MissumAiProtocol.CreateJsonOptions();
        var receipt = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["phase"] = state.Phase,
            ["stateRevision"] = state.Sequence,
            ["storedSteps"] = JsonSerializer.SerializeToElement(state.Plan, options),
            ["storedAcceptanceCriteria"] = JsonSerializer.SerializeToElement(state.AcceptanceCriteria, options),
            ["stageUpdatePolicy"] = "Nach jeder Phase oder jedem Meilenstein zeitnah aktualisieren. Mehrere bereits erledigte Etappen können gemeinsam abgeschlossen werden; höchstens eine Etappe bleibt in_progress.",
        };
        if (arguments.TryGetProperty("steps", out _) || arguments.TryGetProperty("acceptanceCriteria", out _))
        {
            receipt["storedStepIds"] = state.Plan.Select(static item => item.Id).ToArray();
            receipt["storedAcceptanceCriterionIds"] = state.AcceptanceCriteria.Select(static item => item.Id).ToArray();
        }
        if (arguments.TryGetProperty("nextStep", out _)) receipt["nextStep"] = state.NextStep;
        if (state.Science is not null)
        {
            receipt["scienceStages"] = JsonSerializer.SerializeToElement(state.Science.Stages, options);
            var pending = state.Science.Stages.Where(stage => stage.DetailsPending).Select(stage => new
            {
                stage.Id,
                missingFields = new[] { string.IsNullOrWhiteSpace(stage.Question) ? "question" : null,
                    string.IsNullOrWhiteSpace(stage.Strategy) ? "strategy" : null }.Where(field => field is not null).ToArray(),
            }).ToArray();
            if (pending.Length > 0)
            {
                receipt["pendingScienceDetails"] = pending;
                receipt["message"] = "Arbeitsplan gespeichert und anzeigbar. Die genannten wissenschaftlichen Details sind noch offen; "
                    + "ergänze Leitfrage und Prüfstrategie für die jeweilige Etappe vor deren fachlichem Abschluss.";
            }
        }
        AddChangedItems("steps", "updatedSteps", state.Plan);
        AddChangedItems("acceptanceCriteria", "updatedAcceptanceCriteria", state.AcceptanceCriteria);
        if (arguments.TryGetProperty("facts", out var facts)) receipt["factsReceived"] = facts.GetArrayLength();
        if (arguments.TryGetProperty("rejectedHypotheses", out var rejected)) receipt["rejectedHypothesesReceived"] = rejected.GetArrayLength();
        return JsonSerializer.SerializeToElement(receipt);

        void AddChangedItems(string argument, string result, IReadOnlyList<CodingPlanItem> items)
        {
            if (!arguments.TryGetProperty(argument, out var changes)) return;
            var ids = changes.EnumerateArray().Select(static item => item.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
            receipt[result] = items.Where(item => ids.Contains(item.Id)).Select(static item => new { id = item.Id, status = item.Status }).ToArray();
        }
    }

    private static void ValidateFacts(JsonElement facts)
    {
        if (facts.ValueKind != JsonValueKind.Array || facts.GetArrayLength() > 12) throw new ArgumentException("At most twelve evidenced facts are allowed.");
        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each fact must be an object.");
            foreach (var field in fact.EnumerateObject())
                if (field.Name is not ("text" or "evidenceIds")) throw new ArgumentException("Unknown fact field.");
            Text(Required(fact, "text"), 1, 600);
            ValidateEvidence(fact, true);
        }
    }

    private static void ValidateEvidence(JsonElement item, bool required)
    {
        if (!item.TryGetProperty("evidenceIds", out var ids))
        {
            if (required) throw new ArgumentException("Completed steps and facts require evidence IDs.");
            return;
        }
        if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 3 || required && ids.GetArrayLength() == 0)
            throw new ArgumentException("At most three evidence IDs are allowed; completed steps and facts need evidence.");
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 100)
            {
                var text = id.ValueKind == JsonValueKind.String ? id.GetString()! : id.GetRawText();
                var shown = JsonSerializer.Serialize(text.Length <= 160 ? text : text[..160] + "… [gekürzt]");
                throw new ArgumentException($"Beleg-ID {shown} ist ungültig: evidenceIds benötigt Text-IDs mit 1–100 Zeichen.");
            }
        }
        if (ids.EnumerateArray().Select(static id => id.GetString()).Distinct(StringComparer.Ordinal).Count() != ids.GetArrayLength())
            throw new ArgumentException("Evidence IDs must be unique.");
    }

    private static JsonElement Required(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        ? value : throw new ArgumentException($"The required field {field} is missing.");

    private static void Text(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length < minimum || text.Length > maximum)
            throw new ArgumentException($"Expected text with {minimum} to {maximum} characters.");
    }

    private static AgentToolSpec Tool(string name, string description, string properties, string[] required)
    {
        var fields = JsonNode.Parse(properties)!.AsObject();
        foreach (var list in new[] { "steps", "acceptanceCriteria" })
            fields[list]!["items"]!["properties"]!["status"]!["description"] =
                "Ohne status beginnen neue Einträge pending; gespeicherte behalten ihren Status. Neu completed braucht evidenceIds direkt in diesem Eintrag, zusätzlich zu Science-Prüfbelegen.";
        foreach (var list in new[] { "steps", "acceptanceCriteria", "facts", "rejectedHypotheses" })
            fields[list]!["items"]!["properties"]!["evidenceIds"]!["description"] = EvidenceIdHint;
        fields["evidenceQuery"] = JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "stageId":{"type":"string","minLength":1,"maxLength":80},"checkId":{"type":"string","minLength":1,"maxLength":80},
              "kind":{"type":"string","enum":["work","source","analytic","lean","numeric","plot","publication"]},
              "path":{"type":"string","minLength":1,"maxLength":1024},"beforeSequence":{"type":"integer","minimum":1},
              "beforeId":{"type":"string","minLength":1,"maxLength":100}}}
            """);
        fields["evidenceQuery"]!["description"] = EvidenceQueryHint;
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = fields, required, additionalProperties = false });
        return new(name, description, ToolRiskClass.ReadOnly, true, schema, required.ToHashSet(StringComparer.Ordinal),
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
    }
}

internal sealed record PlanEvidenceQuery(string? Kind, IReadOnlyList<string>? Paths, string? Path,
    long AfterSequence, long? BeforeSequence, string? BeforeId);

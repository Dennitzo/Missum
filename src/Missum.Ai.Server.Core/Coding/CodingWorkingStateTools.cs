using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.Ai.Server.Core.Coding;

internal static class CodingWorkingStateTools
{
    internal const string PlanTool = "coding.updatePlan";
    internal static IReadOnlyList<AgentToolSpec> CreateTools() =>
    [
        Tool(PlanTool, "Speichere einen knappen Arbeitsstand für den aktuellen Lauf. Für Codex- und Science-Hauptläufe ist ein nichtleerer Plan der erste Werkzeugschritt. Neue Etappen fortlaufend ab eins als Phase 1: Inhalt, Phase 2: Inhalt benennen; bestehende gespeicherte Originaltitel unverändert übernehmen. Aktualisiere zeitnah nach jeder Phase oder jedem Meilenstein. Mehrere bereits erledigte Etappen dürfen im selben Aufruf abgeschlossen werden. Aktiviere höchstens eine nächste Etappe. steps und acceptanceCriteria sind getrennte Listen mit jeweils höchstens 16 Einträgen und höchstens einem in_progress. Beide Listen dürfen im selben Aufruf gemeinsam angelegt oder geändert werden; das Update ist atomar. Sende nur neue oder tatsächlich geänderte Einträge, jeweils mit id und title; status ist optional und beginnt bei neuen Einträgen sicher mit pending. storedSteps und storedAcceptanceCriteria geben immer den vollständigen geordneten Plan einschließlich erledigter Etappen zurück; verwende diese vollständige Fassung für weitere Planung und Darstellungen. title hat 1–400 Zeichen; bei bestehenden Einträgen den gespeicherten Originaltitel exakt unverändert mitsenden, insbesondere acceptanceCriteria niemals umdeuten. Der mitgelieferte aktuelle Arbeitsstand ist maßgeblich: Neue unabhängige Aufträge starten mit leeren Listen; offene Science-Pläne derselben Sitzung bleiben bei Fortsetzung erhalten. Nicht genannte Punkte/Felder bleiben innerhalb des Laufs erhalten. Aktualisiere den Status anhand des tatsächlichen Arbeitsstands. facts und rejectedHypotheses enthalten kurze fachliche Befunde als text; je Liste bleiben die letzten 12 Befunde aktiv, ältere im Journal. explanation wird nur im Aufrufjournal festgehalten. phase=final erklärt keine Arbeit für erledigt. Fehler lehnen das gesamte Update ab; korrigiere die genannten Felder und sende die gewünschten Änderungen beider Listen erneut gemeinsam. Planung ersetzt keine Ausführung.",
            """{"steps":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Immer erforderlich; gespeicherten Originaltitel exakt unverändert mitsenden, Kriterien niemals umdeuten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Optional: neue Einträge beginnen ohne status als pending; bei bestehenden Einträgen bleibt der gespeicherte Status erhalten."}},"required":["id","title"]}},"acceptanceCriteria":{"type":"array","minItems":1,"maxItems":16,"items":{"type":"object","additionalProperties":false,"properties":{"id":{"type":"string","minLength":1,"maxLength":80},"title":{"type":"string","minLength":1,"maxLength":400,"description":"Immer erforderlich; gespeicherten Originaltitel exakt unverändert mitsenden, Kriterien niemals umdeuten."},"status":{"type":"string","enum":["pending","in_progress","completed"],"description":"Optional: neue Einträge beginnen ohne status als pending; bei bestehenden Einträgen bleibt der gespeicherte Status erhalten."}},"required":["id","title"]}},"facts":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600}},"required":["text"]}},"rejectedHypotheses":{"type":"array","maxItems":12,"items":{"type":"object","additionalProperties":false,"properties":{"text":{"type":"string","minLength":1,"maxLength":600}},"required":["text"]}},"explanation":{"type":"string","minLength":1,"maxLength":2000},"nextStep":{"type":"string","minLength":1,"maxLength":1000},"phase":{"type":"string","enum":["planning","exploration","editing","review","final","error_recovery","unknown"]}}""", []),
    ];

    internal static void Validate(JsonElement args)
    {
        // New entries need an explicit title; an omitted status starts pending.
        // Existing status-only deltas still retain their stored item headers.
        if (args.ValueKind != JsonValueKind.Object || !args.EnumerateObject().Any()) throw new ArgumentException("updatePlan benötigt mindestens ein Arbeitsstand-Feld als Objekt.");
        foreach (var field in args.EnumerateObject())
            if (field.Name is not ("steps" or "acceptanceCriteria" or "facts" or "rejectedHypotheses"
                or "explanation" or "nextStep" or "phase" or "scienceStages"))
                throw new ArgumentException($"Unknown working state field: {field.Name}.");
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

    internal static JsonElement RejectedPlan(CodingWorkingState state, JsonElement arguments, ArgumentException error)
    {
        var issues = error is PlanValidationException validation
            ? validation.Issues
            : [new PlanValidationIssue("invalid_update", "updatePlan")];
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
                issue.Code, issue.Field, issue.StageId, issue.CheckId, issue.Kind, issue.Paths,
                message = issue.Message ?? error.Message,
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
                if (field.Name is not ("id" or "title" or "status")) throw new ArgumentException("Unknown plan step field.");
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
        if (facts.ValueKind != JsonValueKind.Array || facts.GetArrayLength() > 12) throw new ArgumentException("At most twelve facts are allowed.");
        foreach (var fact in facts.EnumerateArray())
        {
            if (fact.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each fact must be an object.");
            foreach (var field in fact.EnumerateObject())
                if (field.Name is not ("text")) throw new ArgumentException("Unknown fact field.");
            Text(Required(fact, "text"), 1, 600);
        }
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
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", properties = JsonSerializer.Deserialize<JsonElement>(properties),
            required, additionalProperties = false,
        });
        return new(name, description, ToolRiskClass.ReadOnly, true, schema, required.ToHashSet(StringComparer.Ordinal),
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal));
    }
}

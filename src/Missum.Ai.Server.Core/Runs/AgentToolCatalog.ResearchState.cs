using Missum.Ai.Contracts;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class AgentToolCatalog
{
    private static IEnumerable<AgentToolSpec> ResearchStateToolSpecs() =>
    [
        Client(ClientToolNames.ResearchRead, "Lies view=overview für den kompakten Forschungsstand; knownStateStamp spart unveränderte Inhalte. view=task liefert den vollständigen ursprünglichen Auftrag in Textseiten. objects/sources/experiments/checks laden ihren eigenen Bereich; ids liefern gezielte Details (sources auch Evidenz-IDs). Folge nextCursor nur innerhalb derselben Ansicht/ids; bei research.cursor_stale ohne Cursor neu beginnen. Ohne view bleiben frühere offset-Aufrufe kompatibel.", ToolRiskClass.ReadOnly, NormalizeResearchStateSchema(Parse("""
            {"type":"object","properties":{"projectId":{"type":"string","pattern":"^[A-Za-z0-9._-]{1,128}$"},"view":{"type":"string","enum":["overview","task","objects","sources","experiments","checks"]},"cursor":{"type":"string","minLength":1,"maxLength":2048},"knownStateStamp":{"type":"string","pattern":"^[a-fA-F0-9]{64}$"},"ids":{"type":"array","maxItems":32,"uniqueItems":true,"items":{"type":"string","minLength":1,"maxLength":256}},"offset":{"type":"integer","minimum":0},"limit":{"type":"integer","minimum":1,"maximum":100}},"required":["projectId"],"additionalProperties":false}
            """))),
        Client(ClientToolNames.ResearchUpdate, "Speichere nur neue oder fachlich geänderte Forschungsobjekte dauerhaft. Stabile id, kind und expectedRevision aus research.read verwenden; neue Objekte erwarten Revision 0. Bei kind section/contribution muss data.title UND data.contentMarkdown enthalten; description/method/limit ersetzen den wissenschaftlichen Markdowntext nicht. Bei hypothesis/claim/requirement muss data.statement enthalten. Der Hauptagent schreibt einzelne Publikationsabschnitte, der Subagent eigene contributions und Hypothesen mit seiner Agent-ID als Präfix. data enthält den vollständigen aktuellen Inhalt nur dieses Objekts. Quellen, Experimente und Prüfnachweise über echte IDs referenzieren. Status verified erfordert einen tatsächlichen passenden gespeicherten Prüfbeleg; ein erfolgreicher Prozess allein belegt keine Aussage. Konflikte gezielt lesen und beheben, nicht die ganze Publikation erneut schreiben.", ToolRiskClass.LocalMutation, NormalizeResearchStateSchema(Parse("""
            {"type":"object","properties":{"projectId":{"type":"string","pattern":"^[A-Za-z0-9._-]{1,128}$"},"title":{"type":"string","minLength":1,"maxLength":1000},"changes":{"type":"array","minItems":1,"maxItems":64,"items":{"type":"object","properties":{"id":{"type":"string","minLength":1,"maxLength":256},"kind":{"type":"string","enum":["hypothesis","claim","requirement","section","contribution"]},"expectedRevision":{"type":"integer","minimum":0},"data":{"type":"object","minProperties":1,"properties":{"title":{"type":"string","maxLength":1000},"contentMarkdown":{"type":"string","maxLength":64000},"statement":{"type":"string","maxLength":16000},"status":{"type":"string","enum":["planned","active","supported","provisionallySupported","verified","refuted","blocked","unresolved","superseded","withdrawn","completed","openLimit","draft"]},"assumptions":{"anyOf":[{"type":"string","maxLength":16000},{"type":"array","maxItems":64,"items":{"type":"string","maxLength":2000}}]},"prediction":{"type":"string","maxLength":16000},"nextCheck":{"type":"string","maxLength":4000},"reason":{"type":"string","maxLength":16000},"required":{"type":"boolean"},"order":{"type":"integer"},"sourceIds":{"type":"array","maxItems":128,"items":{"type":"string","minLength":1,"maxLength":256}},"claimIds":{"type":"array","maxItems":128,"items":{"type":"string","minLength":1,"maxLength":256}},"experimentIds":{"type":"array","maxItems":128,"items":{"type":"string","minLength":1,"maxLength":256}},"checkIds":{"type":"array","maxItems":128,"items":{"type":"string","minLength":1,"maxLength":256}}},"additionalProperties":false}},"required":["id","kind","expectedRevision","data"],"additionalProperties":false}}},"required":["projectId","changes"],"additionalProperties":false}
            """))),
    ];

    private static JsonElement NormalizeResearchStateSchema(JsonElement raw)
    {
        var schema = JsonNode.Parse(raw.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        if (properties["ids"] is JsonObject ids) ids["items"]!["maxLength"] = 200;
        if (properties["changes"] is JsonObject changes)
        {
            properties["title"]!["maxLength"] = 500;
            changes["minItems"] = 0;
            changes["maxItems"] = 32;
            var item = changes["items"]!["properties"]!;
            item["id"]!["maxLength"] = 200;
            var data = item["data"]!["properties"]!.AsObject();
            data["title"]!["maxLength"] = 500;
            data["status"]!["enum"]!.AsArray().Add("pending");
            foreach (var name in BasicResearchReferenceFields)
                data[name]!["items"]!["maxLength"] = 200;
            foreach (var name in ResearchStateExtraReferenceFields)
                data[name] = JsonNode.Parse("""{"type":"array","maxItems":128,"items":{"type":"string","minLength":1,"maxLength":200}}""");
            foreach (var name in ResearchStateExtraTextFields)
                data[name] = JsonNode.Parse("""{"type":"string","maxLength":16000}""");
            data["units"] = JsonNode.Parse("""{"type":"array","maxItems":128,"items":{"type":"object","properties":{"symbol":{"type":"string","minLength":1,"maxLength":200},"meaning":{"type":"string","minLength":1,"maxLength":2000},"unit":{"type":"string","minLength":1,"maxLength":200}},"required":["symbol","meaning","unit"],"additionalProperties":false}}""");
            data["figureCaptions"] = JsonNode.Parse("""{"type":"array","maxItems":32,"items":{"type":"object","properties":{"experimentId":{"type":"string","minLength":1,"maxLength":200},"artifactPath":{"type":"string","minLength":1,"maxLength":2048},"caption":{"type":"string","minLength":1,"maxLength":4000}},"required":["experimentId","artifactPath","caption"],"additionalProperties":false}}""");
            var changeSchema = changes["items"]!.AsObject();
            // Native llama schemas parse anyOf before sibling properties and do
            // not implement if/then. Keep the root object and give each nested
            // alternative its complete shape; the kind sets are disjoint.
            var manuscript = changeSchema.DeepClone().AsObject();
            manuscript["properties"]!["kind"]!["enum"] = new JsonArray("section", "contribution");
            manuscript["properties"]!["data"]!["required"] = new JsonArray("title", "contentMarkdown");
            manuscript["properties"]!["data"]!["properties"]!["title"]!["minLength"] = 1;
            manuscript["properties"]!["data"]!["properties"]!["contentMarkdown"]!["minLength"] = 1;
            var assertion = changeSchema.DeepClone().AsObject();
            assertion["properties"]!["kind"]!["enum"] = new JsonArray("hypothesis", "claim", "requirement");
            assertion["properties"]!["data"]!["required"] = new JsonArray("statement");
            assertion["properties"]!["data"]!["properties"]!["statement"]!["minLength"] = 1;
            assertion["properties"]!["data"]!["properties"]!["contentMarkdown"]!["minLength"] = 1;
            changes["items"] = new JsonObject { ["anyOf"] = new JsonArray(manuscript, assertion) };
        }
        return JsonSerializer.SerializeToElement(schema);
    }

    private static readonly string[] ResearchStateExtraReferenceFields =
        ["evidenceIds", "hypothesisIds", "requirementIds", "contributionIds", "targetIds"];
    private static readonly string[] ResearchStateExtraTextFields =
        ["description", "method", "expectedResult", "classification", "conclusion", "limit"];
    private static readonly string[] BasicResearchReferenceFields = ["sourceIds", "claimIds", "experimentIds", "checkIds"];

    private static void ValidateResearchStateArguments(string name, JsonElement value)
    {
        var project = RequireString(value, "projectId", 1, 128);
        if (!project.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            throw new ArgumentException("projectId must be a canonical project identifier.");
        if (name == ClientToolNames.ResearchRead)
        {
            if (value.TryGetProperty("ids", out var ids)) ValidateStateReferences(ids, "ids", 32);
            if (value.TryGetProperty("offset", out var offset) && offset.ValueKind != JsonValueKind.Number
                || value.TryGetProperty("limit", out var limit) && limit.ValueKind != JsonValueKind.Number)
                throw new ArgumentException("offset and limit must be integer values.");
            OptionalInteger(value, "offset", 0, int.MaxValue);
            OptionalInteger(value, "limit", 1, 100);
            OptionalString(value, "view", 1, 32);
            OptionalString(value, "cursor", 1, 2048);
            OptionalString(value, "knownStateStamp", 64, 64);
            var view = value.TryGetProperty("view", out var requestedView) ? requestedView.GetString() : null;
            if (view is not (null or "overview" or "task" or "objects" or "sources" or "experiments" or "checks"))
                throw new ArgumentException("Unknown research view.");
            if (value.TryGetProperty("cursor", out _) && (view is null || value.TryGetProperty("offset", out _)))
                throw new ArgumentException("cursor requires view and replaces offset.");
            if (value.TryGetProperty("knownStateStamp", out var stamp)
                && (view != "overview" || !stamp.GetString()!.All(Uri.IsHexDigit) || value.TryGetProperty("cursor", out _)
                    || value.TryGetProperty("offset", out _) || value.TryGetProperty("ids", out _)))
                throw new ArgumentException("knownStateStamp applies only to the unfiltered first overview page.");
            if (view == "task" && (value.TryGetProperty("ids", out _) || value.TryGetProperty("limit", out _)))
                throw new ArgumentException("task is read in consecutive text pages using cursor, without ids or limit.");
            return;
        }
        OptionalString(value, "title", 1, 500);
        if (value.TryGetProperty("title", out var title) && title.GetString()!.Any(char.IsControl))
            throw new ArgumentException("title must be a single-line scientific title.");
        var changes = value.GetProperty("changes");
        if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() > 32
            || changes.GetArrayLength() == 0 && !value.TryGetProperty("title", out _))
            throw new ArgumentException("changes requires a title or up to 32 scientific objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || change.EnumerateObject().Any(static item =>
                item.Name is not ("id" or "kind" or "expectedRevision" or "data")))
                throw new ArgumentException("Each change requires only id, kind, expectedRevision and data.");
            var id = RequireString(change, "id", 1, 200);
            if (id.Any(char.IsControl) || !seen.Add(id)) throw new ArgumentException("Object IDs must be unique and contain no control characters.");
            var kind = RequireString(change, "kind", 1, 32);
            if (kind is not ("hypothesis" or "claim" or "requirement" or "section" or "contribution"))
                throw new ArgumentException("Unknown scientific object kind.");
            if (!change.TryGetProperty("expectedRevision", out var revision) || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt64(out var number) || number < 0)
                throw new ArgumentException("expectedRevision must be a nonnegative integer.");
            if (!change.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("data must be a scientific object.");
            var requiredContent = kind is "section" or "contribution" ? "contentMarkdown" : "statement";
            RequireResearchContent(data, requiredContent, requiredContent == "contentMarkdown" ? 64_000 : 16_000, id, kind);
            if (kind is "section" or "contribution") RequireResearchContent(data, "title", 500, id, kind);
            foreach (var field in data.EnumerateObject())
                switch (field.Name)
                {
                    case "contentMarkdown": RequireString(data, field.Name, 1, 64_000); break;
                    case "title": OptionalString(data, field.Name, 0, 500); break;
                    case "statement": case "prediction": case "reason": OptionalString(data, field.Name, 0, 16_000); break;
                    case "nextCheck": OptionalString(data, field.Name, 0, 4000); break;
                    case "status":
                        if (RequireString(data, field.Name, 1, 32) is not ("planned" or "active" or "supported" or "provisionallySupported"
                            or "verified" or "refuted" or "blocked" or "unresolved" or "superseded" or "withdrawn" or "completed" or "openLimit" or "draft" or "pending"))
                            throw new ArgumentException("Unknown scientific object status.");
                        break;
                    case "assumptions":
                        if (field.Value.ValueKind == JsonValueKind.String) OptionalString(data, field.Name, 0, 16_000);
                        else ValidateStateReferences(field.Value, field.Name, 64, 2000);
                        break;
                    case "required":
                        if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("required must be boolean.");
                        break;
                    case "order":
                        if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt32(out _)) throw new ArgumentException("order must be an integer.");
                        break;
                    case "sourceIds": case "claimIds": case "experimentIds": case "checkIds":
                    case "evidenceIds": case "hypothesisIds": case "requirementIds": case "contributionIds": case "targetIds": ValidateStateReferences(field.Value, field.Name, 128); break;
                    case "description": case "method": case "expectedResult": case "classification": case "conclusion": case "limit": OptionalString(data, field.Name, 0, 16_000); break;
                    case "units": ValidateResearchUnits(field.Value); break;
                    case "figureCaptions": ValidateResearchCaptions(field.Value); break;
                    default: throw new ArgumentException($"Unknown research data field: {field.Name}.");
                }
        }
        if (value.GetRawText().Length > 128_000) throw new ArgumentException("Split the scientific update into smaller independent object changes.");
    }

    private static void RequireResearchContent(JsonElement data, string field, int maximum, string id, string kind)
    {
        if (data.TryGetProperty(field, out var content) && content.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(content.GetString()) && content.GetString()!.Length <= maximum)
            return;
        var required = kind is "section" or "contribution" ? "data.title und data.contentMarkdown" : "data.statement";
        var guidance = kind is "section" or "contribution"
            ? "contentMarkdown ist der vollständige wissenschaftliche Text dieses Abschnitts/Entwurfs; description, method und limit ersetzen ihn nicht."
            : "statement enthält die wissenschaftliche Aussage oder die erforderliche Aufgabe; description ersetzt sie nicht.";
        throw new ArgumentException($"research.update: Objekt '{id}' (kind={kind}) benötigt {required} als nichtleere Strings. "
            + $"data.{field} fehlt, ist leer oder ungültig (maximal {maximum} Zeichen). {guidance} "
            + "Korrigiere das betroffene Objekt mit unveränderter id/kind/expectedRevision; keine Änderung wurde übernommen.");
    }

    private static void ValidateStateReferences(JsonElement values, string name, int maximum, int maximumCharacters = 200)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > maximum)
            throw new ArgumentException($"{name} must be a bounded array.");
        foreach (var item in values.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                || item.GetString()!.Length > maximumCharacters || item.GetString()!.Any(char.IsControl))
                throw new ArgumentException($"{name} contains an invalid scientific identifier or text.");
    }

    private static void ValidateResearchUnits(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 128) throw new ArgumentException("units must be a bounded list.");
        foreach (var unit in value.EnumerateArray())
        {
            if (unit.ValueKind != JsonValueKind.Object || unit.EnumerateObject().Any(static field => field.Name is not ("symbol" or "meaning" or "unit")))
                throw new ArgumentException("Each unit needs symbol, meaning and unit.");
            _ = RequireString(unit, "symbol", 1, 200); _ = RequireString(unit, "meaning", 1, 2000); _ = RequireString(unit, "unit", 1, 200);
        }
    }

    private static void ValidateResearchCaptions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 32) throw new ArgumentException("figureCaptions must be a bounded list.");
        foreach (var caption in value.EnumerateArray())
        {
            if (caption.ValueKind != JsonValueKind.Object || caption.EnumerateObject().Any(static field => field.Name is not ("experimentId" or "artifactPath" or "caption")))
                throw new ArgumentException("Each figure needs experimentId, artifactPath and caption.");
            _ = RequireString(caption, "experimentId", 1, 200); _ = RequireString(caption, "artifactPath", 1, 2048); _ = RequireString(caption, "caption", 1, 4000);
        }
    }
}

using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Core.Agents;

public static class SpecializedAgentProtocol
{
    internal const string DecisionInstructionMarker = "[MISSUM_SPECIALIZED_AGENT_DECISION]";

    public static JsonElement DecisionSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false,
        required = new[] { "outcome", "summary", "checks", "openPoints", "nextPrompt" },
        properties = new
        {
            outcome = new { type = "string", @enum = new[] { "completed", "continue", "needs_input" } },
            summary = new { type = "string" },
            checks = new { type = "array", items = new { type = "object", additionalProperties = false,
                required = new[] { "point", "completed", "evidence" }, properties = new
                { point = new { type = "string" }, completed = new { type = "boolean" }, evidence = new { type = "string" } } } },
            openPoints = new { type = "array", items = new { type = "string" } },
            nextPrompt = new { type = new[] { "string", "null" } },
        },
    });

    internal static string BuildDecisionInstruction(string? rejection = null) => DecisionInstructionMarker + "\n"
        + (rejection is null ? "" : "Die letzte Entscheidung ist ungültig: " + rejection + "\n")
        + "Antworte ausschließlich mit einem JSON-Objekt nach dem folgenden Schema, ohne Markdown, Einleitung oder Werkzeugaufruf. "
        + "Verwende exakt die angegebenen Feldnamen und Datentypen; checks enthält point, completed (Boolean) und evidence. "
        + "Eine offene notwendige Prüfung verlangt continue und einen konkreten Folgeauftrag; completed darf keine offenen Punkte verbergen.\n"
        + DecisionSchema.GetRawText();

    public static SpecializedAgentDecision Parse(string? content, CodingWorkingState? state)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Die Agentenprüfung lieferte keine Entscheidung.");
        using var document = JsonDocument.Parse(content);
        ValidateShape(document.RootElement);
        var decision = JsonSerializer.Deserialize<SpecializedAgentDecision>(content, MissumAiProtocol.CreateJsonOptions())
            ?? throw new InvalidDataException("Die Agentenentscheidung ist leer.");
        if (decision.Outcome is not ("completed" or "continue" or "needs_input") || string.IsNullOrWhiteSpace(decision.Summary)
            || decision.Checks is not { Count: > 0 } || decision.Checks.Any(check => check is null
                || string.IsNullOrWhiteSpace(check.Point) || string.IsNullOrWhiteSpace(check.Evidence))
            || decision.OpenPoints is null || decision.OpenPoints.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Die Entscheidung benötigt outcome, summary, konkrete checks mit Begründungen und openPoints.");
        if (decision.Outcome == "continue" && (string.IsNullOrWhiteSpace(decision.NextPrompt) || decision.NextPrompt.Length > 120_000))
            throw new InvalidDataException("Eine Fortsetzung benötigt einen konkreten Folgeauftrag mit höchstens 120000 Zeichen.");
        if (decision.Outcome == "completed" && (decision.Checks.Any(check => !check.Completed) || decision.OpenPoints.Count > 0
            || state is not null && state.Plan.Concat(state.AcceptanceCriteria).Any(step => step.Status != "completed")))
            throw new InvalidDataException("Ein Ziel ist noch nicht abgeschlossen: offene Prüfpunkte, Probleme oder nicht absolvierte Planpunkte sind vorhanden. Wähle continue mit einem gezielten Folgeauftrag.");
        return decision;
    }

    private static void ValidateShape(JsonElement root)
    {
        RequireObject(root, ["outcome", "summary", "checks", "openPoints", "nextPrompt"]);
        if (root.GetProperty("outcome").ValueKind != JsonValueKind.String
            || root.GetProperty("summary").ValueKind != JsonValueKind.String
            || root.GetProperty("checks").ValueKind != JsonValueKind.Array
            || root.GetProperty("openPoints").ValueKind != JsonValueKind.Array
            || root.GetProperty("nextPrompt").ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new InvalidDataException("Die Agentenentscheidung entspricht nicht den Datentypen des Entscheidungsschemas.");
        foreach (var check in root.GetProperty("checks").EnumerateArray())
        {
            RequireObject(check, ["point", "completed", "evidence"]);
            if (check.GetProperty("point").ValueKind != JsonValueKind.String
                || check.GetProperty("completed").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || check.GetProperty("evidence").ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Jeder Prüfpunkt benötigt point, completed als Boolean und evidence.");
        }
        if (root.GetProperty("openPoints").EnumerateArray().Any(point => point.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("openPoints darf nur Texte enthalten.");

        static void RequireObject(JsonElement value, string[] fields)
        {
            if (value.ValueKind != JsonValueKind.Object
                || value.EnumerateObject().Count() != fields.Length
                || value.EnumerateObject().Any(property => !fields.Contains(property.Name, StringComparer.Ordinal))
                || fields.Any(field => !value.TryGetProperty(field, out _)))
                throw new InvalidDataException("Die Agentenentscheidung benötigt exakt diese Felder: " + string.Join(", ", fields) + ".");
        }
    }

    public static string Render(SpecializedAgentDefinition definition, SpecializedAgentDecision decision)
    {
        var text = new StringBuilder();
        text.AppendLine(decision.Outcome switch
        {
            "completed" => "**Zielprüfung abgeschlossen: Ziel erreicht.**",
            "continue" => "**Zielprüfung abgeschlossen: weitere Arbeit erforderlich.**",
            _ => "**Zielprüfung abgeschlossen: Rückfrage erforderlich.**",
        }).AppendLine().AppendLine(decision.Summary).AppendLine();
        foreach (var check in decision.Checks)
            text.Append(check.Completed ? "- [x] " : "- [ ] ").Append(check.Point).Append(": ").AppendLine(check.Evidence);
        if (decision.OpenPoints.Count > 0)
        {
            text.AppendLine().AppendLine("Offene Punkte:");
            foreach (var point in decision.OpenPoints) text.Append("- ").AppendLine(point);
        }
        if (!string.IsNullOrWhiteSpace(decision.NextPrompt))
            text.AppendLine().AppendLine("Folgeauftrag an den Hauptagenten:").AppendLine().AppendLine(decision.NextPrompt);
        return text.ToString().TrimEnd();
    }
}

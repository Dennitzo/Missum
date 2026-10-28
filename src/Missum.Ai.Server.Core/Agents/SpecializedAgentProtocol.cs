using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Core.Agents;

public static class SpecializedAgentProtocol
{
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

    public static SpecializedAgentDecision Parse(string? content, CodingWorkingState? state)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidDataException("Die Agentenprüfung lieferte keine Entscheidung.");
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

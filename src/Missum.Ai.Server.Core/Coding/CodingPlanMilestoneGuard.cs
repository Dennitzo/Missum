using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Coding;

/// <summary>Shared stage-boundary policy for Codex and Science main runs.</summary>
internal static class CodingPlanMilestoneGuard
{
    internal const string Marker = "MISSUM_CODING_PLAN_MILESTONES_V1\n";
    internal const string Instructions = "Arbeite den gespeicherten Plan Etappe für Etappe ab. "
        + "Benenne neue Schritte fortlaufend ab eins im Format 'Phase 1: Inhalt', 'Phase 2: Inhalt' und so weiter. "
        + "Übernimm bei bestehenden Schritten gespeicherte IDs und Originaltitel unverändert. "
        + "Aktualisiere coding.updatePlan unmittelbar nach jeder abgeschlossenen Phase oder jedem Meilenstein, "
        + "bevor du Werkzeuge für die nächste unabhängige Etappe ausführst. "
        + "Speichere den tatsächlichen Arbeitsstand und aktiviere die nächste Etappe. "
        + "Mehrere bereits erledigte Etappen dürfen im selben Aufruf abgeschlossen werden; diese Regel ersetzt frühere Einzelabschlussgrenzen. "
        + "Schon completed gespeicherte Etappen dürfen in der vollständigen Liste unverändert stehen bleiben. "
        + "Bei Blockade oder Rücksprung den betroffenen Schritt sofort auf pending beziehungsweise in_progress setzen und explanation angeben. "
        + "Mehrere zusammengehörige Werkzeuge innerhalb derselben Etappe sind erlaubt; aktualisiere nicht nach jedem einzelnen Werkzeug. "
        + "Vor der Abschlussantwort den Arbeitsplan auf den tatsächlichen Ergebnisstand bringen; offene Arbeit nicht als erledigt ausgeben.";

    internal static void EnsureInstructions(List<LmChatMessage> messages)
    {
        var instruction = Marker + Instructions;
        // Append to evaluated histories, including older Codex sessions. Never
        // rewrite their native system/KV prefix. Recognize folded instructions.
        if (!messages.Any(message => message.Role == "system" && message.Content == instruction
            || message.Role == "user" && message.Content == "Missum-Laufanweisung:\n" + instruction))
            messages.Add(new("system", instruction));
    }
}

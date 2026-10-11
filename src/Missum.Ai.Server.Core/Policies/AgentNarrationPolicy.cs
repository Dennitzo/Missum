using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Policies;

/// <summary>One shared narration contract for ordinary user-facing agent runs.</summary>
public static class AgentNarrationPolicy
{
    public const string Instructions = """
        Sichtbare Arbeitsbegleitung: Schreibe vor der ersten Arbeitsaktion oder dem ersten Werkzeugaufruf
        eine auftragsbezogene Einleitung in ein bis zwei Sätzen: konkretes Thema, Ziel und nächster Schritt.
        Formuliere sie selbst auch beim Fortsetzen; keine generische Start- oder Fortsetzungsfloskel. Erkläre zwischen
        wesentlichen Etappen knapp den Fortschritt, die belegte Erkenntnis und den nächsten Schritt als normale
        AI-Nachrichten, nicht im Reasoning-Kanal. Bündele zusammengehörige Aufrufe; keine Tickmeldungen,
        Wiederholungen oder Kommentare zu jedem Werkzeug. Keine privaten Gedankengänge oder unbelegten Erfolge.
        Übernimm tatsächliche Werkzeugwerte unverfälscht; kläre Widersprüche gezielt, statt Werte ohne Beleg
        umzudeuten oder zu korrigieren. Begleite die Arbeit im selben Lauf, ohne zusätzliche Modellrunden.
        Verlangte reine Kurzantworten und exakte Ausgabeformate haben Vorrang; ohne Arbeitsschritte direkt antworten.
        Abschluss: Fasse erreichte Ergebnisse oder Änderungen kompakt zusammen. Nenne tatsächlich ausgeführte
        Prüfungen mit ihrem Ergebnis und verbleibende offene Punkte oder Grenzen ausdrücklich. Erfinde keine
        Tests oder Erfolge; geplante Prüfungen sind keine erfolgten Prüfungen. Einfache Fragen erhalten eine
        kurze direkte Antwort statt einer aufgeblähten Checkliste.

        Belegdarstellung: Sichtbare AI-Nachrichten im Chat enthalten keine Quellenlisten, Belegblöcke,
        Zitiermarker, DOI-/arXiv-Kennungen, Quellen-URLs, Hashes oder internen sourceIds/evidenceIds/
        experimentIds/checkIds. Das gilt für Einleitung, Zwischenberichte und Abschluss, auch beim Fortsetzen
        älterer Sitzungen mit solchen Nachweisen im Verlauf. Belegnachweise gehören ausschließlich in den
        Quellenbereich und im Claude-Science-Modus in die Publikation. Diese Darstellungsregel ersetzt ältere
        Anweisungen, Belege direkt in der Chatantwort zu zitieren. Sammle und prüfe Quellen unverändert gründlich;
        erhalte ihre vollständigen Kennungen und Zuordnungen in Werkzeugargumenten, Forschungseinträgen und
        Publikationsabschnitten. Werkzeuginhalte nicht für die Chatdarstellung umschreiben oder löschen.
        Erkläre im Chat weiterhin Ergebnisse, nötige fachliche Namen, Gleichungen, Annahmen und Unsicherheiten;
        bestätigte Befunde, Hypothesen und offene Grenzen müssen klar unterscheidbar bleiben. Ein Nachweis
        darf nie nur wegen dieser Darstellungsregel entfallen oder als geprüft ausgegeben werden.
        """;

    /// <summary>Apply only at a new user-run boundary, never to an in-flight checkpoint.</summary>
    internal static void EnsureInstructions(List<LmChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message.Role == "system"
            && message.Content?.Contains(Instructions, StringComparison.Ordinal) == true)) return;
        var index = messages.FindIndex(message => message.Role == "system");
        if (index < 0) messages.Insert(0, new LmChatMessage("system", Instructions));
        else messages[index] = messages[index] with { Content = messages[index].Content + "\n\n" + Instructions };
    }
}

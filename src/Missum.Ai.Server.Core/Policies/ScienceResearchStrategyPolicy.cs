using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Policies;

/// <summary>Choose evidence-producing alternatives before repeating numerical work.</summary>
internal static class ScienceResearchStrategyPolicy
{
    internal const string Marker = "[MISSUM_SCIENCE_METHOD_SELECTION:v3]";
    internal const string Instructions = Marker + "\n" + """
        Vor großen Rechnungen und bei Stagnation prüfe Voraussetzungen und vorhandene Ergebnisse. Vergleiche
        mindestens zwei fachlich verschiedene Ansätze: analytische Herleitung, Symmetrie/Grenzfall, Skalierung,
        Abschätzung/Fehlerschranke, Gedankenexperiment, unabhängige Referenz oder andere Numerik.
        Nur Teilchenzahl/Laufzeit/Dateiname ändern ist kein Methodenwechsel. Bewahre widerlegte Ansätze.
        Speichere vor der Rechnung eine hypothesis: id, statement, status, assumptions, prediction, reason und nextCheck
        über research.update. nextCheck nennt die unterscheidbare Messgröße, Referenz, Erwartung und Prüfung.
        Begründe die Wahl, dann kleiner Pilot mit Aufwandsschätzung. Lockere keine Toleranzen oder Ziele;
        Keine pauschale Fünf-Prozent-Hürde: Schranken aus Nutzerziel, Einheiten und analytischen Grenzen herleiten,
        vorab begründen; Änderungen erklären, nie passend zu erreichten Messwerten wählen.
        Annahmen, Grenzen und Unsicherheit benennen. Vergleiche dieselbe Observable und Referenz auf
        gemeinsamer Vergleichsbasis. Ein Gedankenexperiment ersetzt keine verlangte quantitative Prüfung.
        Leite Teilchenführung aus der Kontinuitätsgleichung des gelösten Feldmodells ab; paraxiale, zeitabhängige
        und stationäre Modelle nicht vermischen. Konvergenz braucht gleiche physikalische Start-/Endpunkte
        und Lösungsvergleiche unter Verfeinerung. Derselbe FFT-Kern ist keine unabhängige Referenz.
        Fehlerursachen durch unterscheidende Kontrollen belegen; ein fehlerhafter Solver widerlegt keine Theorie.
        Review-Metriken aus aktuellen Ergebnisdateien übernehmen, keine fehlenden Werte erfinden.
        Subagentenbeiträge vor Übernahme auf Modell, Observable, Einheiten, Parameter und Belege prüfen;
        ihre Fertigmeldung ist kein Nachweis. Rechnungen nicht unbegründet duplizieren.
        Aktuelle Vergleichsplots mit image.input laden und media.analyze auf Achsen, Einheiten, Kurven,
        Randfehler und Trajektorien prüfen. Sichtbefund mit Rohdaten und nächster Hypothese verbinden.
        Vision liefert keinen mathematischen Beweis und keine Erfüllung numerischer Grenzwerte.
        Ohne aktuelles Bild/Werkzeug keine Sichtprüfung behaupten.
        """;

    // Adopt only at a new user-run boundary, never by rewriting evaluated history.
    internal static void EnsureAtNewRunBoundary(List<LmChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message.Role == "system"
                && message.Content?.Contains(Instructions, StringComparison.Ordinal) == true
            || message.Role == "user"
                && message.Content?.StartsWith("Missum-Laufanweisung:\n" + Instructions, StringComparison.Ordinal) == true)) return;
        var index = messages.FindLastIndex(message => message.Role == "user"
            && !ContextPlanner.IsNativeRuntimeInstruction(message));
        messages.Insert(index >= 0 ? index : messages.Count, new("system", Instructions));
    }
}

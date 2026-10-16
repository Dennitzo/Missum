namespace Missum.Ai.Server.Core.Policies;

public static class SubagentAgentPolicy
{
    public const string Manager = """
        Parallel arbeitender Subagent:
        Wenn subagent.spawn angeboten wird, kann das ausgewählte lokale Modell unabhängig auf GPU0 und GPU1 arbeiten.
        Teile umfangreiche Aufträge in konkrete, unabhängige Teilaufgaben auf. Weise eine passende Teilaufgabe mit
        subagent.spawn dem Subagenten zu und bearbeite gleichzeitig deine eigene Aufgabe. Übergebe Ziel, erwartetes
        Ergebnis und eindeutige Schreibpfade, damit parallele Dateiänderungen nicht kollidieren.
        Keine selbst erfundenen Zeitvorgaben, Eile oder Aufwandsschranken an den Subagenten delegieren. Auch aufwendige
        mathematische Beweise sind erwünscht; die Teilaufgabe fachlich eingrenzen, nicht zeitlich abkürzen. Er erhält
        denselben aktuellen Kontext, dasselbe Modell und dieselben autorisierten Werkzeuge sowie Workspace-Rechte.
        Der Gateway entscheidet anhand der tatsächlich verfügbaren GPU-Ressourcen, ob Delegation möglich ist.
        Hole das Ergebnis mit subagent.wait und übernimm abgeschlossene Arbeit samt erzeugten Dateien direkt.
        Für Zahlenwerte und Berechnungen nutzt der jeweils zuständige Agent die verfügbaren Rechenwerkzeuge
        wie math.evaluate oder Python. Übernimm deren tatsächliche Ergebnisse, statt numerische Resultate zu schätzen.
        Führe die Teilaufgabe nicht selbst erneut aus und prüfe sie nicht noch einmal. Verwende die Ergebnisbelege
        für die weitere Verarbeitung. Bei einem Fehler bearbeite nur explizit offene Punkte. Schließe deinen
        Gesamtauftrag erst ab, nachdem alle delegierten Ergebnisse verarbeitet wurden. Nutze keinen Subagenten
        für triviale Antworten oder Aufgaben, die nicht unabhängig parallel bearbeitet werden können.
        """;

    public const string CompletedWork = """
        Die erfolgreich delegierte Teilaufgabe ist abgeschlossen und ihr tatsächliches Ergebnis wurde übernommen.
        Verwende die gelieferten Zahlen, Quellen, Prozessbelege und erzeugten Artefakte unmittelbar für die weitere
        Verarbeitung. Erstelle jetzt keine Dateiinventur der Child-Artefakte mit coding.list oder ähnlichen Werkzeugen.
        Lies deren Ergebnisdateien nicht erneut zur Existenz-, Inhalts-, Schema- oder Plausibilitätskontrolle und
        schreibe kein zusätzliches verify_artifacts.py oder anderes Kontrollskript für die abgeschlossene Arbeit.
        Wiederhole weder ihre Ausführung noch ihre Berechnungen. Eine angekündigte Kontrolle ist ebenfalls kein
        weiterer notwendiger Arbeitsschritt. Bearbeite ausschließlich deine noch offene eigene Aufgabe, etwa die
        unabhängige symbolische Herleitung mit SymPy, die fachliche Einordnung und das Hauptmanuskript.
        Tatsächliche Fehler oder ausdrücklich offene Punkte werden gezielt bearbeitet.
        """;
}

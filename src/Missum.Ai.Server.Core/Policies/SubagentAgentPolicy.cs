namespace Missum.Ai.Server.Core.Policies;

public static class SubagentAgentPolicy
{
    public const string Manager = """
        Parallel arbeitender Subagent:
        Wenn subagent.spawn angeboten wird, kann das ausgewählte lokale Modell unabhängig auf GPU0 und GPU1 arbeiten.
        Teile umfangreiche Aufträge in konkrete, unabhängige Teilaufgaben auf. Weise eine passende Teilaufgabe mit
        subagent.spawn dem Subagenten zu und bearbeite gleichzeitig deine eigene Aufgabe. Übergebe Ziel, erwartetes
        Ergebnis und eindeutige Schreibpfade, damit parallele Dateiänderungen nicht kollidieren. Der Subagent erhält
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

    public const string EarlyResearchDelegation = """
        Frühe Arbeitsteilung für den aktuellen Forschungsauftrag:
        Wenn du der Hauptagent bist, beginne vor Quellenabrufen, Berechnungen oder Dateiänderungen mit genau
        einem strukturierten subagent.spawn-Aufruf. Formuliere aus dem vollständigen Nutzerauftrag eine konkrete,
        unabhängig bearbeitbare Teilaufgabe. Lege erwartetes Ergebnis, relevante Randbedingungen und eindeutige
        Schreibpfade und überprüfbare Abnahmekriterien fest. Beginne den task-Text mit einer prägnanten Titelzeile
        aus höchstens ungefähr acht Wörtern; beschreibe die konkrete Aufgabe nach einem Zeilenumbruch.
        Benenne im Auftrag auch, welche anderen Teile du selbst gleichzeitig bearbeitest.
        Halte diese erste Zuweisung klein: Wähle eine unabhängige Teilfrage und starte sie, ohne vorher
        die gesamte Theorie oder das Manuskript auszuarbeiten. Eine kurze sichtbare Einleitung darf den
        Aufruf begleiten. Die fachliche Ausarbeitung erfolgt danach parallel und wird früh gespeichert.
        Teile die Quellenrecherche nach unabhängigen Fragen oder Quellenbereichen auf; bei einer geeigneten
        numerischen Teilaufgabe kann der Subagent diese übernehmen, während du die Quellen und Herleitung
        bearbeitest. Delegiere niemals bloß den gesamten Auftrag und führe dieselbe Teilaufgabe nicht parallel aus.
        Warte nach dem Start nicht sofort: Bearbeite deine eigene Aufgabe mit den angebotenen Werkzeugen und
        hole das Child-Ergebnis erst, wenn du es tatsächlich für die weitere Verarbeitung benötigst.
        web.search, web.fetch und gegebenenfalls web.deepResearch stehen beiden Agenten im Rahmen desselben
        Nutzerauftrags zur Verfügung. Übernimm erfolgreich abgeschlossene Child-Arbeit und echte Werkzeugbelege
        direkt. Als Subagent bearbeitest du ausschließlich deinen zuletzt zugewiesenen Auftrag und startest
        keinen weiteren Subagenten. Diese Arbeitsteilung verändert weder den gewählten Reasoning-Modus noch
        den fachlichen Umfang des Nutzerauftrags.
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
        research.deliverables.verify bleibt für das Bereitstellen und die technische Abschlussprüfung der aktuellen
        Publikation und der belegten Simulation zulässig. Es verlangt keine manuelle Wiederholungsprüfung des
        Child-Ergebnisses. Tatsächliche Fehler oder ausdrücklich offene Punkte werden gezielt bearbeitet.
        """;
}

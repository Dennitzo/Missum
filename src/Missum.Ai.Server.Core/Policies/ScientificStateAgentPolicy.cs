using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Policies;

internal static class ScientificStateAgentPolicy
{
    internal const string Instructions = """
        Kanonische wissenschaftliche Arbeit (section-delta-v1):
        research.read liefert den dauerhaften Stand, stabile Objektkennungen, Revisionen und echte Quellen-/Experimentkennungen.
        Beginne nach einer gegebenenfalls verlangten frühen Delegation mit der fachlichen Grundlage: Forschungsfrage,
        Voraussetzungen, Definitionen, Hypothesen und einem ersten passenden Publikationsabschnitt. Schreibe diese früh mit
        research.update. Recherchiere danach gezielt zur jeweils offenen Aussage oder zum nächsten Prüfziel; arbeite nicht
        zuerst ein vollständiges Literaturprogramm ab. Dieser Ablauf gilt ebenso ohne zweite GPU.
        Speichere neue Erkenntnisse und Änderungen als einzelne Objekte mit stabiler id und der tatsächlich gelesenen
        expectedRevision. section und contribution enthalten jeweils nur ihren eigenen vollständigen aktuellen Abschnitt;
        niemals die ganze Publikation im Chat oder in einem Update wiederholen. Missum rendert die kanonischen Abschnitte.
        Bei vorhandenen Objekten ändere gezielt Felder über changes[].patch, besonders status, review und Belegzuordnungen.
        patch enthält nur geänderte Felder und erhält den gespeicherten wissenschaftlichen Text sowie nicht angegebene
        Referenzen; data und patch sind gegenseitige Alternativen. Nutze expectedRevision aus dem aktuellen Stand.
        Arrays werden ersetzt, null entfernt optionale Felder. Neue Objekte benötigen vollständiges data, keine patch.
        Lade bei einem Konflikt nur die betroffene id erneut. Bewahre auch gescheiterte, widerlegte oder offene Hypothesen
        mit Voraussetzungen, Vorhersage, Diagnose und nächster konkreter Prüfung. Ein erfolgreicher Prozess ist noch kein
        wissenschaftlicher Beweis; referenziere tatsächliche Quellen, Experimente und Prüfbelege über ihre gelieferten IDs.
        Halte vor dimensionsabhängigen Herleitungen Geometrie, Radius-/Flächenkonventionen, Einheiten und Geltungsbereich
        ausdrücklich fest. Prüfe die betroffene Beziehung passend zur Aussage symbolisch, durch Rücksubstitution oder
        Dimensionsvergleich; dieselbe Beziehung muss in SI und natürlichen Einheiten nach Rückumrechnung übereinstimmen.
        ProcessSucceeded bestätigt nur die Ausführung, nicht die fachliche Richtigkeit der Eingabe oder Schlussfolgerung.
        Gleiche bekannte externe Formeln und relevante Literaturbehauptungen gezielt mit einer abrufbaren Originalquelle
        ab; vorhandene passende Quellenbelege wiederverwenden. Ohne Beleg als ungeprüft kennzeichnen, nicht als bestätigt.
        Löse erkennbare widersprüchliche Faktoren, Einheiten oder Ergebnisse vor dem fachlichen Abschluss auf; technische
        PDF-Erstellung und research.deliverables.verify ersetzen diese Prüfung nicht. Keine pauschale Wahrheitsgarantie.
        Der Hauptagent besitzt die Publikationsabschnitte und führt Beiträge zusammen. Als Subagent verwende eigene IDs
        mit deiner Agent-ID und ':' als Präfix, schreibe contributions und deine eigenen Forschungsobjekte, keine section
        und keinen globalen Titel. Übernimm abgeschlossene Child-Ergebnisse direkt; prüfe deren Arbeit nicht erneut.
        Wähle Berechnung, formale Prüfung, Simulation oder weitere Quellen anhand des konkreten Nutzerauftrags und der
        offenen Anforderung. Python und eine Abbildung sind keine allgemeine Pflicht für jede Forschungsfrage.
        research.deliverables.verify prüft vor dem Abschluss den aktuellen kanonischen Stand und die gerenderte Publikation
        sowie ausdrücklich erforderliche Auswertungen. Bearbeite nur die konkreten fehlenden Ziele und Diagnosen.
        Ein Ablauf mit drei weiteren Quellenaktionen ohne Erkenntnis oder Prüfziel benötigt eine fachliche Entscheidung,
        keine unveränderte Wiederholung. Werkzeuge und autonomes Weiterarbeiten bleiben dabei verfügbar.
        """ + "\n\n" + PublicationWorkflowInstructions + "\n\n" + ScientificDerivationPolicy.Instructions;

    internal const string PublicationWorkflowInstructions = """
        [MISSUM_SCIENCE_PUBLICATION_REVIEW:v3]
        Fachliche Zielerfüllung ist von einer fertigen Publikation zu unterscheiden. Übertrage die tatsächlichen
        Nutzerziele früh in required=true requirements. Eine offen eingeräumte quantitative Abweichung wird nicht durch
        einen Reviewtext, eine qualitative Illustration oder einen erfolgreichen Python-Prozess zur erfüllten Lösung.
        Für numerische Anforderungen lege vor der Rechnung quantitativeValidation fest: observable (die tatsächlich
        verglichene Messgröße), reference (unabhängige Referenz mit Geltungsbereich), artifactPath (work/...json oder
        artifacts/...json), sampling (Monte-Carlo/Stichprobe ja/nein) und checks mit metric, kind und maximum.
        kind=accuracy prüft eine begründete Fehlerschranke, kind=convergence eine kontrollierte Verfeinerung;
        bei sampling=true ist zusätzlich kind=uncertainty nötig. Die ausgeführte Rechnung schreibt die real gemessenen
        nichtnegativen Werte als {"observable":"exakt wie im Vertrag","reference":"exakt wie im Vertrag",
        "metrics":{"relativeError":0.001,"gridChange":0.0001}} in die Ergebnisdatei. Fehlende oder abweichende
        observable/reference erlauben keine Wiederverwendung einer fremden Messung für diese Anforderung.
        Verknüpfe deren echtes Experiment über experimentIds; Missum prüft Hash, Herkunft und jeden Grenzwert selbst.
        Wähle Toleranzen aus dem Nutzerauftrag und der erforderlichen Aussagegenauigkeit, nicht nach den Ergebnissen.
        Bestehende Kriterien niemals nach einem Misserfolg lockern, löschen oder gegen eine bequemere Messgröße tauschen.
        Bei numerischen Publikationsabschnitten muss eine zugehörige erforderliche Anforderung dasselbe Experiment
        quantitativ prüfen. classification=technical ist ausschließlich für technische Illustrationen ohne numerischen
        Nachweisanspruch zulässig; eine geforderte quantitative Lösung darf nicht zu einer Illustration umetikettiert werden.
        Pearson-Korrelation oder optische Ähnlichkeit allein belegen keine quantitative Übereinstimmung. Vergleiche
        dieselbe Observable, Normierung, Einheiten, Integrationsgrenzen und Bins; prüfe absolute und relative Fehler,
        Rand-/Diskretisierungsfehler sowie statistische Unsicherheit passend zur Aussage. Verfeinere dt, Gitter,
        Gebietsgröße und Stichprobe kontrolliert, nicht mehrere Parameter unbemerkt zugleich. Eine exakte Behauptung
        benötigt eine analytische/formale Herleitung oder eine ausdrücklich begrenzte numerische Näherung mit Fehlerschranke.
        Bei einer Abweichung Diagnose und nächste konkrete Rechnung speichern und fachlich weiterarbeiten. Kein Abschluss
        allein wegen Laufzeit, Tokenverbrauch oder fertiger PDF. openLimit dokumentiert ein ungelöstes erforderliches Ziel
        und erfüllt es nicht. Nicht lösbare/offene Fragen ehrlich benennen; unbegrenzte Laufzeit garantiert keine Lösung.
        Gleiche Publikationsformeln ausdrücklich mit der im Skript geprüften Größe ab: Ein korrektes Programm bestätigt
        keine abweichende Formel im Text. Auch offene coding.updatePlan-Annahmekriterien verhindern den Abschluss.
        Ordne neue Themen fachlich in die bestehende Gliederung ein: Lies vorhandene section.order-Werte und wähle eine
        passende Position, statt neue Abschnitte standardmäßig vor Abschnitt 1 einzufügen. Vorhandene stabile IDs erhalten;
        Missum setzt fortlaufende Gliederungsnummern. Nutze die volle einspaltige Textbreite, keine künstlichen Spalten.
        Formuliere den Publikationstitel fachlich prägnant, möglichst etwa 40–50 Zeichen; vermeide lange Untertitel.
        Missum misst den tatsächlichen Satz deterministisch. Bei Titelüberlauf nur title fachlich kürzen, keine Ellipsen;
        bei Überschriftenüberlauf nur die konkret gemeldete Abschnittsüberschrift kürzen, den Sachtext vollständig erhalten.
        Verknüpfe entstandene Plots mit passenden Publikationsabschnitten über experimentIds und figureCaptions, mit
        echten experimentRecordId/artifactPath, Achsen und Einheiten. Interpretiere die Ergebnisse im Abschnittstext.
        Bei HTML-/Canvas-Simulationen erzeuge relevante statische Plots zusätzlich mit research.code.execute als PNG
        und ordne diese dem Abschnitt zu; die interaktive HTML-Datei allein liefert noch keine PDF-Abbildung.
        Abschluss und nachträgliche Prüfung alter Publikationen: Prüfe jeden Hauptabschnitt und seine Claims/Hypothesen
        wirklich gegen die gelesenen Originalbelege, Rechnungen, Annahmen, Einheiten und mögliche Widersprüche.
        Dokumentiere pro geprüftem bestehenden Objekt patch:{status:"tatsächlich erreichter Status",review:{itemRevision:expectedRevision+1,sourceAssessment:"konkreter Quellenabgleich",
        calculationAssessment:"Rechnung/Grenzfall bzw. begründet nicht erforderlich",contradictionAssessment:"aufgelöste Widersprüche oder konkrete Grenzen",
        scope:"Geltungsbereich und verbleibende Grenzen"}}. Die Belege bleiben echte sourceIds/evidenceIds/experimentIds/checkIds.
        status und review sind Geschwister: changes[].patch.status neben changes[].patch.review bzw. bei vollständigem
        Ersatz changes[].data.status neben changes[].data.review. Nie review.status; review enthält keinen status.
        Vor dem Aufruf prüfen, dass beide Felder im selben data-/patch-Objekt stehen. Für Prüfdaten bestehender Objekte
        patch nutzen, unveränderten contentMarkdown nicht erneut generieren. Dies ersetzt ältere reine data-Anweisungen.
        Setze nur den tatsächlich erreichten fachlichen Status; offene Grenzen mit reason und openLimit erhalten.
        Rein definitorische, technische oder hypothetische Texte als classification=definition/technical/hypothesis mit
        Annahmen bzw. Begründung kennzeichnen. Ein Review ohne Belege bestätigt keine empirische oder mathematische Aussage.
        Jede spätere Inhaltsänderung benötigt eine neue Prüfung ihrer Objektversion. Erst ein erfolgreicher aktueller
        science-review-v1-Beleg von research.deliverables.verify erlaubt den Abschluss. Dies gilt auch beim Fortsetzen.
        """;

    // Only a new user-run adopts this workflow; an evaluated in-flight checkpoint remains unchanged.
    internal static void EnsurePublicationWorkflowAtNewRunBoundary(List<LmChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Any(message => message.Role == "system"
                && message.Content?.Contains(PublicationWorkflowInstructions, StringComparison.Ordinal) == true
            || message.Role == "user"
                && message.Content?.StartsWith("Missum-Laufanweisung:\n" + PublicationWorkflowInstructions, StringComparison.Ordinal) == true)) return;
        var index = messages.FindLastIndex(message => message.Role == "user" && !ContextPlanner.IsNativeRuntimeInstruction(message));
        messages.Insert(index >= 0 ? index : messages.Count, new("system", PublicationWorkflowInstructions));
    }
}

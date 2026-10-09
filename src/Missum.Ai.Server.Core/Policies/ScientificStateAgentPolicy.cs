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
        Bei vorhandenen Objekten ändere nur status, review, Belegzuordnungen oder andere Metadaten über changes[].patch.
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
        [MISSUM_SCIENCE_PUBLICATION_REVIEW:v1]
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
        review immer mit ausdrücklich angegebenem status einreichen; nicht nur die Prüfrevision ändern und den Status
        offenlassen. Unveränderten contentMarkdown nicht erneut generieren oder zusammen mit Prüfdaten übertragen.
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

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
        Arrays werden ersetzt. Unbekannte oder unveränderte Felder weglassen; null löscht nur vorhandene optionale Felder.
        noChange=true und leere changedIds bedeuten keinen Fortschritt. Nicht denselben Aufruf wiederholen: review und
        quantitativeValidation benötigen echte Objekte, null legt sie nicht an. Neue Objekte brauchen data, keine patch.
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
        und keinen globalen Titel. Prüfe bei Child-Ergebnissen Modell, Konventionen, Observable und Belegbindung vor der
        Übernahme. Passende abgeschlossene Rechnungen wiederverwenden; nur konkrete Lücken oder Widersprüche nachprüfen.
        Wähle Berechnung, formale Prüfung, Simulation oder weitere Quellen anhand des konkreten Nutzerauftrags und der
        offenen Anforderung. Python und eine Abbildung sind keine allgemeine Pflicht für jede Forschungsfrage.
        research.deliverables.verify prüft vor dem Abschluss den aktuellen kanonischen Stand und die gerenderte Publikation
        sowie ausdrücklich erforderliche Auswertungen. Bearbeite nur die konkreten fehlenden Ziele und Diagnosen.
        Ein Ablauf mit drei weiteren Quellenaktionen ohne Erkenntnis oder Prüfziel benötigt eine fachliche Entscheidung,
        keine unveränderte Wiederholung. Werkzeuge und autonomes Weiterarbeiten bleiben dabei verfügbar.
        """ + "\n\n" + PublicationWorkflowInstructions + "\n\n" + ScientificDerivationPolicy.Instructions;

    internal const string PublicationWorkflowInstructions = """
        [MISSUM_SCIENCE_PUBLICATION_REVIEW:v5]
        Nutzerziele als required=true requirements unverändert erfüllen. PDF, Exitcode 0,
        Pearson-Korrelation oder qualitative Ähnlichkeit erfüllen keine quantitative Anforderung. openLimit erfüllt es nicht.
        Vor Rechnungen quantitativeValidation setzen: observable, unabhängige reference, artifactPath=work/...json,
        sampling und checks:[{metric,kind,maximum}]. kind=accuracy, convergence und bei sampling=true uncertainty.
        Reale nichtnegative metrics mit identischer observable ins JSON schreiben; mit echten experimentIds verknüpfen.
        Fehlende Prüfungen/Messzeiten sind null, nie 0. Toleranzen nicht lockern, Ziele nicht zu technical umetikettieren.
        Dieselbe Observable, Normierung, Einheiten und Bins vergleichen. Rohdaten und gleiche feste Glättung unterscheiden;
        Stichprobenrauschen abschätzen und unabhängige Seeds prüfen. dt/Gitter/Gebiet getrennt verfeinern, Lösungen selbst
        vergleichen, nicht Differenzen skalarer Fehlernormen. Eine endliche Referenz ist nicht exakt; Erstankunft bei Rückfluss ist nicht automatisch positiver Fluss.
        Pilotlauf messen, maximumStageSeconds beachten. Artefakte auf Schema,
        Status, Zeiten und Zahlen prüfen: reine None-Ausgabe oder Modulimport ist kein fachlicher Fortschritt. Große Arrays
        nicht mit coding.read laden. Altdaten ersetzen keinen neuen Lauf. Zellmassen nicht erneut
        mit Zellfläche multiplizieren. Bei Stagnation die Methode prüfen statt blind Teilchenzahl erhöhen.
        Speicher-/Sandboxblockaden zuerst beheben; Parameterwechsel lösen sie nicht. Formal weiterarbeiten
        oder mit konkreter offener Grenze fortsetzbar bleiben; keine Messwerte erfinden und nicht wegen Laufzeit abschließen.
        Ein bearbeitbarer Projektbaum, keine Revisions-/Backupkopien. Publikation am aktuellen Pfad ersetzen; Hashbelege binden
        Resultate an tatsächlich gelesene Quellen. expectedRevision prüft Konflikte, erzeugt keine Projektkopie.
        section.order fachlich wählen; kurze Titel, einspaltig, Plots mit experimentIds und figureCaptions.
        Bei Titel-/Formelüberlauf nur betroffenen Abschnitt korrigieren.
        Prüfe Claims, Quellen, Rechnungen, Einheiten, Formeln und Grenzen. Numerik mit supported/completed einordnen,
        verified benötigt einen echten formalen/fachlichen Beleg. changes[].patch.status neben changes[].patch.review bzw. changes[].data.status neben changes[].data.review.
        Nie review.status; review enthält keinen status. Den tatsächlich erreichten fachlichen Status setzen.
        Prüfung als review-Objekt mit konkreten Feststellungen ergänzen; review:null ist kein Platzhalter.
        Ungeprüftes offen benennen. Ein Review ohne Belege bestätigt keine Aussage.
        review:{itemRevision:expectedRevision+1,sourceAssessment,
        calculationAssessment,contradictionAssessment,scope}.
        Metadaten mit patch ändern, Text erhalten. Danach frischen science-review-v1-Beleg mit
        research.deliverables.verify verlangen; offene coding.updatePlan-Annahmekriterien verhindern den Abschluss.
        """;

    internal const string ConsistencyInstructions = """
        Geometrie, Radius-/Flächenkonventionen und Geltungsbereich vor der Rechnung festlegen. Rücksubstitution,
        Dimensionsvergleich und Rückumrechnung übereinstimmen lassen; widersprüchliche Faktoren auflösen.
        ProcessSucceeded bestätigt nur die technische Ausführung. HashVerified und research.deliverables.verify ersetzen diese Prüfung nicht.
        Mit Originalquelle und vorhandene passende Belege prüfen; fehlende Nachweise bleiben ungeprüft.
        Keine pauschale Wahrheitsgarantie. Numerische Konsistenz, formaler Beweis und experimentelle Bestätigung unterscheiden.
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

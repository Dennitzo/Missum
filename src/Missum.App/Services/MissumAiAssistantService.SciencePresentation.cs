using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task<string> BuildPublicationWorkspacePromptAsync(ChatSession session, string prompt, CancellationToken cancellationToken)
    {
        if (sciencePublications is null || string.IsNullOrWhiteSpace(session.CodingWorkspacePath)) return prompt;
        var scaffoldWarning = await SciencePublicationScaffold.TryEnsureAsync(session.ChatMode, session.CodingWorkspacePath,
            cancellationToken).ConfigureAwait(false);
        if (scaffoldWarning is not null)
            RunDiagnostic(logger, session.Id.ToString("D"), "Optionale Aktualisierung des lokalen Publikationswerkzeugs zurückgestellt; AI-Lauf wird fortgesetzt", scaffoldWarning);
        var projectId = $"research-{session.Id:N}";
        sciencePresentation?.RegisterWorkspace(projectId, session.CodingWorkspacePath, session.Title);
        var path = await sciencePublications.EnsureWorkspaceDocumentAsync(projectId, session.CodingWorkspacePath,
            session.Title, cancellationToken).ConfigureAwait(false);
        var relative = Path.GetRelativePath(session.CodingWorkspacePath, path).Replace('\\', '/');
        var localPdfTool = File.Exists(Path.Combine(session.CodingWorkspacePath, "Publikation-erzeugen.ps1"))
            ? " Nach jeder Phase und vor ihrem Abschluss oder Beginn der nächsten Phase führe den vollständigen PDF-Bau im Projektordner über coding.command aus: Programm powershell.exe, getrennte Argumente -NoProfile, -ExecutionPolicy, Bypass, -File, .\\Publikation-erzeugen.ps1. "
                + "Nutze dafür nicht -CheckOnly. Prüfe, dass der Exitcode 0 ist, und lies die Ausgabe; behebe Fehler zuerst an den genannten Originalstellen und wiederhole den Bau bis zum Erfolg. Eine erhaltene ältere PDF ist kein Erfolg des aktuellen Baus. "
                + "Konkrete Fehler und Fundstellen stehen im Werkzeugergebnis sowie in publication-build.log und publication-build.json. Wenn Bau oder Korrektur nicht möglich sind, halte die Phase offen und nenne die Blockade. "
            : " Publikation-erzeugen.ps1 fehlt im Projektordner; der für jede Phase erforderliche PDF-Bau ist blockiert. Melde die konkrete Einschränkung und markiere die Phase nicht als abgeschlossen. ";
        return prompt + "\n\nPublikation dieses Projekts: " + relative
            + ". Diese direkt bearbeitbare Markdown-Datei ist der aktuelle Projektstand; Missum setzt daraus automatisch die PDF im vorhandenen Layout mit Inhaltsverzeichnis. "
            + "Gliedere Hauptthemen mit ##, Unterthemen mit ### und weitere Ebenen entsprechend; Missum nummeriert Überschriften und Inhaltsverzeichnis gemeinsam als 1, 1.1, 1.2, 2 usw. "
            + "Der Dokumenttitel mit # bleibt unnummeriert. Missum erzeugt das einzige Inhaltsverzeichnis automatisch; lege in Publikation.md weder ein manuelles Inhaltsverzeichnis noch einen zusätzlichen Navigationsabschnitt wie „Aufbau dieser Arbeit“, „Gliederung der Arbeit“ oder eine Kapitelübersicht an. Verwende keine eigenen Kapitelnummern. "
            + "Führe verwendete Literatur und Datengrundlagen am Ende unter ## Literatur und Daten, gegliedert in ### Fachliteratur und ### Forschungsdaten. "
            + "Erhalte eindeutige Zitierschlüssel und verlinke die tatsächlich verwendeten Quellen, Datensätze oder Projektdateien mit knappen bibliografischen Angaben; erfinde keine Einträge. "
            + "Abschnitte können mit den normalen Projektwerkzeugen ergänzt, geändert oder gelöscht werden. "
            + "Plots können beim jeweiligen Thema als Markdown-Abbildung mit relativen Dateipfaden und Erläuterung eingebettet werden. "
            + "Veraltete Projektplots und Skripte dürfen passend zum Auftrag mit den normalen Projektwerkzeugen bearbeitet oder entfernt werden. "
            + "Geprüfte Lean-Beweise arbeitest du in der Publikation wie in einer Mathematikvorlesung aus: "
            + "Formuliere Voraussetzungen und Satz, führe den mathematischen Beweis mit nachvollziehbaren Zwischenschritten und erläuterten Gleichungen und schließe mit der Folgerung. "
            + "Verwende mathematischen Fließtext mit LaTeX statt Lean-Syntax, Taktiken oder bloß umformatiertem Quellcode. "
            + "Verweise knapp auf die separate Lean-Datei und das tatsächliche Prüfergebnis; bleibe exakt bei der geprüften Aussage und ihren Voraussetzungen. "
            + "Der aktive Science-Skill steuert Etappen und die zur Aussage passenden Prüfmethoden. "
            + "Belegquellen und verfügbare Originaldokumente im vorhandenen Projektordner inputs/ speichern und wiederverwenden; keinen zusätzlichen Quellen-Ordner anlegen. Herkunft sowie verwendete Stellen festhalten. "
            + "Forschungsziel, Herleitung, tatsächliche Prüfung und offene Grenzen klar trennen; eine erfolgreiche Ausführung beweist keine fachliche Richtigkeit."
            + localPdfTool;
    }
}

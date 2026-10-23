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
            ? " Für die lokale PDF-Erzeugung steht Publikation-erzeugen.ps1 im Projekt bereit: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\\Publikation-erzeugen.ps1. "
                + "Mit -CheckOnly wird nur geprüft. Fortschritt und konkrete Fehler mit Fundstellen stehen im Werkzeugergebnis sowie in publication-build.log und publication-build.json; bearbeite die betroffenen Originalstellen in Publikation.md. "
            : "";
        return prompt + "\n\nPublikation dieses Projekts: " + relative
            + ". Diese direkt bearbeitbare Markdown-Datei ist der aktuelle Projektstand; Missum setzt daraus automatisch die PDF im vorhandenen Layout mit Inhaltsverzeichnis. "
            + "Gliedere Hauptthemen mit ##, Unterthemen mit ### und weitere Ebenen entsprechend; Missum nummeriert Überschriften und Inhaltsverzeichnis gemeinsam als 1, 1.1, 1.2, 2 usw. "
            + "Der Dokumenttitel mit # bleibt unnummeriert; eigene Nummern oder ein manuelles Inhaltsverzeichnis sind nicht nötig. "
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
            + "Verwendete Quellen und verfügbare Originaldokumente unter Quellen/ speichern und Herkunft sowie verwendete Stellen festhalten. "
            + "Forschungsziel, Herleitung, tatsächliche Prüfung und offene Grenzen klar trennen; eine erfolgreiche Ausführung beweist keine fachliche Richtigkeit."
            + localPdfTool;
    }
}

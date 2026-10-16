using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private async Task<string> BuildPublicationWorkspacePromptAsync(ChatSession session, string prompt, CancellationToken cancellationToken)
    {
        if (sciencePublications is null || string.IsNullOrWhiteSpace(session.CodingWorkspacePath)) return prompt;
        var projectId = $"research-{session.Id:N}";
        sciencePresentation?.RegisterWorkspace(projectId, session.CodingWorkspacePath, session.Title);
        var path = await sciencePublications.EnsureWorkspaceDocumentAsync(projectId, session.CodingWorkspacePath,
            session.Title, cancellationToken).ConfigureAwait(false);
        var relative = Path.GetRelativePath(session.CodingWorkspacePath, path).Replace('\\', '/');
        return prompt + "\n\nPublikation dieses Projekts: " + relative
            + ". Diese direkt bearbeitbare Markdown-Datei ist der aktuelle Projektstand; Missum setzt daraus automatisch die PDF im vorhandenen Layout mit Inhaltsverzeichnis. "
            + "Abschnitte können mit den normalen Projektwerkzeugen ergänzt, geändert oder gelöscht werden. "
            + "Plots können beim jeweiligen Thema als Markdown-Abbildung mit relativen Dateipfaden und Erläuterung eingebettet werden. "
            + "Veraltete Projektplots und Skripte dürfen passend zum Auftrag mit den normalen Projektwerkzeugen bearbeitet oder entfernt werden. "
            + "Geprüfte Lean-Beweise arbeitest du in der Publikation wie in einer Mathematikvorlesung aus: "
            + "Formuliere Voraussetzungen und Satz, führe den mathematischen Beweis mit nachvollziehbaren Zwischenschritten und erläuterten Gleichungen und schließe mit der Folgerung. "
            + "Verwende mathematischen Fließtext mit LaTeX statt Lean-Syntax, Taktiken oder bloß umformatiertem Quellcode. "
            + "Verweise knapp auf die separate Lean-Datei und das tatsächliche Prüfergebnis; bleibe exakt bei der geprüften Aussage und ihren Voraussetzungen. "
            + "Ein fachlich geeigneter Beleg genügt: Webquelle, gültiger Lean-Beweis oder eine Simulation, die Gleichungen anhand ihrer Berechnungsdaten prüft. "
            + "Websuche, Lean und Simulationen sind Möglichkeiten; wähle Vorgehen und Werkzeuge passend zum Nutzerauftrag selbst.";
    }
}

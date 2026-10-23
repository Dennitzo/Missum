namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    // General chats with a workspace have the same project-writing tools, but
    // do not receive the coding system policy. This applies only to allowed edits.
    internal static string WithRepositoryMaintenance(string prompt, string? workspace, bool audiobook = false, bool compact = false)
    {
        if (audiobook || string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return prompt;
        if (compact)
            return prompt + "\n\nBei erlaubten Projektänderungen auch .gitignore und die GitHub-README vollständig und korrekt pflegen; fehlende Dateien anlegen, "
                + "README-Vorlagen mit tatsächlichen Projektinformationen ausarbeiten und Missums lokalen Publikationsrenderer samt Hilfsskript nicht versionieren. "
                + "Vorhandene Inhalte erhalten; reine Leseaufträge bleiben lesend.";
        return prompt + "\n\nProjektpflege bei erlaubten Dateiänderungen: Lege fehlende .gitignore und README.md im Projektstamm an "
            + "und halte beide passend zu tatsächlichen Projektänderungen aktuell. Erhalte vorhandene Inhalte und Missums verwalteten Ignore-Block. "
            + "Die README nutzt GitHub-Markdown, relative Links und Codeblöcke; beschreibe Zweck, Struktur, Voraussetzungen und nur tatsächlich nutzbare Befehle. "
            + "Ergänze projektspezifische Ignore-Regeln außerhalb des verwalteten Blocks. Lokale Publikationshelfer (.publication-renderer/, "
            + "Publikation-erzeugen.ps1, PDF-ERZEUGEN.md und publication-build-Logs/Receipts/Lockdateien) gehören nicht auf GitHub; "
            + "verlinke sie dort nicht als verfügbare Repositorydateien. Publikationsquellen, Quellen, Forschungsdaten, Plots und Beweise bleiben erhalten. "
            + "Keine zusätzlichen Modellläufe und kein eigenständiges Entfernen bereits versionierter Dateien aus dem Git-Index. "
            + "Bei reinen Lese- oder Analyseaufträgen keine Dateien verändern; unveränderte Dokumentation nicht neu schreiben.";
    }
}

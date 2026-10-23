namespace Missum.Ai.Server.Core.Policies;

/// <summary>Shared project hygiene for ordinary coding and scientific work.</summary>
public static class RepositoryMaintenancePolicy
{
    public const string Instructions = """
        Projektpflege: Bei erlaubter Projektbearbeitung fehlende .gitignore und README.md im Projektwurzelordner
        anlegen und bei relevanten Änderungen aktuell halten, auch bei neuen Projekten. Reine Lese-/Analyseaufträge
        bleiben lesend. Bestehende Inhalte erhalten. README als GitHub-Markdown: Titel, passende Abschnitte, relative
        Links, Befehle in Codeblöcken; Zweck, Struktur, Voraussetzungen und echte Start-/Build-/Prüfkommandos.
        Keine Funktionen oder Resultate erfinden. Dafür gezielt lesen; unveränderte Dateien nicht neu schreiben.
        Missums verwalteten .gitignore-Block erhalten; projektbezogene Regeln außerhalb ergänzen.
        Lokale Hilfsdateien /.publication-renderer/, /Publikation-erzeugen.ps1, /PDF-ERZEUGEN.md,
        /publication-build.json, /publication-build.log und /.publication-build.lock nicht für GitHub aufnehmen.
        In der GitHub-README keine defekten Links auf solche ignorierten lokalen Hilfsdateien anlegen.
        Publikationsquellen, Forschungsdaten, Quellen und Beweisdateien nicht pauschal ignorieren oder entfernen.
        .gitignore entversioniert keine Dateien: ohne Nutzerauftrag kein git rm/--cached und keine Indexentfernung.
        Subagenten ändern README/.gitignore nur bei zugewiesener Schreibzuständigkeit; sonst Anpassungen dem Hauptagenten
        mitteilen. Keine zusätzlichen Modellläufe für Projektpflege starten.
        """;
}

using System.Text.Json;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class RetiredRepositoryInstructionsTests
{
    // Historical fixture only: these strings are never added to a current prompt.
    private const string RetiredSystemBlock = """
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

    private const string RetiredCompactSuffix = "\n\nBei erlaubten Projektänderungen auch .gitignore und die GitHub-README vollständig und korrekt pflegen; fehlende Dateien anlegen, "
        + "README-Vorlagen mit tatsächlichen Projektinformationen ausarbeiten und Missums lokalen Publikationsrenderer samt Hilfsskript nicht versionieren. "
        + "Vorhandene Inhalte erhalten; reine Leseaufträge bleiben lesend.";

    private const string RetiredGeneralSuffix = "\n\nProjektpflege bei erlaubten Dateiänderungen: Lege fehlende .gitignore und README.md im Projektstamm an "
        + "und halte beide passend zu tatsächlichen Projektänderungen aktuell. Erhalte vorhandene Inhalte und Missums verwalteten Ignore-Block. "
        + "Die README nutzt GitHub-Markdown, relative Links und Codeblöcke; beschreibe Zweck, Struktur, Voraussetzungen und nur tatsächlich nutzbare Befehle. "
        + "Ergänze projektspezifische Ignore-Regeln außerhalb des verwalteten Blocks. Lokale Publikationshelfer (.publication-renderer/, "
        + "Publikation-erzeugen.ps1, PDF-ERZEUGEN.md und publication-build-Logs/Receipts/Lockdateien) gehören nicht auf GitHub; "
        + "verlinke sie dort nicht als verfügbare Repositorydateien. Publikationsquellen, Quellen, Forschungsdaten, Plots und Beweise bleiben erhalten. "
        + "Keine zusätzlichen Modellläufe und kein eigenständiges Entfernen bereits versionierter Dateien aus dem Git-Index. "
        + "Bei reinen Lese- oder Analyseaufträgen keine Dateien verändern; unveränderte Dokumentation nicht neu schreiben.";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void RemovesOnlyTheRetiredSectionFromASavedSystemPrefix(string newline)
    {
        var separator = newline + newline;
        List<LmChatMessage> messages = [new("system", "Stable policy" + separator
            + RetiredSystemBlock.ReplaceLineEndings(newline) + separator + "Keep the other policy."), new("user", "Meine Frage")];

        RetiredRepositoryInstructions.RemoveFrom(messages);

        Assert.Equal("Stable policy" + separator + "Keep the other policy.", messages[0].Content);
        Assert.Equal("Meine Frage", messages[1].Content);
        var first = messages.ToArray();
        RetiredRepositoryInstructions.RemoveFrom(messages);
        Assert.Equal(first, messages);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "\n\nPublikation dieses Projekts: Publikation.md. Weitere vorhandene Hinweise.")]
    [InlineData(false, "\n\nMISSUM_DOCUMENT_ATTACHMENTS\nEin Dokument.")]
    public void RemovesGeneratedSuffixAndPreservesOriginalTaskAndFollowingMetadata(bool general, string metadata)
    {
        const string original = "Bitte repariere den Fehler.\nDie README soll ausdrücklich unverändert bleiben.";
        List<LmChatMessage> messages = [new("user", original + (general ? RetiredGeneralSuffix : RetiredCompactSuffix) + metadata)];

        RetiredRepositoryInstructions.RemoveFrom(messages, [original]);

        Assert.Equal(original + metadata, messages[0].Content);
    }

    [Fact]
    public void LeavesSourceOutputsAssistantTextAndQuotedOrIncompleteUserContentUntouched()
    {
        LmChatMessage[] original = [
            new("tool", "Quelle:\n" + RetiredSystemBlock + RetiredCompactSuffix, ToolCallId: "read-source"),
            new("assistant", RetiredSystemBlock + RetiredGeneralSuffix),
            new("user", "Bitte zitiere folgenden Text:\n```text\nAlt" + RetiredCompactSuffix + "\n```"),
            new("user", "Bitte zitiere:\n~~~text\nAlt" + RetiredCompactSuffix),
            new("user", RetiredSystemBlock),
            new("user", "Meine Frage" + RetiredCompactSuffix + "\nDas ist meine zusätzliche eigene Anweisung."),
            new("user", "Bitte bearbeite README.md und .gitignore gemäß meinem Auftrag."),
            new("user", "Auftrag\n\nProjektpflege bei erlaubten Dateiänderungen: Lege fehlende .gitignore und README.md im Projektstamm an unvollständiger Text"),
        ];
        var messages = original.ToList();

        RetiredRepositoryInstructions.RemoveFrom(messages, ["Der ursprüngliche Auftrag"]);

        Assert.Equal(original, messages);
        for (var index = 0; index < original.Length; index++) Assert.Same(original[index], messages[index]);
    }

    [Fact]
    public void CleanedCheckpointKeepsHistoryAndRunStateAndStaysCleanAfterSerialization()
    {
        var checkpoint = new AgentRunCheckpoint([
            new("system", "Stable policy\n\n" + RetiredSystemBlock),
            new("user", "Der ursprüngliche Auftrag" + RetiredCompactSuffix),
            new("assistant", "Der bisherige Befund."),
            new("tool", "Originalausgabe", ToolCallId: "call-1"),
        ], 7, 12, 400, 80, PreserveSessionPromptPrefix: true, ContextProfileVersion: "compact-v1");
        var messages = checkpoint.Messages.ToList();
        RetiredRepositoryInstructions.RemoveFrom(messages, ["Der ursprüngliche Auftrag"]);
        var restored = JsonSerializer.Deserialize<AgentRunCheckpoint>(JsonSerializer.Serialize(checkpoint with { Messages = messages }))!;

        Assert.Equal(checkpoint.Messages.Count, restored.Messages.Count);
        Assert.Equal(checkpoint.RoundCount, restored.RoundCount);
        Assert.Equal(checkpoint.ToolCallCount, restored.ToolCallCount);
        Assert.True(restored.PreserveSessionPromptPrefix);
        Assert.Equal("Stable policy", restored.Messages[0].Content);
        Assert.Equal("Der ursprüngliche Auftrag", restored.Messages[1].Content);
        Assert.Equal(checkpoint.Messages[2], restored.Messages[2]);
        Assert.Equal(checkpoint.Messages[3], restored.Messages[3]);
        Assert.Contains("Projektpflege:", checkpoint.Messages[0].Content);
    }

    [Fact]
    public void NativeRequestProjectionAlsoCleansPinnedCompactPrefixesWithoutMutatingSavedHistory()
    {
        var prefix = CompactAgentContextPolicy.Marker + "\n\nStable policy";
        LmChatMessage[] original = [new("system", prefix + "\n\n" + RetiredSystemBlock),
            new("user", "Weiterarbeiten" + RetiredCompactSuffix)];

        var projected = ModelRuntimeClient.PrepareNativeRequestMessages(original);

        Assert.Equal(prefix, projected[0].Content);
        Assert.Equal(original[1], projected[1]);
        Assert.NotEqual(original[0].Content, projected[0].Content);
        Assert.Contains("Projektpflege:", original[0].Content);
        Assert.Equal(projected, ModelRuntimeClient.PrepareNativeRequestMessages(projected));
    }

    [Fact]
    public void ExactCurrentUserQuotationsRemainUnchangedEvenIfTheirPrefixMatchesAnotherOriginal()
    {
        const string introduction = "Bitte erkläre diesen alten Hinweis:";
        var compactQuotation = introduction + RetiredCompactSuffix;
        var generalQuotation = introduction + RetiredGeneralSuffix;
        List<LmChatMessage> messages = [new("user", compactQuotation), new("user", generalQuotation)];
        var originals = messages.ToArray();

        RetiredRepositoryInstructions.RemoveFrom(messages, [introduction, compactQuotation, generalQuotation]);

        Assert.Equal(originals, messages);
        Assert.Same(originals[0], messages[0]);
        Assert.Same(originals[1], messages[1]);
    }

    [Fact]
    public void HistoricalSuffixWithoutMatchingOriginalProvenanceIsPreserved()
    {
        List<LmChatMessage> messages = [new("user", "Ein historisches Zitat" + RetiredCompactSuffix)];
        var original = messages[0];

        RetiredRepositoryInstructions.RemoveFrom(messages, ["Ein anderer aktueller Auftrag"]);

        Assert.Same(original, messages[0]);
        RetiredRepositoryInstructions.RemoveFrom(messages);
        Assert.Same(original, messages[0]);
    }
}

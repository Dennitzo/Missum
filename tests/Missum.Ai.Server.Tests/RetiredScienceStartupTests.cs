using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class RetiredScienceStartupTests
{
    private const string Folded = "Missum-Laufanweisung:\n";
    private const string OldSkill = """
        Beginne jeden Science-Hauptlauf sofort mit genau einem kurzen deutschen Einleitungssatz als sichtbare
        AI-Nachricht: konkretes Thema und Ziel des Nutzerauftrags sowie der erste geplante Arbeitsschritt.
        Bei einer Fortsetzung an die offene Etappe des Arbeitsplans anknüpfen. Keine allgemeine Startfloskel und keine
        ausführliche fachliche Detailanalyse vor diesem Satz; die Einleitung gehört nicht in den Reasoning-Kanal.
        Der erste Modellturn enthält nur diesen Satz und endet direkt danach, ohne Arbeitsplan oder Werkzeugaufruf.
        Erst im nächsten Modellturn ist der erste Werkzeugschritt coding.updatePlan: Lege zunächst einen nichtleeren
        """;
    private const string OldStages = """
        Zum Laufstart zuerst genau einen kurzen, auftragsbezogenen deutschen Einleitungssatz als normale AI-Nachricht
        ausgeben: Thema, Ziel und erster geplanter Arbeitsschritt, bevor du die fachliche Lösung vertiefst.
        Den ersten Modellturn direkt nach diesem Satz beenden, ohne Arbeitsplan oder Werkzeugaufruf.
        Erst im nächsten Modellturn den Arbeitsplan mit coding.updatePlan speichern oder bestätigen.
        Die Einleitung allein beendet weder den Auftrag noch ersetzt sie den Plan.
        Vor weiterer Werkzeugarbeit kurz erklären, welche konkrete Etappe jetzt folgt. Nach einem belegten Zwischenergebnis
        den Fortschritt in gut vorlesbaren deutschen Sätzen erklären. Keine pauschale generische Startankündigung.
        """;

    private static string LegacyInstruction => "MISSUM_SCIENCE_INITIAL_INTRODUCTION_V1\n"
        + "Gib jetzt zuerst genau einen kurzen deutschen Einleitungssatz als normale sichtbare AI-Nachricht aus. "
        + "Beende diesen Modellturn direkt nach dem Satz. "
        + "Dieser erste Turn enthält ausschließlich die Einleitung: keinen Arbeitsplan und keinen Werkzeugaufruf. "
        + "Gespeicherter nächster Schritt zur Orientierung, keine zusätzlichen Rechte:\n"
        + "{\"runId\":\"saved-run\",\"nextPlannedStage\":null}";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedStartupInstructionIsRemovedWithoutChangingConversationalHistory(bool folded)
    {
        var user = new LmChatMessage("user", "Untersuche die Gleichung.");
        var assistant = new LmChatMessage("assistant", "Ich untersuche zunächst die Gleichung.");
        var tool = new LmChatMessage("tool", "{\"status\":\"completed\"}", ToolCallId: "proof");
        var messages = new List<LmChatMessage> { new("system", "Systemkern"), user,
            new(folded ? "user" : "system", (folded ? Folded : "") + LegacyInstruction), assistant, tool };

        Assert.True(ScienceSkill.RemoveRetiredStartupInstructions(messages));

        Assert.Equal(4, messages.Count);
        Assert.Same(user, messages[1]);
        Assert.Same(assistant, messages[2]);
        Assert.Same(tool, messages[3]);
        Assert.False(ScienceSkill.RemoveRetiredStartupInstructions(messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyRetiredParagraphsAreReplacedInsideTheMergedSkillPolicy(bool crlf)
    {
        const string prefix = "Unveränderter nativer Systemkern.\n\n";
        const string suffix = "\n\nMISSUM_SCIENCE_SKILL_MODULE_V1:research-standard-v2\nBelege in inputs/ ablegen.";
        var old = prefix + ScienceSkill.Marker + OldSkill + "\nArbeitsplan speichern.\n\n" + OldStages + suffix;
        if (crlf) old = old.ReplaceLineEndings("\r\n");
        var messages = new List<LmChatMessage> { new("system", old) };

        Assert.True(ScienceSkill.RemoveRetiredStartupInstructions(messages));

        var updated = Assert.Single(messages).Content!;
        Assert.StartsWith(crlf ? prefix.ReplaceLineEndings("\r\n") : prefix, updated);
        Assert.EndsWith(crlf ? suffix.ReplaceLineEndings("\r\n") : suffix, updated);
        Assert.DoesNotContain("Einleitung", updated);
        Assert.Contains("Der erste Werkzeugschritt eines Science-Hauptlaufs ist coding.updatePlan", updated);
        Assert.Contains("Arbeitsplan speichern.", updated);
        Assert.False(ScienceSkill.RemoveRetiredStartupInstructions(messages));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredPlanRequirementNoLongerRefersToAnIntroduction(bool folded)
    {
        const string plan = "MISSUM_SCIENCE_INITIAL_PLAN_V1\nNach der kurzen sichtbaren Einleitung folgt der erste Werkzeugschritt dieses Science-Laufs: "
            + "Lege mit genau einem coding.updatePlan-Aufruf einen Plan an.\n{\"storedSteps\":[]}";
        var messages = new List<LmChatMessage> { new(folded ? "user" : "system", (folded ? Folded : "") + plan) };

        Assert.True(ScienceSkill.RemoveRetiredStartupInstructions(messages));

        Assert.Equal((folded ? Folded : "") + plan.Replace("Nach der kurzen sichtbaren Einleitung folgt der erste Werkzeugschritt dieses Science-Laufs: ",
            "Erster Arbeitsschritt dieses Science-Laufs: ", StringComparison.Ordinal), messages[0].Content);
    }

    [Fact]
    public void UserQuotesSourceBodiesAndUnmarkedInstructionsArePreserved()
    {
        LmChatMessage[] originals = [new("user", LegacyInstruction), new("user", "Bitte analysiere diese frühere Anweisung:\n" + Folded + LegacyInstruction),
            new("assistant", LegacyInstruction), new("tool", ScienceSkill.Marker + OldSkill, ToolCallId: "source"),
            new("system", "Zitierter Text ohne Skillmarker:\n" + OldSkill), new("user", Folded + LegacyInstruction)];
        var messages = originals.ToList();

        Assert.False(ScienceSkill.RemoveRetiredStartupInstructions(messages, [originals[^1].Content!]));

        for (var index = 0; index < originals.Length; index++) Assert.Same(originals[index], messages[index]);
    }

    [Fact]
    public void FreshCurrentSkillIsAnExactNoOp()
    {
        var message = new LmChatMessage("system", ScienceSkill.Instructions);
        var messages = new List<LmChatMessage> { message, new("user", "Neue Forschungsfrage.") };

        Assert.False(ScienceSkill.RemoveRetiredStartupInstructions(messages));
        Assert.Same(message, messages[0]);
    }

    [Fact]
    public void NewRunContinuationRemovesRetiredPolicyButKeepsSavedAssistantTextAndCurrentRequest()
    {
        var narration = new LmChatMessage("assistant", "Ich bearbeite die gespeicherte Aufgabe.");
        var previous = new AgentRunCheckpoint([new("system", ScienceSkill.Marker + OldSkill),
            new("user", "Alte Frage"), new("user", Folded + LegacyInstruction), narration], 1, 0, 100, 20,
            PreserveSessionPromptPrefix: true);
        LmChatMessage[] initial = [new("system", ScienceSkill.Instructions), new("user", "Bearbeite nun die offene Etappe.")];

        var continued = CodingSessionContext.Continue(previous, initial, science: true);

        Assert.Contains(narration, continued);
        Assert.Same(initial[^1], continued[^1]);
        Assert.DoesNotContain(continued, message => message.Content?.Contains("MISSUM_SCIENCE_INITIAL_INTRODUCTION_V1", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(continued, message => message.Role == "system" && message.Content?.Contains("Einleitung", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void OldCheckpointFlagIsIgnoredRatherThanRestoringTheRetiredStartupFeature()
    {
        var checkpoint = JsonSerializer.Deserialize<AgentRunCheckpoint>(
            "{\"messages\":[],\"roundCount\":1,\"toolCallCount\":0,\"inputTokens\":100,\"outputTokens\":20,\"scienceIntroductionPublished\":true}",
            MissumAiProtocol.CreateJsonOptions());

        Assert.NotNull(checkpoint);
        Assert.Empty(checkpoint.Messages);
        Assert.DoesNotContain("scienceIntroductionPublished", JsonSerializer.Serialize(checkpoint, MissumAiProtocol.CreateJsonOptions()));
    }
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class AgentNarrationPolicyTests
{
    [Theory]
    [InlineData(RunMode.General, false, null)]
    [InlineData(RunMode.Coding, false, null)]
    [InlineData(RunMode.General, true, null)]
    [InlineData(RunMode.General, false, "compact-v1")]
    [InlineData(RunMode.Coding, false, "compact-v1")]
    [InlineData(RunMode.General, true, "compact-v1")]
    public void AllModesReceiveOneSharedInitialPolicyWithoutAnAdditionalMessageOrUserSuffix(
        RunMode mode, bool science, string? contextProfile)
    {
        const string prompt = "Bearbeite die Aufgabe und berichte verständlich über den Fortschritt.";
        var request = new RunRequest(MissumAiProtocol.Version, mode, [new("user", [new("text", prompt)])],
            DeepResearch: science, ResearchOptions: science ? new(ProjectId: "research-fixture", ProtocolVersion: 2) : null);
        string[] tools = science ? [ClientToolNames.ResearchRead, ClientToolNames.ResearchUpdate] : [];

        var messages = RunProcessor.CreateInitialMessages(request, mode == RunMode.Coding ? "coding" : "general", tools, contextProfile);

        Assert.Equal(2, messages.Count);
        var policy = Assert.Single(messages, item => item.Role == "system").Content!;
        Assert.Equal(2, policy.Split(AgentNarrationPolicy.Instructions, StringSplitOptions.None).Length);
        Assert.Equal(prompt, Assert.Single(messages, item => item.Role == "user").Content);
        Assert.DoesNotContain("Vor jedem Werkzeug", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("vor jedem Werkzeugaufruf", policy, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPolicyRequestsVisibleMilestonesAndEvidenceBasedCompactCompletion()
    {
        var policy = AgentNarrationPolicy.Instructions;
        Assert.Contains("vor der ersten Arbeitsaktion oder dem ersten Werkzeugaufruf", policy, StringComparison.Ordinal);
        Assert.Contains("auftragsbezogene Einleitung in ein bis zwei Sätzen", policy, StringComparison.Ordinal);
        Assert.Contains("wesentlichen Etappen", policy, StringComparison.Ordinal);
        Assert.Contains("AI-Nachrichten, nicht im Reasoning-Kanal", policy, StringComparison.Ordinal);
        Assert.Contains("keine Tickmeldungen", policy, StringComparison.Ordinal);
        Assert.Contains("Keine privaten Gedankengänge", policy, StringComparison.Ordinal);
        Assert.Contains("tatsächliche Werkzeugwerte unverfälscht", policy, StringComparison.Ordinal);
        Assert.Contains("ohne zusätzliche Modellrunden", policy, StringComparison.Ordinal);
        Assert.Contains("reine Kurzantworten und exakte Ausgabeformate haben Vorrang", policy, StringComparison.Ordinal);
        Assert.Contains("erreichte Ergebnisse oder Änderungen kompakt", policy, StringComparison.Ordinal);
        Assert.Contains("tatsächlich ausgeführte", policy, StringComparison.Ordinal);
        Assert.Contains("Prüfungen mit ihrem Ergebnis", policy, StringComparison.Ordinal);
        Assert.Contains("offene Punkte oder Grenzen ausdrücklich", policy, StringComparison.Ordinal);
        Assert.Contains("geplante Prüfungen sind keine erfolgten Prüfungen", policy, StringComparison.Ordinal);
        Assert.Contains("statt einer aufgeblähten Checkliste", policy, StringComparison.Ordinal);
        Assert.True(policy.Length < 1400, $"Shared narration contains {policy.Length} characters.");
    }

    [Theory]
    [InlineData(ConversationProfile.Audiobook, null)]
    [InlineData(ConversationProfile.ContextPreparation, null)]
    [InlineData(ConversationProfile.Audiobook, "compact-v1")]
    [InlineData(ConversationProfile.ContextPreparation, "compact-v1")]
    public void ProseAndInternalCompactionDoNotGainOperationalNarration(ConversationProfile profile, string? contextProfile)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.General,
            [new("user", [new("text", "Fortsetzen")])], ConversationProfile: profile);
        var messages = RunProcessor.CreateInitialMessages(request, "general", [], contextProfile);
        Assert.DoesNotContain(AgentNarrationPolicy.Instructions, messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRunBoundaryAddsTheRuleOnceWithoutTreatingUserTextAsPolicy()
    {
        var user = new LmChatMessage("user", AgentNarrationPolicy.Instructions);
        List<LmChatMessage> messages = [new("system", "Bisherige Sitzungsregeln."), user];

        AgentNarrationPolicy.EnsureInstructions(messages);
        var first = messages.ToArray();
        AgentNarrationPolicy.EnsureInstructions(messages);

        Assert.Equal(first, messages);
        Assert.StartsWith("Bisherige Sitzungsregeln.", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(2, messages[0].Content!.Split(AgentNarrationPolicy.Instructions, StringSplitOptions.None).Length);
        Assert.Same(user, messages[1]);
    }

    [Fact]
    public void ExistingCheckpointUpgradeDoesNotInjectTheNewNarrationIntoItsPinnedPrefix()
    {
        List<LmChatMessage> messages = [new("system", CodingAgentPolicy.ReasoningLanguagePrompt
            + CodingAgentPolicy.StagedExecutionAndNarrationPrompt + CodingAgentPolicy.WorkspaceDependenciesPrompt
            + CodingAgentPolicy.ScientificResearchPrompt + MathFormattingPolicy.Instructions), new("user", "Fortsetzen")];
        var before = messages.ToArray();

        CodingAgentPolicy.EnsureCurrentInstructions(messages);

        Assert.Equal(before, messages);
        Assert.DoesNotContain(AgentNarrationPolicy.Instructions, messages[0].Content, StringComparison.Ordinal);
    }
}

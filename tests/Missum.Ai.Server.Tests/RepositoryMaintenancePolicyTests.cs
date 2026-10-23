using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class RepositoryMaintenancePolicyTests
{
    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    [InlineData("compact-v1", false, false)]
    [InlineData("compact-v1", true, false)]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData("compact-v1", false, true)]
    [InlineData("compact-v1", true, true)]
    public void EveryCodingAndSciencePrefixMaintainsTheSameProjectContract(string? profile, bool science, bool child)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", "Implementiere die Änderung im bestehenden Projekt.")])],
            CodingOptions: new(WorkspacePath: "C:/workspace"), WorkflowProfile: science ? ScienceSkill.Profile : null,
            Subagent: child ? new("parent", "child", "Abgegrenzte Änderung", "session") : null);

        var messages = RunProcessor.CreateInitialMessages(request, "coding", [ClientToolNames.CodingRead, ClientToolNames.CodingWrite], profile);
        var system = string.Join("\n", messages.Where(message => message.Role == "system").Select(message => message.Content));

        Assert.Equal(2, system.Split(RepositoryMaintenancePolicy.Instructions, StringSplitOptions.None).Length);
        Assert.Contains("fehlende .gitignore und README.md", system, StringComparison.Ordinal);
        Assert.Contains("bei relevanten Änderungen aktuell halten", system, StringComparison.Ordinal);
        Assert.Contains("GitHub-Markdown", system, StringComparison.Ordinal);
        Assert.Contains("Befehle in Codeblöcken", system, StringComparison.Ordinal);
        Assert.Contains("Lese-/Analyseaufträge", system, StringComparison.Ordinal);
        Assert.Contains("Bestehende Inhalte erhalten", system, StringComparison.Ordinal);
        Assert.Contains("/.publication-renderer/", system, StringComparison.Ordinal);
        Assert.Contains("/Publikation-erzeugen.ps1", system, StringComparison.Ordinal);
        Assert.Contains("/PDF-ERZEUGEN.md", system, StringComparison.Ordinal);
        Assert.Contains("keine Indexentfernung", system, StringComparison.Ordinal);
        Assert.Contains("Schreibzuständigkeit", system, StringComparison.Ordinal);
        Assert.Equal(science, messages.Any(message => message.Content?.Contains(ScienceSkill.Marker, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void LegacyUpgradeIgnoresUserCopiesAndPreservesHistoryWithoutRepeatedPolicyGrowth()
    {
        var user = new LmChatMessage("user", RepositoryMaintenancePolicy.Instructions);
        var tool = new LmChatMessage("tool", "Vorhandene Quelldatei", ToolCallId: "read");
        List<LmChatMessage> messages = [new("system", "Bestehende Regeln."), user, tool];

        CodingAgentPolicy.EnsureCurrentInstructions(messages);
        var first = messages.ToArray();
        CodingAgentPolicy.EnsureCurrentInstructions(messages);

        Assert.Equal(first, messages);
        Assert.StartsWith("Bestehende Regeln.", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(2, messages[0].Content!.Split(RepositoryMaintenancePolicy.Instructions, StringSplitOptions.None).Length);
        Assert.Same(user, messages[1]);
        Assert.Same(tool, messages[2]);
    }

    [Theory]
    [InlineData(ConversationProfile.ContextPreparation)]
    [InlineData(ConversationProfile.Audiobook)]
    public void BackgroundTextPreparationDoesNotAcquireProjectMaintenanceWork(ConversationProfile profile)
    {
        var request = new RunRequest(MissumAiProtocol.Version, RunMode.Coding,
            [new("user", [new("text", "Bereite den bestehenden Text auf.")])], ConversationProfile: profile,
            CodingOptions: new(WorkspacePath: "C:/workspace"));

        var policy = CompactAgentContextPolicy.Build(request, [ClientToolNames.CodingRead]);

        Assert.DoesNotContain(RepositoryMaintenancePolicy.Instructions, policy, StringComparison.Ordinal);
    }
}

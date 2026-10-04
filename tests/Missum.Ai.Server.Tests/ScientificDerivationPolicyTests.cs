using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Tests;

public sealed class ScientificDerivationPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BothScienceProfilesGiveMainAndChildTheSameFullPublicationContract(bool compact, bool child)
    {
        var request = Request() with { Subagent = child ? new("parent-run", "child-agent", "Herleitung", "science-session") : null };
        var messages = RunProcessor.CreateInitialMessages(request, "general",
            [ClientToolNames.ResearchRead, ClientToolNames.ResearchUpdate], compact ? ToolContextProfiles.Current : null);
        if (!compact) RunProcessor.EnsureScientificStateInstructions(messages);
        var original = messages.ToArray();

        ScientificDerivationPolicy.EnsureAtNewRunBoundary(messages);
        Assert.Equal(original, messages);
        var system = messages[0].Content!;
        Assert.Equal(2, system.Split(ScientificDerivationPolicy.Instructions, StringSplitOptions.None).Length);
        foreach (var requirement in new[] { "Knappe Chatbegleitung", "Ausgangsgleichungen", "lückenlos",
            "Rand-/Anfangsbedingungen", "Begründe jeden", "Näherungen", "Zwischenwerten", "Einheiten in jeder Gleichheit",
            "kurzen Listen, nicht Tabellen", "section.data.contentMarkdown", "contribution.data.contentMarkdown",
            "ersetzen keinen Publikationsabschnitt", "verkürzte vorhandene Herleitungen", "Lücken dort ausdrücklich benennen" })
            Assert.Contains(requirement, system, StringComparison.Ordinal);
        Assert.Equal("Leite die Bewegungsgleichung vollständig her.", messages[^1].Content);
    }

    [Fact]
    public void OldSessionAdoptsTheContractOnceWithoutChangingItsEvaluatedPrefixOrToolHistory()
    {
        var system = new LmChatMessage("system", "Alte Regeln.\n" + RunProcessor.ScientificStateInstructionsHeading);
        var result = new LmChatMessage("tool", "Gespeicherter Nachweis.", ToolCallId: "existing-read");
        var user = new LmChatMessage("user", "Fortsetzen.");
        var messages = new List<LmChatMessage> { system, new("user", "Originalauftrag."), result, user };

        ScientificDerivationPolicy.EnsureAtNewRunBoundary(messages);
        Assert.Same(system, messages[0]);
        Assert.Same(result, messages[2]);
        Assert.Same(user, messages[^1]);
        Assert.Equal(ScientificDerivationPolicy.Instructions, messages[^2].Content);
        Assert.Equal("system", messages[^2].Role);
        ScientificDerivationPolicy.EnsureAtNewRunBoundary(messages);
        Assert.Equal(5, messages.Count);

        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var nativeGuide = Assert.Single(normalized, message => message.Content?.StartsWith(
            "Missum-Laufanweisung:\n" + ScientificDerivationPolicy.Instructions, StringComparison.Ordinal) == true);
        Assert.Equal("user", nativeGuide.Role);
        ScientificDerivationPolicy.EnsureAtNewRunBoundary(normalized);
        Assert.Equal(5, normalized.Count);
        Assert.Contains(result, normalized);
    }

    [Theory]
    [InlineData(RunMode.General)]
    [InlineData(RunMode.Coding)]
    public void OrdinaryModesDoNotReceiveTheSciencePublicationContract(RunMode mode)
    {
        var request = Request() with { Mode = mode, ClientCapabilities = [], ResearchOptions = null };
        foreach (var profile in new string?[] { null, ToolContextProfiles.Current })
            Assert.DoesNotContain(ScientificDerivationPolicy.Instructions,
                RunProcessor.CreateInitialMessages(request, "general", [], profile)[0].Content!, StringComparison.Ordinal);
    }

    private static RunRequest Request() => new(MissumAiProtocol.Version, RunMode.General,
        [new("user", [new("text", "Leite die Bewegungsgleichung vollständig her.")])],
        SessionId: "science-session", ClientCapabilities: ["research.deliverables"],
        ResearchOptions: new(ProjectId: "science-project", ProtocolVersion: 2));
}

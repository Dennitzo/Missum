using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Agents;
using Missum.Ai.Server.Core.Coding;

namespace Missum.Ai.Server.Tests;

public sealed class SpecializedAgentProtocolTests
{
    [Fact]
    public void RegistryKeepsPinnedVersionsAndAllowsAdditionalAgentProfiles()
    {
        var old = new ExampleProfile(new("auditor", "1", "Prüfer"));
        var current = new ExampleProfile(new("auditor", "2", "Prüfer"));
        var registry = new SpecializedAgentRegistry([new LoopAgentProfile(), old, current]);
        Assert.Same(current, registry.Resolve("auditor"));
        Assert.Same(old, registry.Resolve("auditor", "1"));
        Assert.IsType<LoopAgentProfile>(registry.Resolve(SpecializedAgentProfiles.Loop));
        Assert.Throws<ArgumentException>(() => registry.Resolve("unknown"));
        Assert.Throws<ArgumentException>(() => registry.Resolve("auditor", "3"));
    }

    [Fact]
    public void CompletionNeedsEverySavedAcceptanceCriterionAndNoOpenGoalPoint()
    {
        var complete = new SpecializedAgentDecision("completed", "Die Zielaussage ist geprüft.", [new("Herleitung", true, "Unabhängige Gegenprüfung")], []);
        var state = CodingWorkingState.Create("Ziel") with { AcceptanceCriteria = [new("criterion", "Grenzfall", "pending", [])] };
        Assert.Throws<InvalidDataException>(() => Parse(complete, state));
        Assert.Throws<InvalidDataException>(() => Parse(complete with { OpenPoints = ["Einheit unklar"] }));
        Assert.Throws<InvalidDataException>(() => Parse(complete with { Checks = [new("Herleitung", false, "Noch offen")] }));
        Assert.Equal("completed", Parse(complete).Outcome);
    }

    [Fact]
    public void AnOpenQuestionCanContinueOrAskForSpecificUserInputButCannotBecomeSuccessByMissingFields()
    {
        Assert.Throws<InvalidDataException>(() => Parse(new("continue", "Eine Lücke bleibt.", [new("Prüfung", false, "Nicht ausgeführt")], ["Prüfung fehlt"])));
        Assert.Throws<InvalidDataException>(() => SpecializedAgentProtocol.Parse("{}", null));
        var decision = Parse(new("continue", "Eine Lücke bleibt.", [new("Prüfung", false, "Nicht ausgeführt")], ["Grenzfall fehlt"], "Prüfe den Grenzfall unabhängig."));
        Assert.Contains("Grenzfall fehlt", SpecializedAgentProtocol.Render(new("loop", "1", "Loop-Agent"), decision));
        Assert.Equal("needs_input", Parse(decision with { Outcome = "needs_input", Summary = "Welche Randbedingung soll gelten?", NextPrompt = null }).Outcome);
    }

    private static SpecializedAgentDecision Parse(SpecializedAgentDecision decision, CodingWorkingState? state = null) =>
        SpecializedAgentProtocol.Parse(JsonSerializer.Serialize(decision, MissumAiProtocol.CreateJsonOptions()), state);

    private sealed class ExampleProfile(SpecializedAgentDefinition definition) : ISpecializedAgentProfile
    {
        public SpecializedAgentDefinition Definition { get; } = definition;
        public string BuildReviewInstruction(SpecializedAgentReviewContext context) => context.Goal;
    }
}

using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceResearchStrategyTests
{
    [Fact]
    public void ConcisePolicyRequiresAlternativesAndPersistedDiscriminatingChecksBeforeLargeComputations()
    {
        var instruction = ScienceResearchStrategyPolicy.Instructions;
        Assert.True(instruction.Length < 2200);
        Assert.StartsWith(ScienceResearchStrategyPolicy.Marker, instruction, StringComparison.Ordinal);
        Assert.Contains("mindestens zwei fachlich verschiedene", instruction, StringComparison.Ordinal);
        Assert.Contains("analytische Herleitung", instruction, StringComparison.Ordinal);
        Assert.Contains("Gedankenexperiment", instruction, StringComparison.Ordinal);
        Assert.Contains("Fehlerschranke", instruction, StringComparison.Ordinal);
        Assert.Contains("vor der Rechnung eine hypothesis", instruction, StringComparison.Ordinal);
        Assert.Contains("reason und nextCheck", instruction, StringComparison.Ordinal);
        Assert.Contains("unterscheidbare Messgröße", instruction, StringComparison.Ordinal);
        Assert.Contains("kein Methodenwechsel", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void MethodChoiceAndVisionCannotRelaxOrReplaceQuantitativeAcceptance()
    {
        var instruction = ScienceResearchStrategyPolicy.Instructions;
        Assert.Contains("Lockere keine Toleranzen", instruction, StringComparison.Ordinal);
        Assert.Contains("dieselbe Observable und Referenz", instruction, StringComparison.Ordinal);
        Assert.Contains("gemeinsamer Vergleichsbasis", instruction, StringComparison.Ordinal);
        Assert.Contains("media.analyze", instruction, StringComparison.Ordinal);
        Assert.Contains("image.input", instruction, StringComparison.Ordinal);
        Assert.Contains("Rohdaten", instruction, StringComparison.Ordinal);
        Assert.Contains("keinen mathematischen Beweis", instruction, StringComparison.Ordinal);
        Assert.Contains("keine Erfüllung", instruction, StringComparison.Ordinal);
        Assert.Contains("numerischer Grenzwerte", instruction, StringComparison.Ordinal);
        Assert.Contains("keine Sichtprüfung behaupten", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void NewRunInstallsGuideOnceWithoutRewritingEvaluatedToolHistoryOrLatestUserRequest()
    {
        var oldSystem = new LmChatMessage("system", "Evaluated prefix.");
        var oldCall = new LmChatMessage("assistant", "Existing work.");
        var oldReceipt = new LmChatMessage("tool", "Actual measured result.", ToolCallId: "existing-call");
        var latestUser = new LmChatMessage("user", "Fortsetzen mit einem anderen fachlichen Ansatz.");
        List<LmChatMessage> messages = [oldSystem, oldCall, oldReceipt, latestUser];
        ScienceResearchStrategyPolicy.EnsureAtNewRunBoundary(messages);
        ScienceResearchStrategyPolicy.EnsureAtNewRunBoundary(messages);
        Assert.Equal(5, messages.Count);
        Assert.Same(oldSystem, messages[0]);
        Assert.Same(oldCall, messages[1]);
        Assert.Same(oldReceipt, messages[2]);
        Assert.Equal(ScienceResearchStrategyPolicy.Instructions, messages[^2].Content);
        Assert.Same(latestUser, messages[^1]);
    }

    [Fact]
    public void NativeRuntimeNormalizationAndRecoveryDoNotDuplicateTheStrategyGuide()
    {
        List<LmChatMessage> messages = [new("system", "Stable prefix."), new("assistant", "Earlier decision."), new("user", "Forschungsauftrag.")];
        ScienceResearchStrategyPolicy.EnsureAtNewRunBoundary(messages);
        var normalized = ModelRuntimeClient.NormalizeMessageOrderForNativeRuntime(messages).ToList();
        var prior = normalized.ToArray();
        ScienceResearchStrategyPolicy.EnsureAtNewRunBoundary(normalized);
        Assert.Equal(prior, normalized);
        var guide = Assert.Single(normalized, message => message.Content?.StartsWith(
            "Missum-Laufanweisung:\n" + ScienceResearchStrategyPolicy.Instructions, StringComparison.Ordinal) == true);
        Assert.Equal("user", guide.Role);
    }

    [Fact]
    public void EmbeddedCurrentStrategyGuideDoesNotAddAnotherSystemMessage()
    {
        List<LmChatMessage> messages = [new("system", "Other instructions.\n" + ScienceResearchStrategyPolicy.Instructions),
            new("user", "Fortsetzen.")];
        var prior = messages.ToArray();
        ScienceResearchStrategyPolicy.EnsureAtNewRunBoundary(messages);
        Assert.Equal(prior, messages);
    }
}

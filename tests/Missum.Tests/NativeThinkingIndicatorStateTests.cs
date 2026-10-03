using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeThinkingIndicatorStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RealReasoningStartsThinkingWithoutProviderTokenPulses()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveReasoning("answer", "round-1", "Ich untersuche die Voraussetzungen.", Start);
        state.ObserveContent("answer", "", Start);
        Assert.True(Show(state, Start));
        Assert.True(Show(state, Start.AddMinutes(5)));
        state.ObserveContent("answer", "Das Ergebnis lautet", Start.AddMinutes(5));
        Assert.False(Show(state, Start.AddMinutes(5)));
        state.ObserveReasoning("answer", "round-1", "Ich untersuche die Voraussetzungen.", Start.AddMinutes(6));
        Assert.False(Show(state, Start.AddMinutes(6)));
        state.ObserveReasoning("answer", "round-2", "Eine weitere Voraussetzung prüfen.", Start.AddMinutes(7));
        Assert.True(Show(state, Start.AddMinutes(7)));
    }

    [Fact]
    public void OldReasoningReplayCannotStartThinkingAndMetadataDoesNotRenewIt()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveReasoning("answer", "round-1", "Bereits gespeicherter Denkprozess.", Start);
        Assert.False(Show(state, Start.AddMinutes(1)));
        state.ObserveReasoning("answer", "round-1", "Bereits gespeicherter Denkprozess.", Start.AddMinutes(1));
        Assert.False(Show(state, Start.AddMinutes(1)));
        state.ObserveReasoning("answer", "round-1", "Bereits gespeicherter Denkprozess. Neue Überlegung.", Start.AddMinutes(2));
        Assert.True(Show(state, Start.AddMinutes(2)));
        Assert.False(state.ShouldShow("answer", active: false, blocked: false, visible: true, Start.AddMinutes(3)));
    }

    [Fact]
    public void FreshActualTokensShowAnEmptyAnswerAndRemainVisibleThroughLongPauses()
    {
        var state = Generating();

        Assert.Equal("answer", state.MessageId);
        Assert.True(Show(state, Start));
        Assert.True(Show(state, Start.AddSeconds(8)));
        Assert.True(Show(state, Start.AddDays(3)));
    }

    [Fact]
    public void InitialSnapshotTextIsABaselineThatKeepsFreshGenerationEligible()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 25, Start);
        state.ObserveContent("answer", "Bereits vorhandener Absatz", Start);

        Assert.False(Show(state, Start));
        Assert.False(Show(state, Start.AddMilliseconds(749)));
        Assert.True(Show(state, Start.AddMilliseconds(750)));
    }

    [Fact]
    public void ChangedAnswerTextConsumesGenerationAndRequiresNewProgressToShowAgain()
    {
        var state = Generating();
        Assert.True(Show(state, Start));

        state.ObserveContent("answer", "Ein neuer sichtbarer Absatz", Start.AddSeconds(1));
        Assert.False(Show(state, Start.AddSeconds(1)));
        Assert.False(Show(state, Start.AddSeconds(2)));
        Assert.False(Show(state, Start.AddSeconds(7)));

        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddSeconds(8));
        Assert.True(Show(state, Start.AddSeconds(8)));
    }

    [Fact]
    public void GenuineProgressAfterNewTextStillHonorsTheQuietPeriod()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neuer Absatz", Start.AddSeconds(1));
        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddMilliseconds(1100));

        Assert.False(Show(state, Start.AddMilliseconds(1749)));
        Assert.True(Show(state, Start.AddMilliseconds(1750)));
    }

    [Fact]
    public void UnchangedTextDoesNotExtendQuietTimeOrReleaseAnExistingLatch()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 10, Start);
        state.ObserveContent("answer", "Ein Absatz", Start);
        state.ObserveContent("answer", "Ein Absatz", Start.AddMilliseconds(700));

        Assert.True(Show(state, Start.AddMilliseconds(750)));
        state.ObserveContent("answer", "Ein Absatz", Start.AddSeconds(20));
        Assert.True(Show(state, Start.AddSeconds(20)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("generationStarted")]
    [InlineData("generationRetry")]
    [InlineData("promptProcessing")]
    [InlineData("codingLoading")]
    [InlineData("codingCompacting")]
    [InlineData("providerRetryWaiting")]
    [InlineData("responseRecovery")]
    [InlineData("toolSelected")]
    [InlineData("toolRejected")]
    [InlineData("deepResearchSearch")]
    [InlineData("deepResearchSynthesis")]
    [InlineData("codingWaiting")]
    [InlineData("reasoningDelta")]
    [InlineData("contentDelta")]
    public void ProgressPhasesNeverReleaseAnAlreadyVisibleLatch(string? phase)
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveProgress("answer", phase, null, Start.AddSeconds(1));

        Assert.True(Show(state, Start.AddSeconds(1)));
        Assert.True(Show(state, Start.AddHours(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FirstDisplayRequiresPositiveGeneratedTokens(int? tokens)
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", tokens, Start);

        Assert.False(Show(state, Start));
    }

    [Fact]
    public void FirstDisplayRequiresTheOriginalTokenEventTime()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 50, null);

        Assert.False(Show(state, Start));
    }

    [Theory]
    [InlineData("codingWaiting")]
    [InlineData("reasoningDelta")]
    [InlineData("contentDelta")]
    public void InternalFragmentsCannotStartThinkingOrRenewUnshownTokenProgress(string phase)
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", phase, 10, Start);
        Assert.False(Show(state, Start));

        state.ObserveProgress("answer", "tokenProgress", 10, Start.AddSeconds(1));
        state.ObserveProgress("answer", phase, 10, Start.AddSeconds(8));
        Assert.False(Show(state, Start.AddSeconds(9)));
    }

    [Fact]
    public void RepeatedTokenCountsDoNotRenewEligibilityBeforeFirstDisplay()
    {
        var state = Generating();
        state.ObserveProgress("answer", "tokenProgress", 10, Start.AddSeconds(7));
        Assert.False(Show(state, Start.AddSeconds(8)));

        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddSeconds(9));
        Assert.True(Show(state, Start.AddSeconds(9)));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(9)]
    public void RepeatedOrLowerCountsCannotReactivateAfterAnAnswer(int tokens)
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neue Antwort", Start.AddSeconds(1));
        state.ObserveProgress("answer", "tokenProgress", tokens, Start.AddSeconds(2));

        Assert.False(Show(state, Start.AddSeconds(3)));
        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddSeconds(4));
        Assert.True(Show(state, Start.AddSeconds(4)));
    }

    [Fact]
    public void ProgressProducedBeforeNewAnswerTextCannotReactivateItsLatch()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neue Antwort", Start.AddSeconds(2));
        state.ObserveProgress("answer", "tokenProgress", 20, Start.AddSeconds(1));

        Assert.False(Show(state, Start.AddSeconds(3)));
        state.ObserveProgress("answer", "tokenProgress", 21, Start.AddSeconds(4));
        Assert.True(Show(state, Start.AddSeconds(4)));
    }

    [Fact]
    public void ReplayedProgressCannotReactivateAfterANewerPreparationPhase()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neue Antwort", Start.AddSeconds(1));
        state.ObserveProgress("answer", "toolSelected", null, Start.AddSeconds(3));
        state.ObserveProgress("answer", "tokenProgress", 20, Start.AddSeconds(2));

        Assert.False(Show(state, Start.AddSeconds(4)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("toolSelected")]
    [InlineData("providerRetryWaiting")]
    [InlineData("promptProcessing")]
    public void IntermediatePhasesKeepTheConsumedTokenWatermark(string? phase)
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neue Antwort", Start.AddSeconds(1));
        state.ObserveProgress("answer", phase, null, Start.AddSeconds(2));
        state.ObserveProgress("answer", "tokenProgress", 10, Start.AddSeconds(3));

        Assert.False(Show(state, Start.AddSeconds(3)));
        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddSeconds(4));
        Assert.True(Show(state, Start.AddSeconds(4)));
    }

    [Fact]
    public void ANewGenerationAllowsLowerCountsWithoutResurrectingConsumedProgress()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveContent("answer", "Neue Antwort", Start.AddSeconds(1));
        state.ObserveProgress("answer", "generationStarted", null, Start.AddSeconds(2));
        Assert.False(Show(state, Start.AddSeconds(2)));

        state.ObserveProgress("answer", "tokenProgress", 1, Start.AddSeconds(3));
        Assert.True(Show(state, Start.AddSeconds(3)));
    }

    [Fact]
    public void ReplayedPhasesCannotStopNewerActualGeneration()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 30, Start.AddSeconds(2));
        state.ObserveProgress("answer", "toolSelected", 20, Start.AddSeconds(1));

        Assert.True(Show(state, Start.AddSeconds(3)));
    }

    [Fact]
    public void StaleOrFutureSnapshotsCannotCreateTheirFirstLatch()
    {
        var stale = Generating();
        Assert.False(Show(stale, Start.AddSeconds(8)));
        var future = new NativeThinkingIndicatorState();
        future.ObserveProgress("answer", "tokenProgress", 25, Start.AddSeconds(1));
        Assert.False(Show(future, Start));
    }

    [Fact]
    public void AForeignMessageCannotInheritThePreviousLatchOrQuietPeriod()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.ObserveProgress("next", "tokenProgress", 1, Start.AddMilliseconds(100));
        state.ObserveContent("answer", "Verspätete alte Antwort", Start.AddMilliseconds(200));

        Assert.Equal("next", state.MessageId);
        Assert.False(state.ShouldShow("answer", true, false, true, Start.AddMilliseconds(300)));
        Assert.True(state.ShouldShow("next", true, false, true, Start.AddMilliseconds(300)));
    }

    [Fact]
    public void HiddenTabsPreserveTheLatchWithoutAllowingFirstDisplayInBackground()
    {
        var state = Generating();
        Assert.False(state.ShouldShow("answer", true, false, false, Start));
        Assert.False(Show(state, Start.AddSeconds(8)));

        state.ObserveProgress("answer", "tokenProgress", 11, Start.AddSeconds(9));
        Assert.True(Show(state, Start.AddSeconds(9)));
        Assert.False(state.ShouldShow("answer", true, false, false, Start.AddSeconds(10)));
        Assert.True(Show(state, Start.AddDays(1)));
    }

    [Fact]
    public void ToolsBlockOnlyFirstDisplayAndNeverReleaseAnExistingLatch()
    {
        var state = Generating();
        Assert.False(state.ShouldShow("answer", true, true, true, Start));
        Assert.True(Show(state, Start.AddSeconds(1)));

        Assert.True(state.ShouldShow("answer", true, true, true, Start.AddSeconds(2)));
        Assert.True(state.ShouldShow("answer", true, true, true, Start.AddDays(1)));
    }

    [Fact]
    public void NewAnswersWhileATabIsHiddenReleaseItsLatchBeforeReturning()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        Assert.False(state.ShouldShow("answer", true, false, false, Start.AddSeconds(1)));
        state.ObserveContent("answer", "Neue Antwort während des Tabwechsels", Start.AddSeconds(2));

        Assert.False(Show(state, Start.AddSeconds(3)));
    }

    [Fact]
    public void EndingARunPermanentlyConsumesItsLatchAndGenerationEligibility()
    {
        var state = Generating();
        Assert.True(Show(state, Start));

        Assert.False(state.ShouldShow("answer", false, false, true, Start.AddSeconds(1)));
        Assert.False(Show(state, Start.AddSeconds(2)));
    }

    [Fact]
    public void EmptyTextChangesDoNotReleaseAnAlreadyVisibleLatch()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 10, Start);
        state.ObserveContent("answer", "Ein bereits vorhandener Absatz", Start);
        Assert.True(Show(state, Start.AddSeconds(1)));

        state.ObserveContent("answer", "", Start.AddSeconds(2));
        Assert.True(Show(state, Start.AddSeconds(2)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bereits sichtbarer Absatz")]
    public void StreamedWhitespaceDoesNotReleaseTheVisibleLatch(string existingText)
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 10, Start);
        state.ObserveContent("answer", existingText, Start);
        Assert.True(Show(state, Start.AddSeconds(1)));

        state.ObserveContent("answer", existingText + " \n\n\t", Start.AddSeconds(2));
        Assert.True(Show(state, Start.AddSeconds(2)));
        Assert.True(Show(state, Start.AddDays(1)));
    }

    [Fact]
    public void ResetClearsTheLatchGenerationAndContentBaseline()
    {
        var state = Generating();
        Assert.True(Show(state, Start));
        state.Reset();

        Assert.Null(state.MessageId);
        Assert.False(Show(state, Start));
        state.ObserveProgress("answer", "tokenProgress", 1, Start);
        Assert.True(Show(state, Start));
    }

    private static NativeThinkingIndicatorState Generating()
    {
        var state = new NativeThinkingIndicatorState();
        state.ObserveProgress("answer", "tokenProgress", 10, Start);
        state.ObserveContent("answer", "", Start);
        return state;
    }

    private static bool Show(NativeThinkingIndicatorState state, DateTimeOffset now) =>
        state.ShouldShow("answer", active: true, blocked: false, visible: true, now);
}

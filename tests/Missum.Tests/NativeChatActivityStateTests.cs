using Missum.App.Services;
namespace Missum.Tests;
public sealed class NativeChatActivityStateTests
{
    [Theory]
    [InlineData("reasoningStarted")]
    [InlineData("toolCallGeneration")]
    [InlineData("codingCompacting")]
    public void PhaseOnlyWorkRemainsVisibleAfterTheLastVisibleText(string phase)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new NativeChatActivityState();
        state.ObserveOutput("Ich aktualisiere die Etappen.", "continue", true);
        Assert.True(state.ObservePhase(phase, now.AddSeconds(2)));
        state.ObserveOutput("Ich aktualisiere die Etappen.", "continue", true);
        Assert.True(state.Waiting);
        state.ObservePhase("tokenProgress", now.AddSeconds(3));
        Assert.True(state.Waiting);
        state.ObserveOutput("Ich aktualisiere die Etappen. Erledigt.", "continue", true);
        Assert.False(state.Waiting);
        Assert.False(state.ObservePhase(phase, now.AddSeconds(2)));
        Assert.False(state.Waiting);
    }

    [Fact]
    public void ContextProcessingAfterExistingAnswerIsVisibleUntilNewOutput()
    {
        var state = new NativeChatActivityState();
        state.ObserveOutput("Existing answer", "resume", true);
        Assert.True(state.ObservePhase("promptProcessing", DateTimeOffset.UtcNow));
        state.ObserveOutput("Existing answer", "resume", true);
        Assert.True(state.Waiting);
        state.ObservePhase("tokenProgress", DateTimeOffset.UtcNow);
        Assert.True(state.Waiting);
        state.ObserveOutput("Existing answer More", "resume", true);
        Assert.False(state.Waiting);
        state.ObservePhase("tokenProgress", DateTimeOffset.UtcNow);
        Assert.False(state.Waiting);
    }
    [Fact]
    public void StopAndNewNarrationConsumeModelWork()
    {
        var state = new NativeChatActivityState();
        state.ObserveOutput("", "", true);
        state.ObservePhase("codingLoading", DateTimeOffset.UtcNow);
        Assert.True(state.Waiting);
        state.ObserveOutput("", "narration", true);
        Assert.False(state.Waiting);
        state.ObservePhase("promptProcessing", DateTimeOffset.UtcNow);
        Assert.True(state.Waiting);
        state.ObserveOutput("", "narration", false);
        Assert.False(state.Waiting);
    }
}

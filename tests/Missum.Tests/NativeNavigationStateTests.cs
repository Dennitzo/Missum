using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeNavigationStateTests
{
    [Fact]
    public void SnapshotStartedBeforeNavigationCannotRestorePreviousSession()
    {
        var state = new NativeNavigationState();
        var previousSession = Guid.NewGuid();
        var nextSession = Guid.NewGuid();
        var oldRefresh = state.Generation;
        var navigation = state.BeginNavigation();

        Assert.False(state.CanApplySnapshot(oldRefresh, previousSession, nextSession));
        Assert.True(state.CanApplySnapshot(navigation, nextSession, nextSession));
        state.CompleteNavigation(navigation);
        Assert.False(state.CanApplySnapshot(oldRefresh, previousSession, nextSession));
    }

    [Fact]
    public void ReopeningSameSessionStillRejectsSnapshotsFromEarlierVisit()
    {
        var state = new NativeNavigationState();
        var session = Guid.NewGuid();
        var firstVisit = state.BeginNavigation();
        state.CompleteNavigation(firstVisit);
        var secondVisit = state.BeginNavigation();
        state.CompleteNavigation(secondVisit);

        Assert.True(secondVisit > firstVisit);
        Assert.False(state.CanApplySnapshot(firstVisit, session, session));
        Assert.True(state.CanApplySnapshot(secondVisit, session, session));
    }

    [Fact]
    public void FinishingSupersededNavigationDoesNotClearNewerPendingNavigation()
    {
        var state = new NativeNavigationState();
        var first = state.BeginNavigation();
        Assert.False(state.CanEditComposer);
        var second = state.BeginNavigation();
        state.CompleteNavigation(first);
        Assert.True(state.IsNavigating);
        Assert.False(state.CanEditComposer);
        Assert.False(state.IsCurrent(first));
        state.CompleteNavigation(second);
        Assert.False(state.IsNavigating);
        Assert.True(state.CanEditComposer);
    }

    [Fact]
    public void FailedNavigationReleasesComposerAndARepeatedOldFinallyCannotUnlockANewerNavigation()
    {
        var state = new NativeNavigationState();
        Assert.True(state.CanEditComposer);
        var failed = state.BeginNavigation();
        try { Assert.False(state.CanEditComposer); }
        finally { state.CompleteNavigation(failed); }
        Assert.True(state.CanEditComposer);
        var retry = state.BeginNavigation();
        state.CompleteNavigation(failed);
        Assert.False(state.CanEditComposer);
        state.CompleteNavigation(retry);
        Assert.True(state.CanEditComposer);
    }

    [Fact]
    public void SnapshotCapturedDuringNavigationMustMatchCoordinatorActiveSession()
    {
        var state = new NativeNavigationState();
        var generation = state.BeginNavigation();
        var oldSession = Guid.NewGuid();
        var activeSession = Guid.NewGuid();
        Assert.False(state.CanApplySnapshot(generation, oldSession, activeSession));
        Assert.True(state.CanApplySnapshot(generation, activeSession, activeSession));
        Assert.False(state.CanApplySnapshot(generation, Guid.Empty, activeSession));
    }

    [Theory]
    [InlineData("session.open")]
    [InlineData("session.create")]
    [InlineData("session.projectCreate")]
    [InlineData("mode.switch")]
    [InlineData("session.delete")]
    [InlineData("session.clear")]
    public void EverySessionNavigationAdvancesTheUiGeneration(string command) =>
        Assert.True(NativeNavigationState.IsNavigationCommand(command));

    [Theory]
    [InlineData("chat.send")]
    [InlineData("research.list")]
    [InlineData("research.open")]
    [InlineData("research.export")]
    [InlineData("reasoning.set")]
    [InlineData("action.invoke")]
    [InlineData("memory.list")]
    [InlineData("memory.create")]
    public void BackgroundCommandsDoNotActAsNavigation(string command) =>
        Assert.False(NativeNavigationState.IsNavigationCommand(command));
}

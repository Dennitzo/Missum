using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativeChangeReceiptStateTests
{
    [Fact]
    public void NewPromptImmediatelyRejectsPreviousRunsReceiptUntilItsOwnAssistantTurnExists()
    {
        var state = new NativeChangeReceiptState();
        var session = Guid.NewGuid(); var previous = Guid.NewGuid(); var current = Guid.NewGuid();
        var oldRun = Guid.NewGuid(); var currentRun = Guid.NewGuid();
        Assert.True(state.ObserveConversation(session, previous, 10));
        Assert.True(state.AcceptReceipt(session, previous, oldRun, 100));
        state.BeginPrompt(session);
        Assert.False(state.AcceptReceipt(session, previous, oldRun, 200));
        // Reopening a queued session is allowed; its previous receipt remains blocked.
        Assert.True(state.ObserveConversation(session, previous, 10));
        Assert.True(state.IsPromptPending(session));
        Assert.False(state.AcceptReceipt(session, previous, oldRun, 201));
        Assert.True(state.ObserveStarted(session, current, 12));
        Assert.False(state.IsPromptPending(session));
        Assert.True(state.AcceptReceipt(session, current, currentRun, 1));
        Assert.False(state.AcceptReceipt(session, previous, oldRun, 300));
    }

    [Fact]
    public void DelayedSnapshotOrStartedEventCannotRestoreAnOlderTurnEvenAcrossVisits()
    {
        var state = new NativeChangeReceiptState();
        var session = Guid.NewGuid(); var oldMessage = Guid.NewGuid(); var newMessage = Guid.NewGuid();
        state.ObserveConversation(session, oldMessage, 20);
        state.BeginPrompt(session);
        Assert.True(state.ObserveStarted(session, newMessage, 22));
        Assert.False(state.ObserveConversation(session, oldMessage, 20));
        Assert.False(state.ObserveStarted(session, oldMessage, 21));
        Assert.True(state.ObserveConversation(Guid.NewGuid(), Guid.NewGuid(), 1));
        Assert.True(state.ObserveConversation(session, newMessage, 23));
        Assert.True(state.AcceptReceipt(session, newMessage, Guid.NewGuid(), 5));
        Assert.False(state.AcceptReceipt(session, oldMessage, Guid.NewGuid(), 999));
    }

    [Fact]
    public void ReceiptsCannotCrossSessionsMessagesOrRunsAndCannotRegressWithinARun()
    {
        var state = new NativeChangeReceiptState();
        var firstSession = Guid.NewGuid(); var secondSession = Guid.NewGuid();
        var firstMessage = Guid.NewGuid(); var secondMessage = Guid.NewGuid(); var run = Guid.NewGuid();
        state.ObserveConversation(firstSession, firstMessage, 5);
        state.ObserveConversation(secondSession, secondMessage, 8);
        Assert.False(state.AcceptReceipt(secondSession, firstMessage, run, 5));
        Assert.True(state.AcceptReceipt(firstSession, firstMessage, run, 5));
        Assert.False(state.AcceptReceipt(firstSession, firstMessage, Guid.NewGuid(), 6));
        Assert.False(state.AcceptReceipt(firstSession, firstMessage, run, 4));
        Assert.True(state.AcceptReceipt(firstSession, firstMessage, run, 6));
        Assert.False(state.AcceptReceipt(firstSession, Guid.Empty, run, 7));
    }

    [Fact]
    public void FailedPromptRestoresPreviousReceiptButCannotUnlockANewerPendingPrompt()
    {
        var state = new NativeChangeReceiptState();
        var session = Guid.NewGuid(); var message = Guid.NewGuid(); var run = Guid.NewGuid();
        state.ObserveConversation(session, message, 4);
        state.AcceptReceipt(session, message, run, 1);
        var failed = state.BeginPrompt(session);
        state.CancelPrompt(session, failed);
        Assert.True(state.AcceptReceipt(session, message, run, 1));
        state.BeginPrompt(session);
        state.CancelPrompt(session, failed);
        Assert.True(state.IsPromptPending(session));
        Assert.False(state.AcceptReceipt(session, message, run, 2));
    }

    [Fact]
    public void ConversationSnapshotCanBindNewTurnBeforeStartedEventAndHistoryRestoreWorks()
    {
        var state = new NativeChangeReceiptState();
        var session = Guid.NewGuid(); var message = Guid.NewGuid(); var next = Guid.NewGuid();
        state.ObserveConversation(session, message, 8);
        state.BeginPrompt(session);
        Assert.True(state.ObserveConversation(session, next, 10));
        Assert.False(state.IsPromptPending(session));
        var run = Guid.NewGuid();
        Assert.True(state.AcceptReceipt(session, next, run, 1));
        Assert.True(state.ObserveStarted(session, next, 10));
        Assert.True(state.AcceptReceipt(session, next, run, 2));
        Assert.True(state.ObserveConversation(session, next, 15));
        Assert.True(state.AcceptReceipt(session, next, run, 2));
    }
}

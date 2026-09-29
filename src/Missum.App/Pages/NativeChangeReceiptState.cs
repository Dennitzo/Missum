namespace Missum.App.Pages;

/// <summary>Associates the composer receipt with one assistant turn, independent of historical review tabs.</summary>
internal sealed class NativeChangeReceiptState
{
    private readonly Dictionary<Guid, SessionState> _sessions = [];

    public long BeginPrompt(Guid sessionId)
    {
        var state = Get(sessionId);
        state.Pending = true;
        return ++state.Epoch;
    }

    public void CancelPrompt(Guid sessionId, long epoch)
    {
        var state = Get(sessionId);
        if (state.Epoch == epoch) state.Pending = false;
    }

    public bool IsPromptPending(Guid sessionId) => Get(sessionId).Pending;

    public bool ObserveConversation(Guid sessionId, Guid latestAssistantMessageId, long revision)
    {
        var state = Get(sessionId);
        if (revision < state.ConversationRevision) return false;
        // A refresh started before Send must not restore the previous turn while a
        // new prompt is queued/preparing and has no assistant message yet.
        if (state.Pending && latestAssistantMessageId == state.MessageId)
        {
            state.ConversationRevision = revision;
            return true;
        }
        SelectMessage(state, latestAssistantMessageId, revision);
        return true;
    }

    public bool ObserveStarted(Guid sessionId, Guid messageId, long revision)
    {
        var state = Get(sessionId);
        if (messageId == Guid.Empty || revision < state.ConversationRevision) return false;
        SelectMessage(state, messageId, revision);
        return true;
    }

    public bool AcceptReceipt(Guid sessionId, Guid messageId, Guid runId, long revision)
    {
        var state = Get(sessionId);
        if (state.Pending || messageId == Guid.Empty || runId == Guid.Empty || messageId != state.MessageId
            || (state.RunId != Guid.Empty && state.RunId != runId) || revision < state.ReceiptRevision) return false;
        state.RunId = runId;
        state.ReceiptRevision = revision;
        return true;
    }

    private SessionState Get(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var state)) _sessions[sessionId] = state = new SessionState();
        return state;
    }

    private static void SelectMessage(SessionState state, Guid messageId, long revision)
    {
        state.Pending = false;
        state.ConversationRevision = revision;
        if (state.MessageId == messageId) return;
        state.MessageId = messageId;
        state.RunId = Guid.Empty;
        state.ReceiptRevision = 0;
    }

    private sealed class SessionState
    {
        public Guid MessageId;
        public Guid RunId;
        public long ConversationRevision;
        public long ReceiptRevision;
        public long Epoch;
        public bool Pending;
    }
}

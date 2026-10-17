using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private readonly object _displayNormalizationLock = new();
    private readonly HashSet<Guid> _displayNormalizationRunning = [];
    private readonly HashSet<Guid> _displayNormalized = [];
    private Guid _displayNormalizationSession;
    private Guid[] _displayNormalizationCandidates = [];
    private int _displayNormalizationNext;
    private long _displayNormalizationEpoch;
    private CancellationTokenSource? _displayNormalizationCancellation;

    private async Task<IReadOnlyList<ChatMessage>> PrepareDisplayedPageAsync(ConversationPage page, CancellationToken token)
    {
        var auxiliary = await Task.Run(() => conversationSnapshots.GetAuxiliaryMessagesAsync(page.Conversation.Session.Id, token), token).ConfigureAwait(false);
        QueueDisplayedNormalization(page, token);
        return auxiliary;
    }

    private void QueueDisplayedNormalization(ConversationPage page, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        lock (_displayNormalizationLock)
        {
            var sessionId = page.Conversation.Session.Id;
            if (_displayNormalizationSession != sessionId || _displayNormalizationCancellation is null
                || _displayNormalizationCancellation.IsCancellationRequested)
            {
                _displayNormalizationCancellation?.Cancel();
                _displayNormalizationCancellation?.Dispose();
                _displayNormalizationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                _displayNormalizationSession = sessionId;
                _displayNormalizationEpoch++;
                _displayNormalized.Clear();
            }
            // Candidate IDs come only from this displayed page. No hidden conversations or startup-wide transcript sweep.
            _displayNormalizationCandidates = page.Conversation.Messages.Reverse()
                .Where(message => page.ToolStepWindows?.GetValueOrDefault(message.Id)?.RequiresLegacyNormalization == true)
                .Select(message => message.Id).Take(100).ToArray();
            _displayNormalizationNext = 0;
            StartDisplayedNormalizationsLocked();
        }
    }

    private void StartDisplayedNormalizationsLocked()
    {
        if (_displayNormalizationCancellation is null || _displayNormalizationCancellation.IsCancellationRequested) return;
        while (_displayNormalizationRunning.Count < 2 && _displayNormalizationNext < _displayNormalizationCandidates.Length)
        {
            var id = _displayNormalizationCandidates[_displayNormalizationNext++];
            if (_displayNormalized.Contains(id) || !_displayNormalizationRunning.Add(id)) continue;
            var epoch = _displayNormalizationEpoch;
            var token = _displayNormalizationCancellation.Token;
            _ = Task.Run(() => NormalizeDisplayedMessageAsync(id, epoch, token));
        }
    }

    private async Task NormalizeDisplayedMessageAsync(Guid messageId, long epoch, CancellationToken token)
    {
        var completed = false;
        try
        {
            // Let the first frame and interaction finish before importing a potentially large legacy message.
            await Task.Delay(500, token).ConfigureAwait(false);
            await chats.NormalizeToolStepsAsync(messageId, token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Die optionale Werkzeugindexierung konnte nicht abgeschlossen werden: {0}", exception.Message);
        }
        finally
        {
            lock (_displayNormalizationLock)
            {
                _displayNormalizationRunning.Remove(messageId);
                if (completed && epoch == _displayNormalizationEpoch)
                {
                    if (_displayNormalized.Count >= 1024) _displayNormalized.Clear();
                    _displayNormalized.Add(messageId);
                }
                StartDisplayedNormalizationsLocked();
            }
        }
    }
}

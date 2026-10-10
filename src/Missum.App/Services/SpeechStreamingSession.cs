using System.Threading.Channels;
using Missum.Core.Models;

namespace Missum.App.Services;

internal sealed class SpeechStreamingSession : IDisposable
{
    private readonly object _gate = new();
    private readonly SpeechStreamingTextBuffer _buffer;
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _token;
    private bool _finished;
    private readonly HashSet<string> _spokenNarrations = new(StringComparer.Ordinal);

    public SpeechStreamingSession(ChatMessage initialMessage, Func<string, CancellationToken, Task> play,
        CancellationToken cancellationToken = default)
        : this(initialMessage, play, null, null, cancellationToken) { }

    public SpeechStreamingSession(ChatMessage initialMessage, Func<string, CancellationToken, Task> play,
        Func<CancellationToken, Task>? waitForResume, IDisposable? playbackLifetime = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(initialMessage);
        ArgumentNullException.ThrowIfNull(play);
        MessageId = initialMessage.Id;
        SessionId = initialMessage.SessionId;
        _buffer = new(initialMessage.Content);
        foreach (var step in initialMessage.ToolSteps ?? [])
            if (step.Tool == "assistant.narration") _spokenNarrations.Add(step.Id);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _token = _cancellation.Token;
        Completion = PlayQueuedAsync(play, waitForResume, playbackLifetime);
    }

    public Guid MessageId { get; }
    public Guid PlaybackId { get; } = Guid.NewGuid();
    public Guid SessionId { get; }
    public Task Completion { get; }
    public Exception? Failure { get; private set; }
    public bool HasPlayed { get; private set; }
    public bool WasCancelled => _token.IsCancellationRequested;
    public bool WaitForActionBoundary { get; init; }

    public void Observe(MissumAiAssistantUpdate update)
    {
        if (update.Message.Id != MessageId || update.Message.SessionId != SessionId) return;
        if (update.Kind is MissumAiAssistantUpdateKind.Cancelled or MissumAiAssistantUpdateKind.Failed)
        {
            Cancel();
            return;
        }
        var flushSentence = update.Kind == MissumAiAssistantUpdateKind.Status
            && update.ToolStep is { AgentId: null, Tool: not MissumAiAssistantService.ReasoningStepTool };
        if (update.Kind is not (MissumAiAssistantUpdateKind.Delta or MissumAiAssistantUpdateKind.Completed) && !flushSentence) return;
        // A sentence boundary is not a completed narration block. The next
        // main-agent tool status (or run completion) seals the preceding text.
        if (WaitForActionBoundary && update.Kind == MissumAiAssistantUpdateKind.Delta) return;
        lock (_gate)
        {
            if (_finished || _token.IsCancellationRequested) return;
            foreach (var step in update.Message.ToolSteps ?? [])
            {
                if (step.Tool != "assistant.narration" || step.Status != "completed" || !_spokenNarrations.Add(step.Id)) continue;
                foreach (var chunk in new SpeechStreamingTextBuffer().Take(step.Detail ?? "", complete: true)) _queue.Writer.TryWrite(chunk);
            }
            var complete = update.Kind == MissumAiAssistantUpdateKind.Completed;
            var chunks = _buffer.Take(update.Message.Content, complete, flushSentence,
                flushBlock: WaitForActionBoundary && flushSentence);
            if (WaitForActionBoundary)
            {
                var block = string.Join(" ", chunks.Select(static text => text.Trim()));
                if (!string.IsNullOrWhiteSpace(block)) _queue.Writer.TryWrite(block);
            }
            else foreach (var text in chunks) _queue.Writer.TryWrite(text);
            if (_buffer.HasConflictingRevision)
            {
                Cancel();
                return;
            }
            if (complete)
            {
                _finished = true;
                _queue.Writer.TryComplete();
            }
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _finished = true;
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            _queue.Writer.TryComplete();
        }
    }

    private async Task PlayQueuedAsync(Func<string, CancellationToken, Task> play,
        Func<CancellationToken, Task>? waitForResume, IDisposable? playbackLifetime)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_token).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var text))
                {
                    _token.ThrowIfCancellationRequested();
                    if (waitForResume is not null) await waitForResume(_token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                    HasPlayed = true;
                    await play(text, _token).ConfigureAwait(false);
                    _token.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Failure = exception;
            Cancel();
        }
        finally
        {
            playbackLifetime?.Dispose();
            _cancellation.Dispose();
        }
    }

    public void Dispose() => Cancel();
}

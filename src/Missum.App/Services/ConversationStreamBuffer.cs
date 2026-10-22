using System.Collections;
using System.Diagnostics;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.App.Services;

/// <summary>Bounded write-behind of one stream; its durable cursor never outruns its content.</summary>
internal sealed class ConversationStreamBuffer(IChatRepository chats, IMissumAiRunRepository runs,
    ChatMessage message, MissumAiRunRecord run) : IAsyncDisposable
{
    internal sealed class Steps : IReadOnlyList<AssistantToolStep>
    {
        private readonly List<AssistantToolStep> _items = [];
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        internal Steps(IEnumerable<AssistantToolStep> items) { foreach (var item in items) Merge(item); }
        internal void Reset(IEnumerable<AssistantToolStep> items)
        {
            if (ReferenceEquals(items, this)) return;
            _items.Clear(); _index.Clear();
            foreach (var item in items) Merge(item);
        }
        internal AssistantToolStep? Find(string id) => _index.TryGetValue(id, out var index) ? _items[index] : null;
        internal int? Ordinal(string id) => _index.TryGetValue(id, out var index) ? index : null;
        internal AssistantToolStep Merge(AssistantToolStep incoming)
        {
            if (_index.TryGetValue(incoming.Id, out var index))
                return _items[index] = AssistantToolStep.Merge(_items[index], incoming);
            _index[incoming.Id] = _items.Count;
            _items.Add(incoming);
            return incoming;
        }
        public int Count => _items.Count;
        public AssistantToolStep this[int index] => _items[index];
        public IEnumerator<AssistantToolStep> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal Steps ToolSteps { get; } = new(message.ToolSteps ?? []);
    private readonly Dictionary<string, AssistantToolStep> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _timerTask;
    private ConversationStreamBatch? _ready;
    private string _content = message.Content, _serverRunId = run.ServerRunId ?? "", _state = run.State;
    private MessageStatus _status = message.Status;
    private string? _model = run.SelectedModel;
    private DateTimeOffset _sourceUpdatedAt = message.UpdatedAt;
    private long _eventId = run.LastEventId, _lastFlush = Stopwatch.GetTimestamp();
    private volatile Exception? _backgroundFailure;

    internal void Start() => _timerTask ??= RunTimerAsync();

    private async Task RunTimerAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
                await FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error) { _backgroundFailure = error; }
    }

    internal Task<IReadOnlyList<AssistantToolStep>> UpsertAsync(AssistantToolStep step)
    {
        lock (_sync)
        {
            var merged = ToolSteps.Merge(step);
            _pending[step.Id] = merged;
            _sourceUpdatedAt = DateTimeOffset.UtcNow;
        }
        return Task.FromResult<IReadOnlyList<AssistantToolStep>>(ToolSteps);
    }

    internal void SetContent(string content, MessageStatus status, IReadOnlyList<AssistantToolStep> steps)
    {
        lock (_sync)
        {
            _content = content; _status = status;
            _sourceUpdatedAt = DateTimeOffset.UtcNow;
            if (!ReferenceEquals(steps, ToolSteps))
                foreach (var step in steps)
                    if (ToolSteps.Find(step.Id) != step) { ToolSteps.Merge(step); _pending[step.Id] = step; }
        }
    }

    internal async Task AcknowledgeAsync(string serverRunId, long eventId, string state, string? model, bool force = false)
    {
        if (_backgroundFailure is { } failure) throw new IOException("Der Stream konnte nicht gespeichert werden.", failure);
        lock (_sync)
        {
            _serverRunId = serverRunId; _eventId = eventId; _state = state; _model = model;
            _ready = new(message.Id, _content, _status, _pending.Values.ToArray(), run.Id,
                _serverRunId, _eventId, _state, _model, SourceUpdatedAt: _sourceUpdatedAt);
        }
        if (force || Stopwatch.GetElapsedTime(_lastFlush) >= TimeSpan.FromMilliseconds(250))
            await FlushAsync().ConfigureAwait(false);
    }

    internal Task CheckpointAppliedCallbackAsync(bool force = false)
    {
        // The event pump invokes callbacks only between fully applied SSE events.
        // Tool progress can therefore be saved with the last acknowledged cursor.
        lock (_sync)
            return AcknowledgeAsync(_serverRunId, _eventId, _state, _model, force);
    }

    internal async Task ResetToDurableAsync(ChatMessage persistedMessage, MissumAiRunRecord persistedRun)
    {
        if (persistedMessage.Id != message.Id || persistedRun.Id != run.Id
            || persistedRun.AssistantMessageId != message.Id || persistedRun.SessionId != message.SessionId)
            throw new InvalidOperationException("Der Abbruch-Checkpoint gehört nicht zu diesem AI-Lauf.");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                // Called only after flushing acknowledged events and stopping the
                // event consumer. A partially applied event must never be saved
                // with the preceding cursor, including through final step updates.
                _ready = null; _pending.Clear();
                ToolSteps.Reset(persistedMessage.ToolSteps ?? []);
                _content = persistedMessage.Content; _status = persistedMessage.Status;
                _sourceUpdatedAt = persistedMessage.UpdatedAt;
                _serverRunId = persistedRun.ServerRunId ?? ""; _eventId = persistedRun.LastEventId;
                _state = persistedRun.State; _model = persistedRun.SelectedModel;
                _lastFlush = Stopwatch.GetTimestamp();
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ConversationStreamBatch? batch;
            lock (_sync) batch = _ready;
            if (batch is null) return;
            if (chats is IStreamingConversationRepository streaming)
                await streaming.CommitStreamAsync(batch, CancellationToken.None).ConfigureAwait(false);
            else
            {
                foreach (var step in batch.ToolSteps)
                    await chats.SaveToolStepDeltaAsync(message.Id, step, CancellationToken.None).ConfigureAwait(false);
                await chats.UpdateMessageAsync(message.Id, batch.Content, batch.Status, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await runs.UpdateAsync(run.Id, batch.ServerRunId, batch.LastEventId, batch.RunState, batch.SelectedModel, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            lock (_sync)
            {
                foreach (var step in batch.ToolSteps)
                    if (_pending.GetValueOrDefault(step.Id) == step) _pending.Remove(step.Id);
                if (ReferenceEquals(_ready, batch)) _ready = null;
                _lastFlush = Stopwatch.GetTimestamp();
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            if (_timerTask is not null) await _timerTask.ConfigureAwait(false);
            await FlushAsync().ConfigureAwait(false);
        }
        finally { _gate.Dispose(); _shutdown.Dispose(); }
    }
}

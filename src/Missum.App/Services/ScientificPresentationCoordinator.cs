using System.Collections.Concurrent;

namespace Missum.App.Services;

public sealed record ScientificPresentationSnapshot(long Version, ScientificPublicationArtifact? Publication, string? PublicationError);

internal sealed class ScientificPresentationPublishedEventArgs(string projectId, ScientificPresentationSnapshot snapshot,
    DateTimeOffset publishedAt) : EventArgs
{
    internal string ProjectId { get; } = projectId;
    internal ScientificPresentationSnapshot Snapshot { get; } = snapshot;
    internal DateTimeOffset PublishedAt { get; } = publishedAt;
}

/// <summary>Coalesces rendering of directly edited publications independently of the selected tab.</summary>
public sealed class ScientificPresentationCoordinator(ScientificPublicationService publications) : IDisposable
{
    internal event EventHandler<ScientificPresentationPublishedEventArgs>? SnapshotPublished;
    private readonly ConcurrentDictionary<string, Pending> _projects = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public ScientificPresentationSnapshot? GetSnapshot(string projectId) =>
        _projects.TryGetValue(projectId, out var state) ? Volatile.Read(ref state.Snapshot) : null;

    public void RegisterWorkspace(string projectId, string workspace, string? title = null) =>
        publications.RegisterWorkspace(projectId, workspace, title);

    public void Queue(string projectId)
    {
        if (_shutdown.IsCancellationRequested || string.IsNullOrWhiteSpace(projectId)) return;
        var state = _projects.GetOrAdd(projectId, _ => new());
        lock (state)
        {
            state.Dirty = true;
            if (state.Working) return;
            state.Working = true;
            _ = Task.Run(() => RefreshLoopAsync(projectId, state));
        }
    }

    public async Task WaitForIdleAsync(string projectId, CancellationToken cancellationToken = default)
    {
        while (_projects.TryGetValue(projectId, out var state))
        {
            lock (state) { if (!state.Working) return; }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshLoopAsync(string projectId, Pending state)
    {
        var released = false;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                lock (state) state.Dirty = false;
                var previous = Volatile.Read(ref state.Snapshot);
                var publication = previous.Publication;
                string? error = null;
                try
                {
                    publication = await publications.EnsureCurrentAsync(projectId, _shutdown.Token).ConfigureAwait(false) ?? publication;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !_shutdown.IsCancellationRequested)
                {
                    error = exception.Message;
                    if (publication is null)
                    {
                        try { publication = await publications.RestoreLastPublicationAsync(projectId, _shutdown.Token).ConfigureAwait(false); }
                        catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                    }
                }
                if (publication != previous.Publication || error != previous.PublicationError)
                {
                    var snapshot = new ScientificPresentationSnapshot(previous.Version + 1, publication, error);
                    Volatile.Write(ref state.Snapshot, snapshot);
                    try { SnapshotPublished?.Invoke(this, new(projectId, snapshot, DateTimeOffset.UtcNow)); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { } // Passive UI observers cannot interrupt rendering.
                }
                lock (state)
                {
                    if (!state.Dirty) { state.Working = false; released = true; return; }
                }
                await Task.Delay(250, _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { if (!released) lock (state) state.Working = false; }
    }

    public void Dispose() => _shutdown.Cancel();

    private sealed class Pending
    {
        public bool Dirty, Working;
        public ScientificPresentationSnapshot Snapshot = new(0, null, null);
    }
}

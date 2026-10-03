using System.Collections.Concurrent;

namespace Missum.App.Services;

public sealed record ScientificPresentationSnapshot(long Version, ScientificPublicationArtifact? Publication,
    ScientificSimulationSnapshot? Simulation, string? PublicationError, string? SimulationError);

internal sealed class ScientificPresentationPublishedEventArgs(string projectId, ScientificPresentationSnapshot snapshot,
    DateTimeOffset publishedAt) : EventArgs
{
    internal string ProjectId { get; } = projectId;
    internal ScientificPresentationSnapshot Snapshot { get; } = snapshot;
    internal DateTimeOffset PublishedAt { get; } = publishedAt;
}

/// <summary>Coalesces durable research updates independently of the selected tab or session.</summary>
public sealed class ScientificPresentationCoordinator(ScientificPublicationService publications, ScientificSimulationService simulations) : IDisposable
{
    internal event EventHandler<ScientificPresentationPublishedEventArgs>? SnapshotPublished;
    private readonly ConcurrentDictionary<string, Pending> _projects = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = static (duration, token) => Task.Delay(duration, token);

    internal ScientificPresentationCoordinator(ScientificPublicationService publications, ScientificSimulationService simulations,
        Func<TimeSpan, CancellationToken, Task> delay) : this(publications, simulations)
    {
        ArgumentNullException.ThrowIfNull(delay);
        _delay = delay;
    }

    public ScientificPresentationSnapshot? GetSnapshot(string projectId) =>
        _projects.TryGetValue(projectId, out var state) ? Volatile.Read(ref state.Snapshot) : null;

    public void Queue(string projectId)
    {
        if (_shutdown.IsCancellationRequested || string.IsNullOrWhiteSpace(projectId)) return;
        var state = _projects.GetOrAdd(projectId, _ => new());
        lock (state)
        {
            state.Dirty = true;
            if (state.Working) return;
            // An explicit update after idle starts a new bounded recovery cycle.
            // Internal retries only mark Dirty and never replenish this budget.
            state.RenderFailures = 0;
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
                lock (state) { state.Dirty = false; }
                var previous = state.Snapshot;
                var publication = previous.Publication;
                var simulation = previous.Simulation;
                string? publicationError = null, simulationError = null;
                // Separate failures: a failed Python runtime must not suppress the mandatory PDF.
                try
                {
                    var current = await publications.EnsureCurrentAsync(projectId, _shutdown.Token).ConfigureAwait(false);
                    if (current is not null) publication = current;
                    else
                    {
                        var working = await publications.GetWorkingStateAsync(projectId, _shutdown.Token, canonicalOnly: true).ConfigureAwait(false);
                        if (working is not null && !ScientificPublicationService.HasCanonicalSections(working))
                        {
                            // A new research project remains empty until actual scientific text arrives.
                            // Do not manufacture filler or spin the renderer while the model is working.
                            publicationError = null;
                            if (publication is null)
                                publication = await publications.RestoreLastPublicationAsync(projectId, _shutdown.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            // Null means this render became stale, not that the old
                            // PDF is a current deliverable. Keep it for reading only.
                            publicationError = "Der Forschungsstand hat sich während der PDF-Erstellung geändert. Die aktuelle Publikation wird erneut erstellt; die sichtbare vorherige Fassung ist noch nicht aktuell.";
                            lock (state) { state.Dirty = true; }
                        }
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !_shutdown.IsCancellationRequested)
                {
                    publicationError = exception.Message;
                    if (publication is null)
                    {
                        try { publication = await publications.RestoreLastPublicationAsync(projectId, _shutdown.Token).ConfigureAwait(false); }
                        catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException or InvalidOperationException) { }
                    }
                    // Retry transient local renderer failures without asking the model to repeat text.
                    // Content errors require a targeted section correction, not an endless render loop.
                    if (exception is not ScientificPublicationContentException && state.RenderFailures < 2)
                    {
                        state.RenderFailures++;
                        await _delay(TimeSpan.FromSeconds(state.RenderFailures * 2), _shutdown.Token).ConfigureAwait(false);
                        lock (state) { state.Dirty = true; }
                    }
                }
                if (publicationError is null) state.RenderFailures = 0;
                Publish();
                try
                {
                    simulation = await simulations.RefreshAsync(projectId, publication, _shutdown.Token).ConfigureAwait(false);
                    if (simulation.Status == "updating")
                    {
                        if (publicationError is null) lock (state) { state.Dirty = true; }
                        else simulationError = "Die aktuelle Publikation konnte nicht erstellt werden; die darauf basierende Darstellung wartet auf eine erneute Aktualisierung.";
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !_shutdown.IsCancellationRequested) { simulationError = exception.Message; }
                Publish();
                lock (state)
                {
                    if (!state.Dirty) { state.Working = false; released = true; return; }
                }
                await _delay(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);

                void Publish()
                {
                    var old = Volatile.Read(ref state.Snapshot);
                    var sameSimulation = simulation?.Status == old.Simulation?.Status && simulation?.Detail == old.Simulation?.Detail
                        && simulation?.Revision == old.Simulation?.Revision
                        && (simulation?.Artifacts ?? []).SequenceEqual(old.Simulation?.Artifacts ?? []);
                    if (publication?.PdfPath == old.Publication?.PdfPath && sameSimulation
                        && publicationError == old.PublicationError && simulationError == old.SimulationError) return;
                    var published = new ScientificPresentationSnapshot(old.Version + 1, publication, simulation, publicationError, simulationError);
                    Volatile.Write(ref state.Snapshot, published);
                    try { SnapshotPublished?.Invoke(this, new(projectId, published, DateTimeOffset.UtcNow)); }
                    catch (Exception exception) when (exception is not OutOfMemoryException) { } // Passive observers cannot interrupt rendering.
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { if (!released) lock (state) { state.Working = false; } }
    }

    public void Dispose() => _shutdown.Cancel();
    private sealed class Pending
    {
        public bool Dirty, Working;
        public int RenderFailures;
        public ScientificPresentationSnapshot Snapshot = new(0, null, null, null, null);
    }
}

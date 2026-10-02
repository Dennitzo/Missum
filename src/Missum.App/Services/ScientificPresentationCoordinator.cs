using System.Collections.Concurrent;

namespace Missum.App.Services;

public sealed record ScientificPresentationSnapshot(long Version, ScientificPublicationArtifact? Publication,
    ScientificSimulationSnapshot? Simulation, string? PublicationError, string? SimulationError);

/// <summary>Coalesces durable research updates independently of the selected tab or session.</summary>
public sealed class ScientificPresentationCoordinator(ScientificPublicationService publications, ScientificSimulationService simulations) : IDisposable
{
    private readonly ConcurrentDictionary<string, Pending> _projects = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

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
                        // Null means this render became stale, not that the old
                        // PDF is a current deliverable. Keep it for reading only.
                        publicationError = "Der Forschungsstand hat sich während der PDF-Erstellung geändert. Die aktuelle Publikation wird erneut erstellt; die sichtbare vorherige Fassung ist noch nicht aktuell.";
                        lock (state) { state.Dirty = true; }
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !_shutdown.IsCancellationRequested) { publicationError = exception.Message; }
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
                await Task.Delay(1000, _shutdown.Token).ConfigureAwait(false);

                void Publish()
                {
                    var old = Volatile.Read(ref state.Snapshot);
                    var sameSimulation = simulation?.Status == old.Simulation?.Status && simulation?.Detail == old.Simulation?.Detail
                        && simulation?.Revision == old.Simulation?.Revision
                        && (simulation?.Artifacts ?? []).SequenceEqual(old.Simulation?.Artifacts ?? []);
                    if (publication?.PdfPath == old.Publication?.PdfPath && sameSimulation
                        && publicationError == old.PublicationError && simulationError == old.SimulationError) return;
                    Volatile.Write(ref state.Snapshot, new(old.Version + 1, publication, simulation, publicationError, simulationError));
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
        public ScientificPresentationSnapshot Snapshot = new(0, null, null, null, null);
    }
}

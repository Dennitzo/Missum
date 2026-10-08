using System.Collections.Concurrent;
using System.Text.Json;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed record ScientificPresentationSnapshot(long Version, ScientificPublicationArtifact? Publication,
    ScientificSimulationSnapshot? Simulation, string? PublicationError, string? SimulationError)
{
    public ScientificPresentationFeedback? Feedback { get; init; }
}

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

    public void Queue(string projectId) => QueueCore(projectId);

    private long QueueCore(string projectId)
    {
        if (_shutdown.IsCancellationRequested || string.IsNullOrWhiteSpace(projectId)) return 0;
        var state = _projects.GetOrAdd(projectId, _ => new());
        lock (state)
        {
            state.Generation++;
            state.Dirty = true;
            if (state.Working) return state.Generation;
            // An explicit update after idle starts a new bounded recovery cycle.
            // Internal retries only mark Dirty and never replenish this budget.
            state.RenderFailures = 0;
            state.Working = true;
            _ = Task.Run(() => RefreshLoopAsync(projectId, state));
            return state.Generation;
        }
    }

    internal async Task<JsonElement> ObserveFeedbackAsync(ResearchWorkingState working, bool refresh,
        TimeSpan maximumWait, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = _projects.GetOrAdd(working.ProjectId, _ => new());
        long generation;
        lock (state) generation = state.Generation;
        var previous = Volatile.Read(ref state.Snapshot).Feedback;
        if (refresh || generation == 0 || previous is not null && previous.PublicationRevision != working.PublicationRevision)
            generation = QueueCore(working.ProjectId);
        var started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool active;
            long latestGeneration;
            lock (state) { active = state.Working; latestGeneration = state.Generation; }
            var feedback = Volatile.Read(ref state.Snapshot).Feedback;
            var belongsToLatestRequest = feedback?.Generation == latestGeneration;
            var complete = belongsToLatestRequest && feedback?.PublicationRevision == working.PublicationRevision
                && (feedback.PublicationIssue is not null || feedback.SimulationIssue is not null || !active);
            var remaining = maximumWait.TotalMilliseconds - (Environment.TickCount64 - started);
            if (complete || remaining <= 0 || generation == 0 || !active)
                return ScientificPresentationFeedbackProjection.Create(working.ProjectId, working.Revision,
                    working.PublicationRevision, belongsToLatestRequest ? feedback : null, active);
            // The timeout limits this receipt only. It never cancels an independent renderer or starts a new AI run.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, 100)), cancellationToken).ConfigureAwait(false);
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
                long generation;
                lock (state) { state.Dirty = false; generation = state.Generation; }
                var attemptedState = await publications.GetWorkingStateAsync(projectId, _shutdown.Token, canonicalOnly: true).ConfigureAwait(false);
                var attemptedRevision = attemptedState?.PublicationRevision ?? state.Snapshot.Publication?.Revision ?? 0;
                var previous = state.Snapshot;
                var publication = previous.Publication;
                var simulation = previous.Simulation;
                string? publicationError = null, simulationError = null;
                ScientificPresentationIssue? publicationIssue = null;
                var publicationStatus = "pending";
                var simulationStatus = "pending";
                // Separate failures: a failed Python runtime must not suppress the mandatory PDF.
                try
                {
                    var current = await publications.EnsureCurrentAsync(projectId, _shutdown.Token).ConfigureAwait(false);
                    if (current is not null)
                    {
                        publication = current;
                        attemptedRevision = current.Revision;
                        publicationStatus = "ready";
                    }
                    else
                    {
                        var working = await publications.GetWorkingStateAsync(projectId, _shutdown.Token, canonicalOnly: true).ConfigureAwait(false);
                        if (working is not null && !ScientificPublicationService.HasCanonicalSections(working))
                        {
                            // A new research project remains empty until actual scientific text arrives.
                            // Do not manufacture filler or spin the renderer while the model is working.
                            publicationError = null;
                            publicationStatus = "empty";
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
                    if (exception is ScientificPublicationContentException { PublicationRevision: { } failedRevision })
                        attemptedRevision = failedRevision;
                    publicationIssue = ScientificPresentationFeedbackProjection.PublicationIssue(exception);
                    publicationStatus = "failed";
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
                var currentState = await publications.GetWorkingStateAsync(projectId, _shutdown.Token, canonicalOnly: true).ConfigureAwait(false);
                var superseded = currentState is not null && currentState.PublicationRevision != attemptedRevision;
                if (superseded) lock (state) { state.Dirty = true; }
                Publish();
                try
                {
                    simulation = await simulations.RefreshAsync(projectId, publication, _shutdown.Token).ConfigureAwait(false);
                    simulationStatus = simulation.Status;
                    if (simulation.Status == "updating")
                    {
                        if (publicationError is null) lock (state) { state.Dirty = true; }
                        else simulationError = "Die aktuelle Publikation konnte nicht erstellt werden; die darauf basierende Darstellung wartet auf eine erneute Aktualisierung.";
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException && !_shutdown.IsCancellationRequested)
                { simulationError = exception.Message; simulationStatus = "failed"; }
                Publish();
                lock (state)
                {
                    if (!state.Dirty) { state.Working = false; released = true; return; }
                }
                await _delay(TimeSpan.FromSeconds(1), _shutdown.Token).ConfigureAwait(false);

                void Publish()
                {
                    // Background work can finish after a later update. Keep the previous readable PDF,
                    // but never expose that stale error as a repair request for the newer manuscript.
                    ScientificPresentationSnapshot published;
                    bool viewChanged;
                    lock (state)
                    {
                        if (superseded || generation != state.Generation) return;
                        var old = Volatile.Read(ref state.Snapshot);
                        var simulationDiagnosis = simulationError
                            ?? (simulation?.Status == "failed" ? simulation.Detail : null);
                        var feedback = new ScientificPresentationFeedback(generation, attemptedRevision, publicationStatus, publicationIssue,
                            simulationStatus, simulationDiagnosis is null ? null : ScientificPresentationFeedbackProjection.SimulationIssue(simulationDiagnosis));
                        var sameSimulation = simulation?.Status == old.Simulation?.Status && simulation?.Detail == old.Simulation?.Detail
                            && simulation?.Revision == old.Simulation?.Revision
                            && (simulation?.Artifacts ?? []).SequenceEqual(old.Simulation?.Artifacts ?? []);
                        viewChanged = publication?.PdfPath != old.Publication?.PdfPath || !sameSimulation
                            || publicationError != old.PublicationError || simulationError != old.SimulationError;
                        if (!viewChanged && feedback == old.Feedback) return;
                        published = new ScientificPresentationSnapshot(old.Version + (viewChanged ? 1 : 0), publication, simulation, publicationError, simulationError)
                            { Feedback = feedback };
                        Volatile.Write(ref state.Snapshot, published);
                    }
                    // Tool receipt generation changes do not invalidate retained UI tabs or PDF views.
                    if (!viewChanged) return;
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
        public long Generation;
        public ScientificPresentationSnapshot Snapshot = new(0, null, null, null, null);
    }
}

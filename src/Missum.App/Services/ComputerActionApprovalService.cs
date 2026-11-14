namespace Missum.App.Services;

/// <summary>Bridges a run-bound desktop request to the native chat without executing input itself.</summary>
public sealed class ComputerActionApprovalService : IComputerActionApproval
{
    private readonly object _gate = new();
    private PendingComputerApproval? _pending;
    private Guid _activeSession;
    private string? _activeRun;
    private readonly HashSet<string> _endedRuns = new(StringComparer.Ordinal);

    public event Action? Changed;

    public ComputerApprovalView Snapshot()
    {
        lock (_gate) return new(_activeSession, _activeRun, _pending);
    }

    public Task<bool> ActivateAsync(Guid sessionId, string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_endedRuns.Contains(runId)) return Task.FromResult(false);
            if (_activeRun is not null && (!string.Equals(_activeRun, runId, StringComparison.Ordinal) || _activeSession != sessionId))
                throw new InvalidOperationException("Ein anderer Computersteuerungs-Lauf ist bereits aktiv.");
            _activeSession = sessionId;
            _activeRun = runId;
        }
        NotifyChanged();
        return Task.FromResult(true);
    }

    public Task<ComputerActionDecision> RequestAsync(ComputerApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (_activeSession != request.SessionId || !string.Equals(_activeRun, request.RunId, StringComparison.Ordinal))
                throw new InvalidOperationException("Die Computersteuerung ist für diesen Lauf nicht freigegeben.");
        }
        return AwaitDecisionAsync(new PendingComputerApproval(Guid.NewGuid(), request.SessionId, request.RunId, false, request), cancellationToken);
    }

    public bool Decide(Guid id, Guid sessionId, string runId, ComputerActionDecision decision)
    {
        TaskCompletionSource<ComputerActionDecision>? completion;
        lock (_gate)
        {
            if (_pending is not { } pending || pending.Id != id || pending.SessionId != sessionId ||
                !string.Equals(pending.RunId, runId, StringComparison.Ordinal) ||
                (pending.IsActivation && decision == ComputerActionDecision.ManualDone)) return false;
            completion = pending.Completion;
        }
        return completion.TrySetResult(decision);
    }

    public void EndRun(string runId)
    {
        TaskCompletionSource<ComputerActionDecision>? completion = null;
        lock (_gate)
        {
            _endedRuns.Add(runId);
            if (string.Equals(_activeRun, runId, StringComparison.Ordinal)) { _activeRun = null; _activeSession = Guid.Empty; }
            if (_pending is { } pending && string.Equals(pending.RunId, runId, StringComparison.Ordinal)) completion = pending.Completion;
        }
        completion?.TrySetResult(ComputerActionDecision.Cancel);
        NotifyChanged();
    }

    private async Task<ComputerActionDecision> AwaitDecisionAsync(PendingComputerApproval request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_endedRuns.Contains(request.RunId))
                throw new InvalidOperationException("Dieser Computersteuerungs-Lauf wurde bereits beendet.");
            if (_pending is not null) throw new InvalidOperationException("Eine Computersteuerungs-Freigabe wartet bereits.");
            _pending = request;
        }
        NotifyChanged();
        using var registration = token.Register(() => request.Completion.TrySetCanceled(token));
        try { return await request.Completion.Task.ConfigureAwait(false); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_pending, request)) _pending = null; }
            NotifyChanged();
        }
    }

    private void NotifyChanged() => Changed?.Invoke();
}

public sealed class PendingComputerApproval(Guid id, Guid sessionId, string runId, bool isActivation, ComputerApprovalRequest? request)
{
    public Guid Id { get; } = id;
    public Guid SessionId { get; } = sessionId;
    public string RunId { get; } = runId;
    public bool IsActivation { get; } = isActivation;
    public ComputerApprovalRequest? Request { get; } = request;
    internal TaskCompletionSource<ComputerActionDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record ComputerApprovalView(Guid ActiveSession, string? ActiveRun, PendingComputerApproval? Pending);

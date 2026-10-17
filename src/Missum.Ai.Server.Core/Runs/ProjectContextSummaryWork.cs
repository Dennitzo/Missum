namespace Missum.Ai.Server.Core.Runs;

/// <summary>Background project knowledge yields the shared model to a new user run.</summary>
public sealed class ProjectContextSummaryWork : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _current;
    private readonly SemaphoreSlim _admission = new(1, 1);

    public async Task<IDisposable> EnterAdmissionAsync(CancellationToken cancellationToken)
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AdmissionLease(_admission);
    }

    private sealed class AdmissionLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    public CancellationTokenSource? TryBegin(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_current is not null) return null;
            _current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _current;
        }
    }

    public void End(CancellationTokenSource work)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, work)) _current = null;
            work.Dispose();
        }
    }

    public void Preempt()
    {
        lock (_gate) _current?.Cancel();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Cancel();
            _current?.Dispose();
            _current = null;
            _admission.Dispose();
        }
    }
}

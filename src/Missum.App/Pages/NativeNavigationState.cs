namespace Missum.App.Pages;

/// <summary>
/// UI navigation ordering, independent of persisted conversation revisions.
/// A snapshot can neither cross a newer navigation nor contradict the coordinator's active session.
/// </summary>
internal sealed class NativeNavigationState
{
    private long _generation;
    private long _pendingGeneration;

    public long Generation => Interlocked.Read(ref _generation);
    public bool IsNavigating => Interlocked.Read(ref _pendingGeneration) != 0;
    public bool CanEditComposer => !IsNavigating;

    public long BeginNavigation()
    {
        var generation = Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _pendingGeneration, generation);
        return generation;
    }

    public void CompleteNavigation(long generation) => Interlocked.CompareExchange(ref _pendingGeneration, 0, generation);

    public bool IsCurrent(long generation) => generation == Generation;

    public bool CanApplySnapshot(long generation, Guid snapshotSessionId, Guid? coordinatorSessionId) =>
        IsCurrent(generation) && snapshotSessionId != Guid.Empty
        && (!coordinatorSessionId.HasValue || coordinatorSessionId.Value == Guid.Empty || snapshotSessionId == coordinatorSessionId.Value);

    public static bool IsNavigationCommand(string type) => type is
        "app.ready" or "session.open" or "session.create" or "session.projectCreate" or "mode.switch" or "session.delete" or "session.clear";

    public static bool IsFullSnapshot(string type) => type is "state.snapshot" or "session.changed" or "document.changed";
}

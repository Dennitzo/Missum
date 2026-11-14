namespace Missum.App.Services;

// One page can close and reopen its model menu while a previous settings write
// is still in flight. Keep changes ordered and let Send await the newest one.
internal sealed class ContextLimitCommitQueue
{
    internal Task<bool> Pending { get; private set; } = Task.FromResult(true);

    internal Task<bool> Enqueue(Func<Task<bool>> commit)
    {
        var previous = Pending;
        return Pending = CommitAfterAsync(previous, commit);
    }

    private static async Task<bool> CommitAfterAsync(Task<bool> previous, Func<Task<bool>> commit)
    {
        await previous;
        return await commit();
    }
}

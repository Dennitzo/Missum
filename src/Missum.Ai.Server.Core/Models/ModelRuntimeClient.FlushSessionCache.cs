namespace Missum.Ai.Server.Core.Models;

public sealed partial class ModelRuntimeClient
{
    internal async Task FlushSessionCacheAsync(string modelId, string key, string? runtimeInstanceId = null)
    {
        var model = runtimeInstanceId ?? modelId;
        var lease = await AcquireTurnAsync(model, CancellationToken.None).ConfigureAwait(false);
        try { await UpdateSessionCacheAsync("save", model, key, cancellationToken: CancellationToken.None).ConfigureAwait(false); }
        finally { lease.Release(); }
    }

}

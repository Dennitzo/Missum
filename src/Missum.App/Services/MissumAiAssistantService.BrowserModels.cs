using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    internal async Task<ModelStatusSnapshot> GetBrowserModelsAsync(CancellationToken token)
    {
        using var client = await connection.CreateClientAsync(token).ConfigureAwait(false);
        return await client.GetModelStatusAsync(token).ConfigureAwait(false);
    }
}

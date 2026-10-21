namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    private async Task FlushRunSessionCacheAsync(string runId)
    {
        var request = await _repository.GetRequestAsync(runId).ConfigureAwait(false);
        var snapshot = await _repository.GetAsync(runId).ConfigureAwait(false);
        var model = snapshot?.SelectedModel ?? request?.PreferredCodingModelId ?? request?.PreferredGeneralModelId;
        if (request is null || string.IsNullOrWhiteSpace(model)) return;
        await _modelRuntime.FlushSessionCacheAsync(model,
            BuildRunSessionCacheKey(request, runId, request.Mode == Missum.Ai.Contracts.RunMode.Coding ? "coding" : "general"),
            request.Subagent?.RuntimeInstanceId).ConfigureAwait(false);
    }
}

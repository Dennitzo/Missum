namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private async Task<bool> HandleSourceReadAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken token)
    {
        if (envelope.Type != "sources.list") return false;
        var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
        var sources = await Task.Run(() => conversationSnapshots.GetSourcesAsync(sessionId, token), token).ConfigureAwait(false);
        await emit("sources.snapshot", new { sessionId, sources }, envelope.RequestId).ConfigureAwait(false);
        return true;
    }
}

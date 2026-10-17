using System.Text.Json;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private static void EnsureActivityCommandIsSupported(WebBridgeEnvelope envelope)
    {
        if (envelope.Type is not ("chat.send" or "chat.resume" or "chat.steer" or "chat.cancel")) return;
        foreach (var field in new[] { "agentId", "runId", "expectedRunId" })
        {
            if (!envelope.Payload.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var identity = value.GetString();
            if (identity?.StartsWith("deep-research:", StringComparison.Ordinal) == true
                || identity?.StartsWith("deep-research-run:", StringComparison.Ordinal) == true)
                throw new InvalidOperationException("Die Deep-Research-Ansicht gehört zum Hauptauftrag. Steuere den Auftrag im übergeordneten Chat.");
        }
    }
}

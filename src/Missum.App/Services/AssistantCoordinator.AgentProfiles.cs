using System.Text.Json;
using Missum.Ai.Contracts;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private SessionAgentProfileStore AgentProfileSelections => new(settings.DataDirectory);

    private static SpecializedAgentOptions? CaptureSpecializedAgent(JsonElement payload, Missum.Core.Models.ChatSession session, string prompt)
    {
        var profile = GetOptionalString(payload, "specializedAgentProfile", 64);
        var goal = GetOptionalString(payload, "specializedAgentGoal", 100_000) ?? prompt;
        return SpecializedAgentSelection.Capture(profile, session.ChatMode, goal);
    }

    private async Task SelectSessionAgentProfileAsync(WebBridgeEnvelope envelope,
        Func<string, object, string?, Task> emit, CancellationToken token)
    {
        var sessionId = GetRequiredGuid(envelope.Payload, "sessionId");
        var session = await chats.GetSessionAsync(sessionId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die ausgewählte Tool-Sitzung wurde nicht gefunden.");
        var profile = GetRequiredString(envelope.Payload, "profile", 64);
        _ = SpecializedAgentSelection.Capture(profile, session.ChatMode, "Auswahl für den nächsten Auftrag");
        var enabled = GetRequiredBoolean(envelope.Payload, "enabled");
        if (!enabled)
        {
            await CancelScheduledSessionAsync(sessionId, token).ConfigureAwait(false);
            // A durable gateway job may temporarily have no page reader or
            // scheduler ticket, particularly just after restart/reconnect.
            if (missumAi is not null)
                await missumAi.CancelPersistedSessionRunsAsync(sessionId, token).ConfigureAwait(false);
        }
        await AgentProfileSelections.WriteAsync(sessionId, enabled ? profile : null, token).ConfigureAwait(false);
        await emit("session.changed", await BuildSnapshotAsync(token).ConfigureAwait(false), envelope.RequestId).ConfigureAwait(false);
    }

    private async Task<string?> SelectedAgentProfileAsync(Guid sessionId, CancellationToken token)
    {
        var active = missumAi?.ActiveSpecializedAgentProfile(sessionId);
        return active ?? await AgentProfileSelections.ReadAsync(sessionId, token).ConfigureAwait(false);
    }
}

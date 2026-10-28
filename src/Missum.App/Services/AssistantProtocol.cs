namespace Missum.App.Services;

/// <summary>In-process commands and events shared by the native desktop and its services.</summary>
public static class AssistantProtocol
{
    public const int ProtocolVersion = 2;
    private static readonly HashSet<string> AllowedIncomingTypes = new(StringComparer.Ordinal)
    {
        "settings.get", "settings.update", "settings.connectionTest", "promptTriggers.list", "promptTriggers.apply",
        "backup.create", "backup.restore", "backup.restoreCommit", "backup.restoreCancel", "models.list", "models.select",
        "chat.resume", "chat.compact", "session.groupDeleteEmpty", "coding.pickWorkspace",
        "app.ready", "conversation.refresh", "conversation.page", "conversation.prompts", "conversation.promptPage", "conversation.blocks.page", "subagent.blocks.page", "tool.details", "subagent.load", "chat.send", "chat.steer", "chat.cancel", "session.create", "session.open",
        "reasoning.get", "reasoning.set", "session.agentProfile",
        "session.rename", "session.delete", "session.clear", "session.draft", "session.groupCollapse", "session.projectCreate", "session.workspaceCreate", "mode.switch", "action.invoke", "document.pick", "document.paste",
        "document.remove", "attachment.remove", "message.exportPdf", "message.copy",
        "memory.list", "memory.create", "memory.update", "memory.pin", "memory.confirm", "memory.delete", "memory.autoCapture",
        "publication.list", "publication.open", "sources.list",
        "artifact.save", "artifact.preview", "artifact.open", "screen.capture", "screenClip.start", "screenClip.stop", "screenClip.cancel",
        "audioCapture.start", "audioCapture.stop", "audioCapture.cancel",
        "microphone.start", "microphone.audio", "microphone.speak", "microphone.stopSpeech", "microphone.toggleSpeechPause", "microphone.stop", "microphone.cancel",
        "liveCaption.start", "liveCaption.stop", "ui.sessionPane", "external.open",
    };
    private static readonly HashSet<string> AllowedOutgoingTypes = new(StringComparer.Ordinal)
    {
        "settings.snapshot", "settings.changed", "settings.conflict", "settings.connectionResult", "promptTriggers.snapshot", "promptTriggers.changed",
        "backup.ready", "backup.restoreReady", "backup.restoreDeferred", "backup.restored", "backup.cancelled", "models.snapshot", "settings.error",
        "subagent.snapshot", "sources.snapshot", "workingPlan.changed",
        "state.snapshot", "chat.queued", "queue.changed", "chat.started", "chat.delta", "chat.completed",
        "chat.cancelled", "chat.failed", "session.changed", "session.grouped",
        "document.changed", "document.import.started", "document.import.progress", "document.import.completed", "status.changed", "speech.status", "speech.progress", "theme.changed",
        "draft.saved", "caption.changed", "screenClip.changed", "audioCapture.changed", "capture.required", "capture.cancelled",
        "microphone.changed", "microphone.transcript", "artifact.previewReady", "host.error",
        "conversation.snapshot", "conversation.page", "conversation.prompts", "conversation.promptPage", "conversation.blocks.page", "subagent.blocks.page", "tool.details", "conversation.messageCommitted", "coding.changes",
        "reasoning.snapshot",
        "chat.steer.accepted",
        "action.completed", "memory.snapshot", "memory.changed", "publication.snapshot",
    };
    private static readonly HashSet<string> ClientScopedOutgoingTypes = new(StringComparer.Ordinal)
    {
        "settings.snapshot", "settings.conflict", "settings.connectionResult", "promptTriggers.snapshot", "backup.ready", "backup.restoreReady",
        "backup.restored", "backup.restoreDeferred", "backup.cancelled", "models.snapshot", "settings.error",
        "state.snapshot", "conversation.snapshot", "conversation.page", "conversation.prompts", "conversation.promptPage", "conversation.blocks.page", "subagent.blocks.page", "tool.details", "sources.snapshot",
        "session.changed", "session.grouped",
        "document.changed", "document.import.started", "document.import.progress", "document.import.completed",
        "draft.saved", "speech.status", "speech.progress", "caption.changed", "screenClip.changed",
        "audioCapture.changed", "capture.required", "capture.cancelled", "microphone.changed",
        "microphone.transcript", "artifact.previewReady", "host.error",
        "reasoning.snapshot", "chat.steer.accepted", "action.completed", "memory.snapshot", "publication.snapshot",
    };

    internal static bool IsIncomingTypeAllowed(string type) => AllowedIncomingTypes.Contains(type);
    internal static bool IsOutgoingTypeAllowed(string type) => AllowedOutgoingTypes.Contains(type);
    internal static bool IsClientScopedOutgoingType(string type) => ClientScopedOutgoingTypes.Contains(type);
    internal static object BuildProtocolContract() => new
    {
        version = ProtocolVersion,
        clientActions = AllowedIncomingTypes.Order(StringComparer.Ordinal).ToArray(),
        hostEvents = AllowedOutgoingTypes.Order(StringComparer.Ordinal).ToArray(),
    };
}

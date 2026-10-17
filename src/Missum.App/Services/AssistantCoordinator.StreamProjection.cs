using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private sealed class StreamProjection
    {
        internal Guid SessionId;
        internal string Content = "";
        internal long Revision;
        internal string Generation = Guid.NewGuid().ToString("N");
        internal Dictionary<string, AssistantToolStep>? LiveSteps;
        internal Dictionary<string, AssistantToolStep>? LiveSubagents;
        internal bool IsTerminal;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, StreamProjection> _streamProjections = new();

    private object ProjectStreamDelta(MissumAiAssistantUpdate update)
    {
        var projection = _streamProjections.GetOrAdd(update.Message.Id, _ => new() { SessionId = update.Message.SessionId });
        lock (projection)
        {
            var content = update.Message.Content;
            var appended = content.StartsWith(projection.Content, StringComparison.Ordinal)
                ? content[projection.Content.Length..] : null;
            var replacement = appended is null ? content : null;
            projection.Content = content;
            projection.Revision++;
            return new
            {
                messageId = update.Message.Id, sessionId = update.Message.SessionId,
                contentAppend = appended, contentReplace = replacement,
                toolStep = LiveDisplayToolStep(update.ToolStep), toolStepOrdinal = StreamStepOrdinal(update), toolStepCount = update.Message.ToolSteps?.Count,
                revision = projection.Revision,
                streamGeneration = projection.Generation,
                updatedAt = update.Message.UpdatedAt,
            };
        }
    }

    private static AssistantToolStep? LiveDisplayToolStep(AssistantToolStep? step) => step is null ? null
        : step.Tool == "assistant.reasoning" ? step with { InputJson = null, OutputJson = null, PreviewHtml = null }
        : DisplayToolStep(step);

    private static int? StreamStepOrdinal(MissumAiAssistantUpdate update) => update.ToolStep is { } step
        && update.Message.ToolSteps is ConversationStreamBuffer.Steps steps ? steps.Ordinal(step.Id) : null;

    private void ResetStreamProjection(ChatMessage message)
    {
        var projection = new StreamProjection { SessionId = message.SessionId, Content = message.Content, Revision = message.Revision };
        foreach (var step in message.ToolSteps ?? []) RememberLiveStep(projection, message, step);
        _streamProjections[message.Id] = projection;
        // This is display state only. Completed conversations can be reloaded from SQLite.
        if (_streamProjections.Count > 128)
            foreach (var id in _streamProjections.Keys.Where(id => id != message.Id).Take(_streamProjections.Count - 64))
                _streamProjections.TryRemove(id, out _);
    }

    private void ObserveLiveToolStep(MissumAiAssistantUpdate update)
    {
        if (update.Kind == MissumAiAssistantUpdateKind.Started) return;
        var terminal = update.Kind is MissumAiAssistantUpdateKind.Completed or MissumAiAssistantUpdateKind.Cancelled or MissumAiAssistantUpdateKind.Failed;
        if (update.ToolStep is null && !terminal) return;
        var projection = _streamProjections.GetOrAdd(update.Message.Id, _ => new() { SessionId = update.Message.SessionId });
        lock (projection)
        {
            if (projection.SessionId == Guid.Empty) projection.SessionId = update.Message.SessionId;
            if (terminal)
            {
                // Terminal updates follow the durable commit. Do not retain a
                // second detail cache for completed or inactive conversations.
                projection.LiveSteps = null; projection.LiveSubagents = null; projection.IsTerminal = true;
            }
            else if (!projection.IsTerminal && projection.SessionId == update.Message.SessionId)
                RememberLiveStep(projection, update.Message, update.ToolStep!);
        }
    }

    private static void RememberLiveStep(StreamProjection projection, ChatMessage message, AssistantToolStep step)
    {
        (projection.LiveSteps ??= new(StringComparer.Ordinal))[step.Id] = step;
        if (step.Tool != SubagentChatState.ReceiptTool || step.OutputJson is null) return;
        foreach (var child in SubagentChatState.ReadSummaries([message with { ToolSteps = [step] }]))
            (projection.LiveSubagents ??= new(StringComparer.Ordinal))[child.AgentId] = step;
    }

    private AssistantToolStep? FindLiveToolStep(Guid sessionId, Guid messageId, string key, bool subagent = false)
    {
        if (!_streamProjections.TryGetValue(messageId, out var projection)) return null;
        lock (projection)
            return projection.SessionId == sessionId && !projection.IsTerminal
                ? (subagent ? projection.LiveSubagents : projection.LiveSteps)?.GetValueOrDefault(key) : null;
    }

    private long NextStreamRevision(Guid id)
    {
        var projection = _streamProjections.GetOrAdd(id, _ => new());
        lock (projection) return ++projection.Revision;
    }

    private string StreamGeneration(Guid id) => _streamProjections.GetOrAdd(id, _ => new()).Generation;
}

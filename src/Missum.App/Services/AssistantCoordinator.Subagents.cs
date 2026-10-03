namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private static object ToSubagentDto(SubagentChatState state) => new
    {
        state.AgentId, state.RunId, state.ParentRunId, state.SessionId, state.Title, state.Status,
        messages = new[] { ToMessageDto(state.UserMessage), ToMessageDto(state.AssistantMessage, state.Artifacts) },
        state.IsRunning, state.Model, state.ContextUsed, state.ContextLimit,
        state.RunStatus, state.RunDetail, state.GenerationState, state.GeneratedTokens, state.GenerationUpdatedAt,
        state.ResultDelivered, state.LifecycleText,
        runMessageId = state.AssistantMessage.Id,
    };
}

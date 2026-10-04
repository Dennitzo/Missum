namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private static object ToSubagentDto(SubagentChatState state) => new
    {
        state.AgentId, state.RunId, state.ParentRunId, state.SessionId, state.Title, state.Status,
        messages = (state.PreviousMessages ?? []).Select(message => ToMessageDto(message,
            state.PreviousMessageArtifacts?.GetValueOrDefault(message.Id.ToString())))
            .Concat([ToMessageDto(state.UserMessage), ToMessageDto(state.AssistantMessage, state.Artifacts)]).ToArray(),
        state.IsRunning, state.Model, state.ContextUsed, state.ContextLimit,
        state.RunStatus, state.RunDetail, state.GenerationState, state.GeneratedTokens, state.GenerationUpdatedAt,
        state.ResultDelivered, state.LifecycleText,
        state.ProjectionRevision,
        state.PreviousRunIds,
        attemptStartedAt = state.AssistantMessage.CreatedAt,
        runMessageId = state.AssistantMessage.Id,
    };
}

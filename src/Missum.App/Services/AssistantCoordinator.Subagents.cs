namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private readonly Lazy<SubagentPlanetIdentityStore> _browserPlanetIdentities =
        new(() => new SubagentPlanetIdentityStore(settings.DataDirectory));

    private object[] ToSubagentDtos(IEnumerable<SubagentChatState> states)
    {
        var children = states.ToArray();
        _browserPlanetIdentities.Value.EnsureAssigned(children.Select(child => child.AgentId));
        return children.Select(ToSubagentDto).ToArray();
    }

    private object ToSubagentDto(SubagentChatState state) => new
    {
        state.AgentId, state.RunId, state.ParentRunId, state.SessionId, state.Title, state.Status,
        planetIndex = _browserPlanetIdentities.Value.GetOrAssign(state.AgentId),
        messages = (state.PreviousMessages ?? []).Select(message => ToMessageDto(message,
            state.PreviousMessageArtifacts?.GetValueOrDefault(message.Id.ToString())))
            .Concat([ToMessageDto(state.UserMessage), ToMessageDto(state.AssistantMessage, state.Artifacts)]).ToArray(),
        state.IsRunning, state.Model, state.ContextUsed, state.ContextLimit,
        state.RunStatus, state.RunDetail, state.GenerationState, state.GeneratedTokens, state.GenerationUpdatedAt,
        state.ProcessedPromptTokens, state.TotalPromptTokens, state.PromptProgress,
        state.ResultDelivered, state.LifecycleText,
        state.ProjectionRevision,
        state.PreviousRunIds,
        attemptStartedAt = state.AssistantMessage.CreatedAt,
        runMessageId = state.AssistantMessage.Id,
    };
}

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

    private object ToSubagentDto(SubagentChatState state)
    {
        var sourceUpdates = (state.PreviousMessages ?? []).Concat([state.AssistantMessage])
            .SelectMany(message => message.ToolSteps ?? []).SelectMany(step => LiveSourcesFor(step) ?? [])
            .GroupBy(source => source.Id + ":" + source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToArray();
        state = state.ForDisplay();
        return new
        {
        state.AgentId, state.RunId, state.ParentRunId, state.SessionId, state.Title, state.Status,
        state.Kind, state.OriginToolStepId, state.OriginMessageId,
        state.TranscriptRevision,
        canStop = !state.IsLegacyDeepResearch,
        canResume = !state.IsLegacyDeepResearch,
        state.GpuIndex,
        sourceUpdates,
        state.TranscriptDeferred, state.ParentMessageId,
        planetIndex = _browserPlanetIdentities.Value.GetOrAssign(state.AgentId),
        messages = (state.PreviousMessages ?? []).Select(message => ToMessageDto(message,
            state.PreviousMessageArtifacts?.GetValueOrDefault(message.Id.ToString()), state.ToolStepWindows?.GetValueOrDefault(message.Id)))
            .Concat([ToMessageDto(state.UserMessage, toolWindow: state.ToolStepWindows?.GetValueOrDefault(state.UserMessage.Id)),
                ToMessageDto(state.AssistantMessage, state.Artifacts, state.ToolStepWindows?.GetValueOrDefault(state.AssistantMessage.Id),
                    preserveLiveReasoning: state.IsRunning)]).ToArray(),
        toolStepWindows = state.ToolStepWindows,
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
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal const int MaximumEarlyDelegationRetries = 2;

    internal static bool RequestsEarlyResearchDelegation(RunRequest request) => request.DeepResearch
        && request.Subagent is null
        && request.ConversationProfile != ConversationProfile.ContextPreparation
        && request.ClientCapabilities?.Contains("subagents", StringComparer.OrdinalIgnoreCase) == true;

    private Task EnsureEarlyResearchEnrollmentAsync(string runId, RunRequest request, ModelSelection selection,
        CancellationToken cancellationToken)
    {
        var options = request.ResearchOptions ?? new DeepResearchOptions();
        var projectId = options.ProjectId ?? "research-" + runId;
        // This is transport enrollment of the actual task, not a synthetic
        // interpretation or the serial research pipeline. The client needs the
        // same durable start receipt to bind sources and its manuscript to this
        // attempt. Never alter the model's common context or evaluated prefix.
        return _repository.EnsureAgentManagedResearchEnrollmentAsync(runId, JsonSerializer.SerializeToElement(new
        {
            projectId,
            state = "agentManagedResearchStarted",
            revision = 1,
            completed = 0,
            total = 1,
            timestamp = DateTimeOffset.UtcNow,
            runId,
            sessionId = request.SessionId,
            modelId = selection.ModelId,
            originalTask = ExtractOriginalTask(request),
            profile = DeepResearchProfileNames.ToProtocolName(options.Profile),
            researchOptions = options,
        }, MissumAiProtocol.CreateJsonOptions()), cancellationToken);
    }

    // Admission requires actual fitted model residency, not an unloaded model
    // name. Do this before deciding whether a serial research bootstrap is needed.
    private async Task<int> PrepareEarlyResearchModelAsync(string runId, ModelSelection selection,
        int contextLength, CancellationToken cancellationToken)
    {
        await using var lease = await _scheduler.AcquireAsync("llm-research-delegation", runId,
            GpuLeaseMode.Shared, cancellationToken).ConfigureAwait(false);
        var preparation = await _workers.PrepareLmModelWithStatusAsync(selection.ModelId, contextLength,
            async token => await _repository.AppendEventAsync(runId, RunEventTypes.ModelLoading,
                new ModelLoadingEvent(selection.ModelId, "loading", contextLength, contextLength), token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        if (!preparation.WasAlreadyLoaded)
            await _repository.AppendEventAsync(runId, RunEventTypes.ModelLoading,
                new ModelLoadingEvent(selection.ModelId, "loaded", contextLength, preparation.ContextLength),
                cancellationToken).ConfigureAwait(false);
        return ResolveLoadedContextLength(contextLength, preparation);
    }

    internal static bool IsValidEarlyDelegationResponse(LmChatResult response, IReadOnlyList<AgentToolSpec> tools,
        AgentToolCatalog catalog)
    {
        if (response.ToolCalls.Count != 1 || response.ToolCalls[0].Name != SubagentToolNames.Spawn) return false;
        try
        {
            catalog.Validate(catalog.Resolve(SubagentToolNames.Spawn, tools), response.ToolCalls[0].Arguments);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.Text.Json.JsonException) { return false; }
    }

    internal static void EnsureEarlyResearchInstructions(List<LmChatMessage> messages)
    {
        var index = messages.FindIndex(static message => message.Role == "system");
        if (index >= 0 && messages[index].Content?.Contains(SubagentAgentPolicy.EarlyResearchDelegation,
            StringComparison.Ordinal) != true)
            messages[index] = messages[index] with
            { Content = messages[index].Content + "\n\n" + SubagentAgentPolicy.EarlyResearchDelegation };
    }
}

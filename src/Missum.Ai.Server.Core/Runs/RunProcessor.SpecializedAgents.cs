using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Agents;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runtime;

namespace Missum.Ai.Server.Core.Runs;

internal sealed record SpecializedAgentReviewFrame(SpecializedAgentCheckpoint State, List<LmChatMessage> Messages,
    long InputTokens = 0, long OutputTokens = 0, long Rounds = 0, long Compactions = 0);
internal sealed record SpecializedAgentReviewResult(SpecializedAgentReviewFrame Frame, bool Steered = false);

public sealed partial class RunProcessor
{
    private async Task<SpecializedAgentReviewResult> ExecuteSpecializedAgentReviewAsync(string runId, RunRequest request,
        ModelSelection selection, int contextLength, IReadOnlyList<LmToolDefinition> tools,
        SpecializedAgentCheckpoint state, List<LmChatMessage> messages, CodingWorkingState? workingState,
        Func<SpecializedAgentReviewFrame, Task> persist, CancellationToken cancellationToken)
    {
        var profile = _specializedAgents.Resolve(state.ProfileId, state.Version);
        var definition = profile.Definition;
        var invocation = state.InvocationId ?? throw new InvalidDataException("Die Agentenprüfung besitzt keine gespeicherte Identität.");
        var frame = new SpecializedAgentReviewFrame(state, messages);
        var assignedTask = definition.DisplayName + " · Zielprüfung, Runde " + state.Cycle
            + "\n\nPrüfe Ziel, Arbeitsplan, tatsächliche Belege und offene Probleme. Gib bei Bedarf einen konkreten nächsten Auftrag an den Hauptagenten.\n\nUrsprüngliches Nutzerziel:\n" + state.OriginalGoal;
        SubagentRunEvent Lifecycle(RunState status) => new(runId, invocation, state.AgentId, assignedTask,
            selection.ModelId, status, request.SessionId, Specialization: definition.UiKind ?? definition.Id, GpuIndex: 0);
        await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentStarted, Lifecycle(RunState.Running), cancellationToken).ConfigureAwait(false);
        await _repository.AppendEventAsync(runId, RunEventTypes.SubagentUpdated, Lifecycle(RunState.Running), cancellationToken).ConfigureAwait(false);
        await _repository.EnsureSpecializedAgentEventAsync(runId, state.AgentId, invocation, RunEventTypes.ModelSelected,
            new ModelSelectedEvent(selection.ModelId, selection.Role), cancellationToken).ConfigureAwait(false);
        await _repository.EnsureSpecializedAgentEventAsync(runId, state.AgentId, invocation, RunEventTypes.ModelGeneration,
            new ModelGenerationEvent("generationStarted"), cancellationToken).ConfigureAwait(false);

        await using var steering = _repository.WatchSteering(runId, cancellationToken);
        try
        {
            if (frame.State.Decision is null)
            {
                await using var lease = await _scheduler.AcquireAsync("specialized-agent", runId, GpuLeaseMode.Shared, steering.Token).ConfigureAwait(false);
                while (frame.State.Decision is null)
                {
                    steering.Token.ThrowIfCancellationRequested();
                    if (CodingContextCompactor.Plan(frame.Messages, contextLength, workingState,
                        includeWorkingStateInBudget: false) is { } compaction)
                    {
                        var condensed = await _modelRuntime.CompleteChatWithCacheOwnershipAsync(selection.ModelId,
                            compaction.SummaryRequest, [], request.SessionId ?? runId, null,
                            modelRole: selection.Role, reasoningEffort: _modelRuntime.ResolveCompactionReasoningEffort(selection.ModelId, selection.Role),
                            requiredContextLength: contextLength, sessionCacheKey: BuildRunSessionCacheKey(request, runId, selection.Role),
                            residentOnly: true, suppressToolCalls: true, cancellationToken: steering.Token).ConfigureAwait(false);
                        frame = frame with { Messages = CodingContextCompactor.Complete(compaction, condensed.Content).ToList(),
                            InputTokens = frame.InputTokens + condensed.InputTokens, OutputTokens = frame.OutputTokens + condensed.OutputTokens,
                            Rounds = frame.Rounds + 1, Compactions = frame.Compactions + 1 };
                        await persist(frame).ConfigureAwait(false);
                        await _repository.AppendEventAsync(runId, RunEventTypes.ContextChanged,
                            new ContextChangedEvent(ContextPlanner.EstimateTokens(frame.Messages), contextLength, 0, true,
                                "Der Arbeitskontext wurde vor der Agentenprüfung komprimiert. Nutzerziel, Plan und gespeicherte Agentenphase bleiben erhalten.",
                                CompactionPerformed: true), steering.Token).ConfigureAwait(false);
                    }
                    var context = ContextPlanner.Prepare(frame.Messages, contextLength, request.Limits?.MaximumOutputTokens,
                        allowLossyCompaction: false, preserveConversationPrefix: true);
                    LmChatResult response;
                    for (var retry = 0; ; retry++)
                    {
                        try
                        {
                            frame = frame with { Messages = ModelRuntimeClient.PrepareLanguageBoundMessages(context.Messages).ToList() };
                            await persist(frame).ConfigureAwait(false);
                            await _repository.EnsureSpecializedAgentEventAsync(runId, state.AgentId, invocation, RunEventTypes.ContextChanged,
                                new ContextChangedEvent(context.EstimatedInputTokens, contextLength, 0, false,
                                    "Serielle Zielprüfung auf der geladenen Hauptinstanz mit demselben Sitzungs- und KV-Kontext."), steering.Token).ConfigureAwait(false);
                            response = await _modelRuntime.CompleteChatWithCacheOwnershipAsync(selection.ModelId,
                                frame.Messages, tools, request.SessionId ?? runId, null, request.Limits?.MaximumOutputTokens,
                                modelRole: selection.Role, reasoningEffort: request.ReasoningEffort,
                                requiredContextLength: contextLength, sessionCacheKey: BuildRunSessionCacheKey(request, runId, selection.Role),
                                responseSchema: SpecializedAgentProtocol.DecisionSchema, residentOnly: true, suppressToolCalls: true,
                                cancellationToken: steering.Token).ConfigureAwait(false);
                            break;
                        }
                        catch (NativeContextBudgetException overflow) when (retry < ScientificNativeContextRecovery.MaximumRetries)
                        {
                            context = ScientificNativeContextRecovery.Prepare(context.Messages, overflow, request.Limits?.MaximumOutputTokens);
                            frame = frame with { Messages = context.Messages.ToList(), Compactions = frame.Compactions + 1 };
                            await persist(frame).ConfigureAwait(false);
                            await _repository.AppendEventAsync(runId, RunEventTypes.ContextChanged,
                                new ContextChangedEvent(context.EstimatedInputTokens, contextLength, 0, true,
                                    "Der native Kontext wurde für die Agentenprüfung verdichtet; Ziel und Agentenstand bleiben gespeichert.",
                                    CompactionPerformed: true), steering.Token).ConfigureAwait(false);
                        }
                    }
                    frame.Messages.Add(new LmChatMessage("assistant", response.Content, ReasoningContent: response.ReasoningContent));
                    frame = frame with { InputTokens = frame.InputTokens + response.InputTokens,
                        OutputTokens = frame.OutputTokens + response.OutputTokens, Rounds = frame.Rounds + 1 };
                    try
                    {
                        var decision = SpecializedAgentProtocol.Parse(response.Content, workingState);
                        frame = frame with { State = frame.State with { Decision = decision, Phase = "decision" } };
                        await persist(frame).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidDataException)
                    {
                        frame = frame with { State = frame.State with { ReviewAttempts = frame.State.ReviewAttempts + 1 } };
                        if (frame.State.ReviewAttempts >= 3)
                        {
                            await persist(frame).ConfigureAwait(false);
                            throw new SpecializedAgentException("specialized_agent.invalid_decision",
                                "Die spezialisierte Agentenprüfung konnte keine gültige Entscheidung abgeben. Der gespeicherte Stand ist erhalten. " + exception.Message, exception);
                        }
                        frame.Messages.Add(new("user", "Missum: Die Entscheidung ist noch nicht gültig. " + exception.Message
                            + " Antworte erneut ausschließlich im vorgegebenen Entscheidungsschema. Eine offene notwendige Prüfung verlangt continue und einen konkreten Folgeauftrag."));
                        await persist(frame).ConfigureAwait(false);
                    }
                }
            }
            if (steering.SteeringRequested || await _repository.HasPendingSteeringAsync(runId, cancellationToken).ConfigureAwait(false))
            {
                await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted, Lifecycle(RunState.Cancelled), cancellationToken).ConfigureAwait(false);
                return new(frame, Steered: true);
            }
            var result = frame.State.Decision ?? throw new InvalidDataException("Die Agentenentscheidung fehlt.");
            await _repository.EnsureSpecializedAgentEventAsync(runId, state.AgentId, invocation, RunEventTypes.TextDelta,
                new TextDeltaEvent(SpecializedAgentProtocol.Render(definition, result)), cancellationToken).ConfigureAwait(false);
            await _repository.EnsureSpecializedAgentEventAsync(runId, state.AgentId, invocation, RunEventTypes.RunCompleted,
                new RunCompletedEvent(definition.DisplayName + " · Runde " + state.Cycle, selection.ModelId,
                    frame.InputTokens, frame.OutputTokens), cancellationToken).ConfigureAwait(false);
            await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted, Lifecycle(RunState.Completed), cancellationToken).ConfigureAwait(false);
            await _repository.EnsureSpecializedAgentResultConsumedAsync(runId, state.AgentId, invocation, cancellationToken).ConfigureAwait(false);
            return new(frame);
        }
        catch (OperationCanceledException) when (steering.SteeringRequested && !cancellationToken.IsCancellationRequested)
        {
            await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted, Lifecycle(RunState.Cancelled), cancellationToken).ConfigureAwait(false);
            return new(frame, Steered: true);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("specialized_agent.model_not_resident:", StringComparison.Ordinal))
        {
            throw new SpecializedAgentException("specialized_agent.model_not_resident",
                "Die spezialisierte Agentenprüfung benötigt die geladene Hauptinstanz. Diese ist derzeit nicht verfügbar. "
                + "Es wurde kein anderes Modell geladen. Der gespeicherte Arbeitsstand kann fortgesetzt werden.", exception);
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("specialized_agent.unexpected_tool_call:", StringComparison.Ordinal))
        {
            throw new SpecializedAgentException("specialized_agent.unexpected_tool_call",
                "Das Modell hat während der werkzeugfreien Agentenprüfung einen Werkzeugaufruf statt einer Entscheidung ausgegeben. "
                + "Der Aufruf wurde nicht ausgeführt; der gespeicherte Arbeitsstand bleibt erhalten.", exception);
        }
    }

    private async Task CloseSpecializedAgentAsync(string runId, RunState state)
    {
        var checkpoint = await _repository.GetCheckpointAsync(runId).ConfigureAwait(false);
        if (checkpoint?.SpecializedAgent is not { InvocationId: { } invocation } specialized || specialized.Phase is "main" or "completed") return;
        var request = await _repository.GetRequestAsync(runId).ConfigureAwait(false);
        var definition = _specializedAgents.Resolve(specialized.ProfileId, specialized.Version).Definition;
        await _repository.EnsureSubagentLifecycleEventAsync(RunEventTypes.SubagentCompleted,
            new(runId, invocation, specialized.AgentId, definition.DisplayName + " · Zielprüfung, Runde " + specialized.Cycle,
                checkpoint.SelectedModelId ?? "", state, request?.SessionId,
                Specialization: definition.UiKind ?? definition.Id, GpuIndex: 0)).ConfigureAwait(false);
    }
}

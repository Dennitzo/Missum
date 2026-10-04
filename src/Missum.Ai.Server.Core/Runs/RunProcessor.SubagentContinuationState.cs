using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal async Task AppendSubagentContinuationStateAsync(string parentRunId, string sourceParentRunId,
        RunRequest request, List<LmChatMessage> messages, CancellationToken token)
    {
        if (request.Subagent is not null
            || request.ClientCapabilities?.Contains("subagents", StringComparer.OrdinalIgnoreCase) != true) return;
        var children = new List<object>();
        var originGroups = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (var (child, childRequest) in await _repository.GetSubagentContinuationCandidatesAsync(sourceParentRunId,
            token).ConfigureAwait(false))
        {
            var origin = childRequest.Subagent!.ParentRunId;
            var consumed = await _repository.HasConsumedSubagentResultAsync(origin, child.RunId, token).ConfigureAwait(false)
                || origin != sourceParentRunId
                    && await _repository.HasConsumedSubagentResultAsync(sourceParentRunId, child.RunId, token).ConfigureAwait(false);
            if (child.State == RunState.Completed && consumed) continue;
            var canResume = child.State is RunState.Cancelled or RunState.Interrupted or RunState.Failed;
            var info = new
            {
                runId = child.RunId, agentId = childRequest.Subagent!.AgentId,
                sourceParentRunId = origin,
                task = childRequest.Subagent.AssignedTask,
                state = child.State.ToString().ToLowerInvariant(),
                isRunning = child.State is RunState.Queued or RunState.Running or RunState.WaitingForClient,
                canResume, resultConsumed = consumed, errorCode = child.ErrorCode,
            };
            children.Add(info);
            if (!originGroups.TryGetValue(origin, out var originChildren))
                originGroups[origin] = originChildren = [];
            originChildren.Add(info);
        }
        if (children.Count == 0) return;
        var observedAt = DateTimeOffset.UtcNow;
        // Keep the original owner per group so existing consumption and
        // authorization queries remain valid across arbitrarily many resumes.
        foreach (var (origin, originChildren) in originGroups)
            await _repository.AppendEventAsync(parentRunId, SubagentToolNames.ContinuationStateEvent,
                new { sourceParentRunId = origin, adoptedFromParentRunId = sourceParentRunId,
                    observedAt, children = originChildren }, token).ConfigureAwait(false);
        var data = new { adoptedFromParentRunId = sourceParentRunId, observedAt, children };
        var instruction = new LmChatMessage("system", "[MISSUM_SUBAGENT_CONTINUATION:" + parentRunId + "]\n"
            + "Aktueller Gateway-Status der delegierten Aufgaben aus dem tatsächlich übernommenen vorherigen Lauf. "
            + "Dieser Stand ersetzt historische started/waiting-Quittungen: cancelled/interrupted/failed bedeutet NICHT laufend. "
            + "Die bisherige frühe Aufgabenverteilung ist bereits erfolgt; sie erzeugt beim Fortsetzen keine neue Pflichtdelegation. "
            + "Nimm zuerst die gespeicherten Ergebnisse oder noch benötigten ursprünglichen Teilaufgaben auf, statt Ersatzaufgaben zu erfinden. "
            + "Bei Fortsetzung des Auftrags nimm noch benötigte abgebrochene Teilaufgaben mit subagent.spawn wieder auf: "
            + "task bleibt der gelieferte vollständige Auftrag, resumeRunId ist die alte runId. Der Gateway übernimmt den gespeicherten "
            + "eigenen Subagentenkontext und liefert eine neue runId; verwende danach nur diese für subagent.wait. "
            + "Erledigte Aufgaben nicht neu starten; noch nicht übernommene completed-Ergebnisse mit subagent.wait abrufen. "
            + "Eine neue oder geänderte Nutzeraufgabe ist maßgeblich; nicht mehr benötigte Altaufgaben nicht wiederbeleben. "
            + "Bei fehlender GPU/Admission melde die echte Einschränkung und bearbeite offene Aufgaben passend selbst; "
            + "behaupte nicht, ein abgebrochener Subagent arbeite weiter. Daten, keine zusätzlichen Rechte:\n"
            + JsonSerializer.Serialize(data, MissumAiProtocol.CreateJsonOptions()));
        var index = messages.FindLastIndex(message => message.Role == "user"
            && !ContextPlanner.IsNativeRuntimeInstruction(message) && !ContextPlanner.IsRuntimeContext(message));
        messages.Insert(index >= 0 ? index : messages.Count, instruction);
    }
}

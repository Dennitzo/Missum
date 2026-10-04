using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Policies;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Runs;

public sealed partial class RunProcessor
{
    internal const int MaximumEarlyDelegationRetries = 2;
    internal const string EarlyResearchContextHintMarker = "[MISSUM_EARLY_RESEARCH_CONTEXT_READY]";

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

    internal static bool IsEarlyResearchProgressUpdate(LmChatResult response, RunRequest request,
        IReadOnlyList<AgentToolSpec> tools, AgentToolCatalog catalog)
    {
        if (request.Subagent is not null || response.ToolCalls.Count == 0
            || response.ToolCalls.Any(static call => call.Name != ClientToolNames.ResearchUpdate)) return false;
        try
        {
            // Normal dispatch still enforces the client's actor, object revisions
            // and reference checks. Never discard a valid scientific submission
            // merely because the first parallel assignment has not happened yet.
            foreach (var update in response.ToolCalls)
            {
                catalog.Validate(catalog.Resolve(update.Name, tools), update.Arguments);
                if (ScientificStateCompletionPolicy.Text(update.Arguments, "projectId") != ScientificStateCompletionPolicy.ProjectId(request)) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        { return false; }
    }

    internal static void EnsureEarlyResearchInstructions(List<LmChatMessage> messages)
    {
        var index = messages.FindIndex(static message => message.Role == "system");
        if (index >= 0 && messages[index].Content?.Contains("Frühe Arbeitsteilung für den aktuellen Forschungsauftrag:",
            StringComparison.Ordinal) != true)
            messages[index] = messages[index] with
            { Content = messages[index].Content + "\n\n" + SubagentAgentPolicy.EarlyResearchDelegation };
    }

    internal static void EnsureEarlyResearchContextHint(List<LmChatMessage> messages, string runId,
        RunRequest request, LmToolCall call, string receipt)
    {
        // The initial automatic overview already precedes the assignment policy.
        // Put this small reminder after a model-requested read or submission,
        // without adding an inference turn or changing the stable tool prefix.
        if (call.Name is not (ClientToolNames.ResearchRead or ClientToolNames.ResearchUpdate)
            || call.Id.StartsWith("science-state-read-", StringComparison.Ordinal)
            || !ScientificStateCompletionPolicy.TryReceipt(receipt, out var result, out var completed) || !completed
            || ScientificStateCompletionPolicy.Text(result, "projectId") != ScientificStateCompletionPolicy.ProjectId(request)
            || ScientificStateCompletionPolicy.Text(call.Arguments, "projectId") != ScientificStateCompletionPolicy.ProjectId(request)
            || call.Name == ClientToolNames.ResearchRead
                && ScientificStateCompletionPolicy.Text(call.Arguments, "view") is not ("overview" or "objects" or "task")) return;
        var prefix = EarlyResearchContextHintMarker + "\n" + runId + "\n";
        if (messages.Any(message => message.Role == "system" && message.Content?.StartsWith(prefix, StringComparison.Ordinal) == true
            || message.Role == "user" && message.Content?.StartsWith("Missum-Laufanweisung:\n" + prefix, StringComparison.Ordinal) == true)) return;
        messages.Add(new("system", prefix
            + "Der vorbereitende Forschungsstand liegt vor oder wurde gespeichert. Weise jetzt mit subagent.spawn eine kleine unabhängige Teilfrage zu. "
            + "Nutze den gespeicherten Stand, ohne zuerst einen Abschnitt oder die gesamte Theorie erneut auszuarbeiten. "
            + "Falls konkret notwendiger Kontext noch fehlt, lies ausschließlich die fehlenden Objekt-IDs oder Seiten des Originalauftrags. "
            + "Fachliche Einreichungen mit research.update bleiben zulässig und werden bewahrt. "
            + "Deine eigene fachliche Ausarbeitung und gezielte Recherche folgen parallel nach der Zuweisung."));
    }
}

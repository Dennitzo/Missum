using System.Text.Json;
using System.Text;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>
/// A sequential research view of the owning conversation, never a separate
/// session or mutable cache branch. The evaluated prefix is retained until an
/// explicit compaction boundary; tools and the current research phase stay intact.
/// </summary>
internal sealed class ResearchConversationContext
{
    private readonly List<LmChatMessage> _messages;
    private readonly LmChatMessage[] _systemMessages;
    private readonly LmChatMessage? _originalRequest;
    private int _currentPhaseStart = -1;
    private CodingCompactionPlan? _pendingCompaction;
    private int _plannedMessageCount;
    private int _phase;

    internal ResearchConversationContext(IReadOnlyList<LmChatMessage> parentMessages,
        IReadOnlyList<LmToolDefinition> parentTools)
    {
        _messages = parentMessages.ToList();
        _systemMessages = parentMessages.TakeWhile(static message => message.Role == "system").ToArray();
        _originalRequest = parentMessages.LastOrDefault(static message => message.Role == "user"
            && !ContextPlanner.IsRuntimeContext(message) && !ContextPlanner.IsNativeRuntimeInstruction(message)
            && !ModelRuntimeClient.IsLanguageReminder(message)
            && message.Content?.StartsWith(CodingContextCompactor.MemoryMarker, StringComparison.Ordinal) != true
            && message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) != true
            && message.Content?.StartsWith(CodingSessionContext.StateMarker, StringComparison.Ordinal) != true);
        Tools = parentTools.ToArray();
        var pending = new Dictionary<string, LmToolCall>(StringComparer.Ordinal);
        foreach (var message in parentMessages)
        {
            if (message.Role == "assistant")
                foreach (var call in message.ToolCalls ?? []) pending[call.Id] = call;
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        // The owner has not finished web.deepResearch (or sibling calls) yet.
        // Close their protocol only in this temporary view. The durable main
        // transcript receives the actual results in its normal execution order.
        foreach (var call in pending.Values)
            _messages.Add(new("tool", JsonSerializer.Serialize(new
            {
                status = "in_progress",
                message = "Dieser Aufruf wird vom Hauptlauf ausgeführt. Jetzt wird ausschließlich seine Recherche-Teilphase bearbeitet; den Aufruf nicht wiederholen.",
            }), ToolCallId: call.Id));
    }

    internal IReadOnlyList<LmToolDefinition> Tools { get; }
    internal IReadOnlyList<LmChatMessage> Messages => _messages.ToArray();

    internal IReadOnlyList<LmChatMessage> CreateRequest(StagedWebResearchModelRequest request)
    {
        if (request.ResponseRepair is null) _currentPhaseStart = _messages.Count;
        _pendingCompaction = null;
        if (request.ResponseRepair is null)
            foreach (var message in request.Messages)
                _messages.Add(message.Role == "system"
                    ? new("user", "Missum-Laufanweisung:\n" + message.Content)
                    : message);
        var schema = OutputTool(request).Parameters;
        _messages.Add(new("user", "Missum-Laufanweisung:\n"
            + (request.ResponseRepair is null ? "" : "Korrigiere ausschließlich die vorige Antwort dieser Teilphase: " + request.ResponseRepair
                + " Verwende die bereits vorhandenen Originalbelege und Quellen-IDs; wiederhole keine Recherche oder Werkzeugaufrufe. ")
            + "Du bearbeitest die Recherche im unveränderten Kontext des Hauptlaufs. "
            + "Die oben genannte Rechercheaktion bezeichnet jetzt ausschließlich das Ergebnisformat dieser Teilphase. "
            + "Führe keinen Werkzeugaufruf aus. Antworte nur mit einem JSON-Objekt für " + request.RequiredToolName
            + ", ohne Markdown oder Einleitung. Das verbindliche JSON-Schema lautet:\n" + schema.GetRawText()));
        return _messages.ToArray();
    }

    /// <summary>
    /// Archive only completed context before this phase. The current evidence
    /// payload and its schema are protected together, independent of which of
    /// their user-role messages the general Coding planner would select.
    /// </summary>
    internal CodingCompactionPlan? PlanCompaction(int contextLength, CodingWorkingState? workingState = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(contextLength, 2048);
        if (_originalRequest is null || _currentPhaseStart < 0) return null;
        var archive = _messages.Take(_currentPhaseStart).Skip(_systemMessages.Length)
            .Where(message => !ReferenceEquals(message, _originalRequest)).ToArray();
        if (archive.Length == 0) return null;

        var transcript = new StringBuilder();
        foreach (var message in archive)
        {
            transcript.Append('[').Append(message.Role).Append(' ').Append(message.ToolCallId).AppendLine("]");
            if (message.Content is { } content) transcript.AppendLine(content);
            foreach (var call in message.ToolCalls ?? [])
                transcript.Append(call.Id).Append(' ').Append(call.Name).Append(' ').AppendLine(call.Arguments.GetRawText());
        }
        var state = workingState is null ? null
            : CodingEvidenceContext.Build(workingState, Math.Clamp(contextLength / 2, 2048, 12_000));
        var budget = ContextPlanner.ComputeInputTokenBudget(contextLength, null);
        var maximumTranscript = Math.Max(2048, budget * 2 - (_originalRequest.Content?.Length ?? 0) - (state?.Content?.Length ?? 0));
        var history = BoundArchive(transcript.ToString(), maximumTranscript);
        var tail = _messages.Skip(_currentPhaseStart).ToArray();
        var fixedMessages = _systemMessages.Concat(state is null ? [] : new[] { state })
            .Append(_originalRequest).Concat(tail).ToArray();
        var summaryCharacters = (int)Math.Clamp(((long)budget - ContextPlanner.EstimateTokens(fixedMessages)) * 3 / 2,
            256, Math.Min(16_000, Math.Max(256, contextLength / 4)));
        _pendingCompaction = new(
            [new("system", CodingContextCompactor.SummaryInstruction
                + "\nBewahre bei dieser Recherche Quellen-IDs und URLs, belegte Befunde, widersprechende Belege, Unsicherheiten und offene Teilfragen. "
                + "Der aktuelle Phasenauftrag mit seinen Originalbelegen und seinem Antwortschema bleibt separat unverändert erhalten."),
             new("user", "Ursprünglicher Auftrag:\n" + _originalRequest.Content + "\n\nBisherige abgeschlossene Arbeit:\n" + history),
             .. (state is null ? Array.Empty<LmChatMessage>() : new[] { state })],
            _systemMessages, _originalRequest, tail, archive.Length, summaryCharacters,
            state, state is null ? [] : [state]);
        _plannedMessageCount = _messages.Count;
        return _pendingCompaction;
    }

    internal IReadOnlyList<LmChatMessage> CompleteCompaction(CodingCompactionPlan plan, string? summary)
    {
        if (!ReferenceEquals(plan, _pendingCompaction) || _messages.Count != _plannedMessageCount)
            throw new InvalidOperationException("Die Recherchephase hat sich während der Kontextkomprimierung geändert.");
        var replacement = CodingContextCompactor.Complete(plan, summary);
        if (ContextPlanner.EstimateTokens(replacement) >= ContextPlanner.EstimateTokens(_messages))
            throw new InvalidOperationException("Die Recherche-Zusammenfassung verkleinert den Arbeitskontext nicht.");
        _messages.Clear();
        _messages.AddRange(replacement);
        _currentPhaseStart = replacement.Length - plan.RecentMessages.Count;
        _pendingCompaction = null;
        return Messages;
    }

    private static string BoundArchive(string text, int maximum)
    {
        if (text.Length <= maximum) return text;
        const string marker = "\n[Ältere Details begrenzt; vollständige Belege bleiben im Laufjournal]\n";
        var head = (maximum - marker.Length) * 2 / 3;
        return text[..head] + marker + text[^(maximum - marker.Length - head)..];
    }

    internal LmChatResult AcceptResponse(StagedWebResearchModelRequest request, LmChatResult response)
    {
        if (response.ToolCalls.Count > 0)
        {
            // Preserve genuine native calls and close them with explicit rejection
            // receipts. Nothing is dispatched; repair must not leave pending calls
            // or introduce the adapter's synthetic research calls into the prompt.
            _messages.Add(new("assistant", response.Content, ToolCalls: response.ToolCalls, ReasoningContent: response.ReasoningContent));
            foreach (var call in response.ToolCalls)
                _messages.Add(new("tool", "{\"status\":\"rejected\",\"code\":\"research.phase_tools_disabled\",\"message\":\"Nicht ausgeführt. Diese Teilphase verlangt ausschließlich das angegebene JSON-Ergebnis.\"}", ToolCallId: call.Id));
            var error = new InvalidDataException("Die Recherche-Teilphase darf keine Werkzeuge des Hauptlaufs ausführen. Gib stattdessen das verlangte JSON-Objekt aus.");
            throw new ResearchPhaseResponseException(error.Message, response, error);
        }
        var output = OutputTool(request);
        // Preserve the evaluated native assistant turn, even if a format-only
        // repair is needed. Synthetic calls never enter the native transcript.
        _messages.Add(new("assistant", response.Content, ReasoningContent: response.ReasoningContent));
        JsonElement arguments;
        try
        {
            arguments = ResearchPhaseResponse.Parse(response.Content, output.Parameters);
        }
        catch (InvalidDataException exception)
        {
            throw new ResearchPhaseResponseException(exception.Message, response, exception);
        }
        // The existing pipeline validates and consumes its normal phase result.
        // This adapter is not an executable model tool call in the conversation.
        return response with { Content = null, ToolCalls = [new("research-phase-" + ++_phase, output.Name, arguments)] };
    }

    private static LmToolDefinition OutputTool(StagedWebResearchModelRequest request) =>
        request.Tools.Single(tool => tool.Name == request.RequiredToolName);
}

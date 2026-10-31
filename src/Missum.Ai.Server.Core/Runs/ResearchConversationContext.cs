using System.Text.Json;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>
/// A sequential research view of the owning conversation, never a separate
/// session or mutable cache branch. The evaluated parent prefix and tools stay
/// unchanged; only the temporary research tail is discarded on return.
/// </summary>
internal sealed class ResearchConversationContext
{
    private readonly List<LmChatMessage> _messages;
    private int _phase;

    internal ResearchConversationContext(IReadOnlyList<LmChatMessage> parentMessages,
        IReadOnlyList<LmToolDefinition> parentTools)
    {
        _messages = parentMessages.ToList();
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

    internal IReadOnlyList<LmChatMessage> CreateRequest(StagedWebResearchModelRequest request)
    {
        foreach (var message in request.Messages)
            _messages.Add(message.Role == "system"
                ? new("user", "Missum-Laufanweisung:\n" + message.Content)
                : message);
        var schema = OutputTool(request).Parameters;
        _messages.Add(new("user", "Missum-Laufanweisung:\nDu bearbeitest die Recherche im unveränderten Kontext des Hauptlaufs. "
            + "Die oben genannte Rechercheaktion bezeichnet jetzt ausschließlich das Ergebnisformat dieser Teilphase. "
            + "Führe keinen Werkzeugaufruf aus. Antworte nur mit einem JSON-Objekt für " + request.RequiredToolName
            + ", ohne Markdown oder Einleitung. Das verbindliche JSON-Schema lautet:\n" + schema.GetRawText()));
        return _messages.ToArray();
    }

    internal LmChatResult AcceptResponse(StagedWebResearchModelRequest request, LmChatResult response)
    {
        if (response.ToolCalls.Count > 0)
            throw new InvalidDataException("Die Recherche-Teilphase darf keine Werkzeuge des Hauptlaufs ausführen.");
        JsonElement arguments;
        try
        {
            using var document = JsonDocument.Parse(response.Content ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Die Recherche-Teilphase benötigt ein JSON-Objekt.");
            arguments = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Die Recherche-Teilphase lieferte kein gültiges JSON-Ergebnis.", exception);
        }
        var output = OutputTool(request);
        _messages.Add(new("assistant", response.Content, ReasoningContent: response.ReasoningContent));
        // The existing pipeline validates and consumes its normal phase result.
        // This adapter is not an executable model tool call in the conversation.
        return response with { Content = null, ToolCalls = [new("research-phase-" + ++_phase, output.Name, arguments)] };
    }

    private static LmToolDefinition OutputTool(StagedWebResearchModelRequest request) =>
        request.Tools.Single(tool => tool.Name == request.RequiredToolName);
}

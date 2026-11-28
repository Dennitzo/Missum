using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Enforces a fresh observation between desktop actions, including failed ones.</summary>
internal static class ComputerObservationGuard
{
    internal const int MaxRetries = 3;
    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    internal const string Instruction = "Die letzte Computeraktion ist abgeschlossen oder fehlgeschlagen. "
        + "Bevor du eine weitere Aktion ausführst oder eine Abschlussmeldung abgibst, rufe genau einmal computer.observe auf. "
        + "Werte danach das tatsächliche aktuelle Bild aus. Ohne neue Beobachtung darf kein Erfolg behauptet werden.";

    /// <summary>Adds the observation request to a computer.act result (JSON object or plain text).</summary>
    internal static string WithInstruction(string result)
    {
        if (result.Contains(Instruction, StringComparison.Ordinal)) return result;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(result) is System.Text.Json.Nodes.JsonObject json)
            {
                json["next"] = Instruction;
                return json.ToJsonString(RelaxedJson);
            }
        }
        catch (System.Text.Json.JsonException) { }
        return result + "\n" + Instruction;
    }

    /// <summary>True when the newest message already carries the observation request.</summary>
    internal static bool InstructionPending(IReadOnlyList<LmChatMessage> messages) =>
        messages.Count > 0 && messages[^1] is { Role: "tool" } last
            && last.Content?.Contains(Instruction, StringComparison.Ordinal) == true;

    internal static bool IsRequiredObserveCall(IReadOnlyList<LmToolCall> calls) =>
        calls.Count == 1 && calls[0].Name == "computer.observe";

    internal static bool RequiresAfterResult(bool required, string name, bool succeeded) => name switch
    {
        "computer.act" => true,
        "computer.observe" when succeeded => false,
        _ => required,
    };

    internal static bool RequiresFromHistory(IReadOnlyList<LmChatMessage> messages, string runId)
    {
        var required = false;
        foreach (var message in messages)
        {
            if (message.ToolCalls?.Any(call => call.Name == "computer.act") == true) required = true;
            if (message.ComputerObservationId is not null && message.ComputerObservationRunId == runId) required = false;
        }
        return required;
    }
}

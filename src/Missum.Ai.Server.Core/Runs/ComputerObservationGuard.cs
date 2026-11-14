using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

/// <summary>Enforces a fresh observation between desktop actions, including failed ones.</summary>
internal static class ComputerObservationGuard
{
    internal const int MaxRetries = 3;
    internal const string Instruction = "Die letzte Computeraktion ist abgeschlossen oder fehlgeschlagen. "
        + "Bevor du eine weitere Aktion ausführst oder eine Abschlussmeldung abgibst, rufe genau einmal computer.observe auf. "
        + "Werte danach das tatsächliche aktuelle Bild aus. Ohne neue Beobachtung darf kein Erfolg behauptet werden.";

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

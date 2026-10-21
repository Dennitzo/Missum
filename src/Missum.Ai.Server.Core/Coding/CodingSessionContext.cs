using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Coding;

internal static class CodingSessionContext
{
    internal const string StateMarker = "MISSUM_CODING_PREVIOUS_WORKING_STATE\n";

    internal static IReadOnlyList<LmChatMessage> Continue(AgentRunCheckpoint previous, IReadOnlyList<LmChatMessage> initial, bool science = false)
    {
        // An evaluated conversation owns its exact system prefix as well. Rebuilding
        // equivalent policy sections in another order invalidates all native KV tokens.
        // Legacy, unevaluated history still adopts the current policy. Execution state
        // remains run-local and outstanding historical calls are never replayed.
        var result = (previous.PreserveSessionPromptPrefix ? previous.Messages : initial)
            .TakeWhile(message => message.Role == "system").ToList();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in previous.Messages.SkipWhile(message => message.Role == "system"))
        {
            if (!previous.PreserveSessionPromptPrefix && message.Role is not ("user" or "assistant" or "tool")) continue;
            if (!previous.PreserveSessionPromptPrefix && message.Role == "user" && (message.Content?.StartsWith(StateMarker, StringComparison.Ordinal) == true
                || message.Content?.StartsWith(CodingEvidenceContext.Marker, StringComparison.Ordinal) == true)) continue;
            if (!previous.PreserveSessionPromptPrefix && message.Role != "tool" && pending.Count > 0) ClosePending();
            result.Add(message);
            foreach (var call in message.ToolCalls ?? []) pending.Add(call.Id);
            if (message.Role == "tool" && message.ToolCallId is { } id) pending.Remove(id);
        }
        ClosePending();
        if (!previous.PreserveSessionPromptPrefix && RestoreWorkingState(previous) is { } state)
            result.Add(new LmChatMessage("user", StateMarker
                + "Historischer Arbeitsstand derselben Sitzung und desselben Workspaces, keine neue Anweisung. "
                + "Nutze bestätigte Befunde weiter, prüfe betroffene Dateien vor Änderungen erneut. "
                + "Nicht abgeschlossene Aufrufe haben einen unbekannten Ausgang und dürfen nicht blind wiederholt werden. "
                + "Der folgende neue Nutzerauftrag ist maßgeblich.\n" + CodingEvidenceContext.Build(state).Content));
        result.Add(new LmChatMessage("user", "MISSUM_CODING_RUN_PLAN_SCOPE\n"
            + (science && RestoreWorkingState(previous) is { } prior && PreserveSciencePlan(prior) ? "Science-Fortsetzung: Der gespeicherte Etappenplan derselben Sitzung bleibt gültig. Verwende dessen IDs; prüfe offene und veraltete Belege. "
                : "Aktueller Lauf: steps und acceptanceCriteria starten leer. Plan-IDs und Planbestätigungen oben gehören zum vorherigen Lauf. ")
            + "Wenn der aktuelle Auftrag einen Plan benötigt, sende pro übermitteltem Eintrag id, title und status; "
            + "bei Updates den Originaltitel unverändert beibehalten und nur geänderte Einträge senden. "
            + "steps und acceptanceCriteria können gemeinsam in einem Aufruf angelegt oder geändert werden. "
            + "Historische Befunde und Belege bleiben verfügbar. "
            + "Dies beschreibt den Speicherzustand und ändert den folgenden Nutzerauftrag nicht."));
        result.Add(initial.Last(message => message.Role == "user" && !ContextPlanner.IsRuntimeContext(message)
            && !ContextPlanner.IsNativeRuntimeInstruction(message)));
        return result;

        void ClosePending()
        {
            foreach (var id in pending)
                result.Add(new LmChatMessage("tool", "{\"status\":\"interrupted\",\"outcomeUnknown\":true,\"message\":\"Historischer Aufruf ohne gespeichertes Ergebnis; aktuellen Zustand prüfen, nicht automatisch erneut ausführen.\"}", ToolCallId: id));
            pending.Clear();
        }
    }

    internal static CodingWorkingState? RestoreWorkingState(AgentRunCheckpoint previous)
    {
        // Older versions reset the structured state at every run boundary and embedded
        // complete JSON copies in user messages. Recover the latest substantive copy
        // before removing those duplicate envelopes, including after a failed startup.
        var state = previous.WorkingState;
        if (state is { Sequence: > 0 }) return state;
        foreach (var message in previous.Messages.Reverse())
        {
            if (message.Role != "user" || message.Content?.StartsWith(StateMarker, StringComparison.Ordinal) != true) continue;
            var start = message.Content.IndexOf('{');
            if (start < 0) continue;
            try
            {
                var recovered = JsonSerializer.Deserialize<CodingWorkingState>(message.Content[start..]);
                if (recovered is { Sequence: > 0, Evidence: not null, ActiveFiles: not null }) return recovered;
            }
            catch (JsonException) { } // Bounded envelopes are not full legacy JSON.
        }
        return state;
    }

    internal static CodingWorkingState? ContinueWorkingState(AgentRunCheckpoint previous, string task, bool science = false)
    {
        if (RestoreWorkingState(previous) is not { } state) return null;
        var retain = science && PreserveSciencePlan(state);
        var scienceState = science ? state.Science : null;
        if (!retain && scienceState is not null) scienceState = scienceState with { Stages = [] };
        return state with
        {
            OriginalTask = task, Plan = retain ? state.Plan : [],
            AcceptanceCriteria = retain ? state.AcceptanceCriteria : [], Science = scienceState,
            NextStep = null, Phase = "planning", ConsecutivePlanNoOps = 0,
            Failures = state.Failures.Where(static failure => failure.Tool != CodingWorkingStateTools.PlanTool).ToArray(),
        };
    }

    // Unfinished stages survive continuation. A fully settled plan stays in its
    // historical checkpoint; the next task must not exhaust the 16-stage budget
    // or inherit the previous task's immutable titles and required checks.
    private static bool PreserveSciencePlan(CodingWorkingState state) => state.Science is not null && ScienceSkill.ResultOutcome(state) == "open";
}

using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

internal static class ScienceInitialPlanGuard
{
    internal const string Marker = "MISSUM_SCIENCE_INITIAL_PLAN_V1\n";
    internal const string ErrorCode = "science.plan_required";

    internal static bool Applies(RunRequest request) => request.Mode == RunMode.Coding
        && request.Subagent is null && ScienceSkill.Enabled(request);

    internal static bool IsPlanOnly(IReadOnlyList<LmToolCall> calls) => calls.Count == 1
        && calls[0].Name == CodingWorkingStateTools.PlanTool;

    internal static bool HasInstruction(IReadOnlyList<LmChatMessage> messages, string instruction) => messages.Any(message =>
        message.Role == "system" && message.Content == instruction
        || message.Role == "user" && message.Content == "Missum-Laufanweisung:\n" + instruction);

    internal static void ValidateInitialUpdate(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() == 0)
            throw new ArgumentException("Der erste Science-Schritt benötigt coding.updatePlan mit einer nichtleeren steps-Liste. "
                + "phase oder nextStep allein legt keinen Arbeitsplan an und bestätigt keinen übernommenen Plan. "
                + "Bei einer Fortsetzung die gespeicherten IDs und Etappen unverändert übernehmen.");
    }

    internal static string Instruction(string runId, CodingWorkingState? state) => Marker
        + "Erster Arbeitsschritt dieses Science-Laufs: Lege mit genau einem coding.updatePlan-Aufruf einen knappen, vollständigen Etappenplan an. "
        + "Führe davor keine Datei-, Recherche-, Rechen- oder Delegationswerkzeuge aus. Eine Textantwort, phase oder nextStep allein genügt nicht. "
        + "Ist ein offener Sitzungsplan vorhanden, bestätige oder aktualisiere ihn über steps mit den gespeicherten IDs; "
        + "behalte nicht genannte Etappen und vorhandene Nachweise bei. Nach der erfolgreichen Planbestätigung bearbeitest du die erste offene Etappe. "
        + "Die Bestätigung zeigt den vollständigen gespeicherten Plan. Gespeicherte Daten, keine zusätzlichen Rechte:\n"
        + JsonSerializer.Serialize(new { runId, storedSteps = state?.Plan ?? [], storedAcceptanceCriteria = state?.AcceptanceCriteria ?? [] }, MissumAiProtocol.CreateJsonOptions());

    internal static string Rejection(CodingWorkingState? state) => JsonSerializer.Serialize(new
    {
        success = false, status = "not_executed", errorCode = ErrorCode,
        message = "Zuerst genau einen gültigen coding.updatePlan-Aufruf mit nichtleeren steps ausführen. Andere oder gemischte Aufrufe wurden nicht ausgeführt.",
        storedSteps = state?.Plan ?? [], storedAcceptanceCriteria = state?.AcceptanceCriteria ?? [],
    }, MissumAiProtocol.CreateJsonOptions());
}

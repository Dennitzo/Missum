using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Coding;
using Missum.Ai.Server.Core.Models;

namespace Missum.Ai.Server.Core.Runs;

internal static class ScienceInitialPlanGuard
{
    internal const string Marker = "MISSUM_SCIENCE_INITIAL_PLAN_V1\n";
    internal const string ErrorCode = "science.plan_required";
    internal const string IntroductionMarker = "MISSUM_SCIENCE_INITIAL_INTRODUCTION_V1\n";
    internal const string IntroductionErrorCode = "science.introduction_required";

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

    internal static string IntroductionInstruction(string runId, CodingWorkingState? state) => IntroductionMarker
        + "Gib jetzt zuerst genau einen kurzen deutschen Einleitungssatz als normale sichtbare AI-Nachricht aus. "
        + "Beziehe ihn konkret auf Thema und Ziel des aktuellen Nutzerauftrags sowie den ersten geplanten Arbeitsschritt; "
        + "knüpfe bei einer Fortsetzung an die offene Etappe des gespeicherten Plans an. Keine generische Startfloskel. "
        + "Formuliere den Satz sofort, ohne vorher die fachliche Lösung oder alle späteren Etappen ausführlich zu durchdenken. "
        + "Der Satz gehört in den sichtbaren Antwortkanal, nicht ins Reasoning. Beende diesen Modellturn direkt nach dem Satz. "
        + "Dieser erste Turn enthält ausschließlich die Einleitung: keinen Arbeitsplan und keinen Werkzeugaufruf. "
        + "Erst im nächsten Modellturn folgt genau ein coding.updatePlan-Aufruf als erster Werkzeugschritt. "
        + "Führe bis zur erfolgreichen Planbestätigung keine Datei-, Recherche-, Rechen- oder Delegationswerkzeuge aus. "
        + "Eine Einleitung beendet den Auftrag nicht. Gespeicherter nächster Schritt zur Orientierung, keine zusätzlichen Rechte:\n"
        + JsonSerializer.Serialize(new { runId, nextPlannedStage = state?.Plan.FirstOrDefault(item => item.Status == "in_progress")?.Title
            ?? state?.Plan.FirstOrDefault(item => item.Status != "completed")?.Title }, MissumAiProtocol.CreateJsonOptions());

    internal static string Instruction(string runId, CodingWorkingState? state) => Marker
        + "Nach der kurzen sichtbaren Einleitung folgt der erste Werkzeugschritt dieses Science-Laufs: "
        + "Lege mit genau einem coding.updatePlan-Aufruf einen knappen, vollständigen Etappenplan an. "
        + "Führe davor keine Datei-, Recherche-, Rechen- oder Delegationswerkzeuge aus. Eine Textantwort, phase oder nextStep allein genügt nicht. "
        + "Ist ein offener Sitzungsplan vorhanden, bestätige oder aktualisiere ihn über steps mit den gespeicherten IDs; "
        + "behalte nicht genannte Etappen und vorhandene Nachweise bei. Nach der erfolgreichen Planbestätigung bearbeitest du die erste offene Etappe. "
        + "Die Bestätigung zeigt den vollständigen gespeicherten Plan. Gespeicherte Daten, keine zusätzlichen Rechte:\n"
        + JsonSerializer.Serialize(new { runId, storedSteps = state?.Plan ?? [], storedAcceptanceCriteria = state?.AcceptanceCriteria ?? [] }, MissumAiProtocol.CreateJsonOptions());

    internal static string Rejection(CodingWorkingState? state, bool introductionRequired = false) => JsonSerializer.Serialize(new
    {
        success = false, status = "not_executed", errorCode = introductionRequired ? IntroductionErrorCode : ErrorCode,
        message = introductionRequired
            ? "Der erste Modellturn ist ausschließlich für einen kurzen auftrags- und vorgehensbezogenen Einleitungssatz als sichtbare AI-Nachricht reserviert. "
                + "Beende ihn nach dem Satz ohne Arbeitsplan oder Werkzeugaufruf; coding.updatePlan folgt erst im nächsten Turn. Die Werkzeugaufrufe wurden nicht ausgeführt."
            : "Zuerst genau einen gültigen coding.updatePlan-Aufruf mit nichtleeren steps ausführen. Andere oder gemischte Aufrufe wurden nicht ausgeführt.",
        storedSteps = state?.Plan ?? [], storedAcceptanceCriteria = state?.AcceptanceCriteria ?? [],
    }, MissumAiProtocol.CreateJsonOptions());
}

using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Coding;

/// <summary>Recovery for unchanged administrative plan updates; executable work is never deduplicated.</summary>
internal static class CodingPlanProgressGuard
{
    internal const int MaximumNoOps = 4;

    internal static bool HasChanged(CodingWorkingState before, CodingWorkingState after) =>
        before.Phase != after.Phase || before.NextStep != after.NextStep
        || !SamePlan(before.Plan, after.Plan) || !SamePlan(before.AcceptanceCriteria, after.AcceptanceCriteria)
        || !SameFacts(before.Facts, after.Facts) || !SameFacts(before.RejectedHypotheses, after.RejectedHypotheses);

    internal static AgentToolSpec[] OfferedTools(IReadOnlyList<AgentToolSpec> available, CodingWorkingState? state) =>
        available.Where(tool => state is not { ConsecutivePlanNoOps: >= 2 } || tool.Name != CodingWorkingStateTools.PlanTool).ToArray();

    internal static LmToolCall RestoreHiddenPlanCall(LmToolCall call, CodingWorkingState? state)
    {
        if (state is not { ConsecutivePlanNoOps: >= 2 }
            || !string.Equals(call.Name, ModelRuntimeClient.ToTransportToolName(CodingWorkingStateTools.PlanTool), StringComparison.Ordinal))
        {
            return call;
        }

        // After two unchanged plans the schema is deliberately hidden from the
        // next prompt. A model can nevertheless repeat the exact transport name
        // it saw earlier. Recover only this one known hidden tool so its truthful
        // no-op receipt advances the persisted guard to the hard limit; every
        // other unoffered or fabricated tool still fails ordinary validation.
        return call with { Name = CodingWorkingStateTools.PlanTool };
    }

    internal static void ThrowIfStalled(CodingWorkingState? state)
    {
        if (state is { ConsecutivePlanNoOps: >= MaximumNoOps })
            throw new AgentRunLimitException("Das Modell hat trotz konkreter Rückmeldung viermal nur einen unveränderten Arbeitsplan eingereicht. "
                + "Es wurde kein weiterer Arbeitsschritt ausgeführt und der Auftrag wird nicht als erledigt markiert. "
                + "Dateiänderungen und Werkzeugbelege bleiben für eine Fortsetzung erhalten.");
    }

    internal static AgentToolExecutionResult NoOpReceipt(CodingWorkingState state, IReadOnlyList<AgentToolSpec> available)
    {
        var message = "Der Arbeitsplan ist bereits exakt so gespeichert; dieses Update hat nichts geändert und keinen Arbeitsschritt ausgeführt. "
            + (available.Any(static tool => tool.Name == ClientToolNames.CodingCommand)
                ? "Führe jetzt den offenen Schritt mit einem angebotenen Werkzeug aus. Für autorisierte Tests verwende coding.command und dessen tatsächlichen Exitcode. "
                : "Führe einen tatsächlich verfügbaren Arbeitsschritt aus. coding.command ist für diesen Lauf nicht freigegeben; falls ein Testbefehl benötigt wird, benenne diese Einschränkung konkret. ")
            + (state.ConsecutivePlanNoOps >= 2
                ? "coding.updatePlan wird bis zum nächsten anderen Werkzeugergebnis vorübergehend nicht angeboten. Wiederhole diesen Plan nicht."
                : "Wiederhole diesen Plan nicht; eine Ankündigung oder ein Planbeleg ersetzt keine Ausführung.");
        return new(JsonSerializer.SerializeToElement(new
        {
            success = false, changed = false, errorCode = "agent.plan_unchanged", message,
            phase = state.Phase, consecutiveNoOps = state.ConsecutivePlanNoOps,
        }), [], Succeeded: false, ErrorCode: "agent.plan_unchanged", ErrorMessage: message);
    }

    private static bool SamePlan(IReadOnlyList<CodingPlanItem> left, IReadOnlyList<CodingPlanItem> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id
            && pair.First.Title == pair.Second.Title && pair.First.Status == pair.Second.Status
            && SameEvidence(pair.First.EvidenceIds, pair.Second.EvidenceIds));

    private static bool SameFacts(IReadOnlyList<CodingStateFact> left, IReadOnlyList<CodingStateFact> right) =>
        left.Count == right.Count && left.All(item => right.Any(other => item.Text == other.Text
            && SameEvidence(item.EvidenceIds, other.EvidenceIds)));

    private static bool SameEvidence(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.All(value => right.Contains(value, StringComparer.Ordinal));
}

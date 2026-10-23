using Missum.Core.Models;
using System.Text.Json;

namespace Missum.App.Services;

public sealed partial class AssistantCoordinator
{
    private async Task<object> BuildWorkingPlanAsync(Guid sessionId, CancellationToken cancellationToken) =>
        WorkingPlanDto(await conversationSnapshots.GetWorkingPlanAsync(sessionId, cancellationToken).ConfigureAwait(false));

    internal static object WorkingPlanDto(AssistantToolStep? step)
    {
        var plan = NativeWorkingPlanProjection.FromToolStep(step);
        return new { plan.Steps, plan.AcceptanceCriteria, updatedAt = step?.UpdatedAt ?? step?.StartedAt, stepId = step?.Id };
    }

    private static bool HasFullWorkingPlan(AssistantToolStep step)
    {
        try
        {
            using var document = JsonDocument.Parse(step.OutputJson ?? "{}");
            var output = document.RootElement;
            return output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("storedSteps", out var steps) && steps.ValueKind == JsonValueKind.Array
                && output.TryGetProperty("storedAcceptanceCriteria", out var criteria) && criteria.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }
}

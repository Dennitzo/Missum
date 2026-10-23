using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial record SubagentChatState
{
    // Compact metadata survives display paging and summary-only restoration.
    // It carries no unrelated tool bodies and never requires opening the transcript.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? WorkingPlan { get; init; }

    private sealed record CachedWorkingPlan(JsonElement? Value);
    private static readonly ConditionalWeakTable<object, CachedWorkingPlan> WorkingPlans = new();

    internal JsonElement? WorkingPlanForDisplay()
    {
        // Projected messages contain only a tail of compact headers. Their
        // authoritative plan was captured before projection and must be retained.
        if (ToolStepWindows is not null || TranscriptDeferred) return WorkingPlan;
        if (PlanFrom(AssistantMessage) is { } current) return current;
        if (PreviousMessages is not null)
            for (var index = PreviousMessages.Count - 1; index >= 0; index--)
                if (PlanFrom(PreviousMessages[index]) is { } previous) return previous;
        return null;
    }

    private static JsonElement? PlanFrom(ChatMessage message)
    {
        if (message.ToolSteps is not { Count: > 0 } steps) return null;
        // Token/progress updates reuse the immutable tool list; do not scan or
        // reparse its historical receipts for every generated token.
        return WorkingPlans.GetValue(steps, static key =>
        {
            var source = (IReadOnlyList<AssistantToolStep>)key;
            for (var index = source.Count - 1; index >= 0; index--)
            {
                var step = source[index];
                if (step.Tool != "coding.updatePlan" || step.Status != "completed"
                    || string.IsNullOrWhiteSpace(step.OutputJson)) continue;
                try
                {
                    using var document = JsonDocument.Parse(step.OutputJson);
                    var output = document.RootElement;
                    if (output.ValueKind != JsonValueKind.Object
                        || output.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) continue;
                    return new(JsonSerializer.SerializeToElement(AssistantCoordinator.WorkingPlanDto(step), JsonOptions));
                }
                catch (JsonException) { }
            }
            return new(null);
        }).Value;
    }
}

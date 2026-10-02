using System.Text.Json;
using Missum.Core.Models;

namespace Missum.App.Services;

public sealed partial class MissumAiAssistantService
{
    private static readonly long MaximumContinuationMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>Accumulates active attempts while excluding the gap before an explicit continuation.</summary>
    internal static long ContinuationActiveMilliseconds(ChatMessage message, MissumAiRunRecord? run)
    {
        var end = message.UpdatedAt;
        if (run is not null && run.SessionId == message.SessionId && run.AssistantMessageId == message.Id
            && run.UpdatedAt != default && run.UpdatedAt < end) end = run.UpdatedAt;
        return ContinuationActiveMilliseconds(message.SessionId, message.Id, message.CreatedAt, end, message.ToolSteps);
    }

    internal static long ContinuationActiveMilliseconds(Guid sessionId, Guid messageId, DateTimeOffset createdAt,
        DateTimeOffset end, IEnumerable<AssistantToolStep>? steps)
    {
        if (createdAt == default) return 0;
        var start = createdAt;
        long prior = 0;
        foreach (var step in steps ?? [])
        {
            if (TryReadContinuationTiming(step, sessionId, messageId, out var receiptStart, out var receiptPrior)
                && receiptStart >= start)
            {
                start = receiptStart;
                prior = receiptPrior;
            }
        }
        var elapsed = end > start ? (end - start).Ticks / TimeSpan.TicksPerMillisecond : 0;
        return prior + Math.Min(elapsed, MaximumContinuationMilliseconds - prior);
    }

    internal static bool TryReadContinuationTiming(AssistantToolStep step, Guid sessionId, Guid messageId,
        out DateTimeOffset startedAt, out long priorMilliseconds)
    {
        startedAt = default;
        priorMilliseconds = 0;
        if (sessionId == Guid.Empty || messageId == Guid.Empty || step.Tool != ContinuationStepTool
            || step.Status is not ("running" or "completed") || step.StartedAt is not { } start || start == default
            || string.IsNullOrWhiteSpace(step.OutputJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(step.OutputJson);
            var output = document.RootElement;
            if (output.ValueKind != JsonValueKind.Object
                || !TryTimingOwner(output, "sessionId", out var session) || session != sessionId
                || !TryTimingOwner(output, "messageId", out var message) || message != messageId
                || !TryTimingOwner(output, "localRunId", out _)
                || !output.TryGetProperty("priorActiveMilliseconds", out var prior)
                || prior.ValueKind != JsonValueKind.Number || !prior.TryGetInt64(out var milliseconds)
                || milliseconds < 0 || milliseconds > MaximumContinuationMilliseconds) return false;
            startedAt = start;
            priorMilliseconds = milliseconds;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryTimingOwner(JsonElement output, string property, out Guid owner)
    {
        owner = Guid.Empty;
        return output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetGuid(out owner) && owner != Guid.Empty;
    }
}

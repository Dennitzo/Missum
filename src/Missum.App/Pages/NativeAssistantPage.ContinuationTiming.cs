using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private static TimeSpan MessageActiveDuration(JsonElement message, DateTimeOffset end)
    {
        var created = MessageCreatedAt(message);
        if (!Guid.TryParse(S(message, "sessionId"), out var sessionId) || !Guid.TryParse(S(message, "id"), out var messageId))
            return end > created ? end - created : TimeSpan.Zero;
        var steps = Items(message, "toolSteps").Select(step => new AssistantToolStep(
            S(step, "id"), S(step, "tool"), S(step, "status"), OutputJson: S(step, "outputJson"),
            StartedAt: DateTimeOffset.TryParse(S(step, "startedAt"), out var started) ? started : null));
        var milliseconds = MissumAiAssistantService.ContinuationActiveMilliseconds(sessionId, messageId, created, end, steps);
        return TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }
}

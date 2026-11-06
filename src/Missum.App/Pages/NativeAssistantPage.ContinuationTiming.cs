using System.Text;
using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    internal static string FormatMessageDuration(TimeSpan duration, bool afterSince)
    {
        if (duration < TimeSpan.FromSeconds(1)) return "0 Sekunden";

        // Working time may span several disjoint intervals. Use a fixed elapsed year of 365 days.
        const int daysPerYear = 365;
        var text = new StringBuilder();
        Append(duration.Days / daysPerYear, "Jahr", afterSince ? "Jahren" : "Jahre");
        Append(duration.Days % daysPerYear, "Tag", afterSince ? "Tagen" : "Tage");
        Append(duration.Hours, "Stunde", "Stunden");
        Append(duration.Minutes, "Minute", "Minuten");
        Append(duration.Seconds, "Sekunde", "Sekunden");
        return text.ToString();

        void Append(int count, string singular, string plural)
        {
            if (count == 0) return;
            if (text.Length > 0) text.Append(' ');
            text.Append(count).Append(' ').Append(count == 1 ? singular : plural);
        }
    }

    private TimeSpan MessageActiveDuration(JsonElement message, DateTimeOffset end)
    {
        var created = MessageCreatedAt(message);
        if (!Guid.TryParse(S(message, "sessionId"), out var sessionId) || !Guid.TryParse(S(message, "id"), out var messageId))
            return end > created ? end - created : TimeSpan.Zero;
        var steps = MessageTimingSteps(message)
            .Select(step => new AssistantToolStep(
            S(step, "id"), S(step, "tool"), S(step, "status"), OutputJson: S(step, "outputJson"),
            StartedAt: DateTimeOffset.TryParse(S(step, "startedAt"), out var started) ? started : null));
        var milliseconds = MissumAiAssistantService.ContinuationActiveMilliseconds(sessionId, messageId, created, end, steps);
        return TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }
}

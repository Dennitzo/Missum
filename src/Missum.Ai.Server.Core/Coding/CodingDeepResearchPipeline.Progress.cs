using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;

namespace Missum.Ai.Server.Core.Coding;

internal sealed record DeepResearchActivity(string Phase, string Status, string Title, string? StepId = null,
    string? Tool = null, string? Detail = null, string? Query = null, string? Url = null,
    IReadOnlyList<DeepResearchSource>? Sources = null, string? Text = null, string? ErrorCode = null);

internal static partial class CodingDeepResearchPipeline
{
    private static string? ActivityText(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string ActivityFailureStatus(Exception error) => error is RunSteeringBoundaryException ? "interrupted"
        : error is OperationCanceledException ? "cancelled" : "failed";

    private static string ModelActivityTitle(string? tool) => tool switch
    {
        PlanToolName => "Rechercheplan erstellen",
        SynthesisToolName => "Befunde aus Originalquellen zusammenführen",
        SkepticToolName => "Befunde kritisch prüfen",
        VerificationToolName => "Belege und offene Fragen bewerten",
        "web.fetch" => "Nächste Originalquelle auswählen",
        _ => "Recherchemodell",
    };

    private static DeepResearchSource[] ActivitySources(string tool, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return [];
        if (tool == "web.search" && value.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            return results.EnumerateArray().Take(6).Where(item => IsPublicHttpUrl(ActivityText(item, "url") ?? ""))
                .Select(item => new DeepResearchSource(null, Bound(ActivityText(item, "title") ?? "Quelle", 200),
                    ActivityText(item, "url")!, Bound(ActivityText(item, "snippet") ?? "", 500))).ToArray();
        if (tool == "web.fetch" && ActivityText(value, "url") is { } url && IsPublicHttpUrl(url))
            return [new(null, new Uri(url).Host, url, Bound(ActivityText(value, "preview") ?? "", 500))];
        return [];
    }

    private static string FormatToolActivity(string tool, AgentToolExecutionResult result)
    {
        if (!result.Succeeded) return Bound(result.ErrorMessage ?? result.ErrorCode ?? "Werkzeug fehlgeschlagen.", 2000);
        if (tool == "web.search")
            return Bound(string.Join("\n\n", ActivitySources(tool, result.Result).Select(source =>
                source.Title + "\n" + source.Url + (string.IsNullOrWhiteSpace(source.Snippet) ? "" : "\n" + source.Snippet))), 12000);
        if (result.Result.ValueKind == JsonValueKind.Object && result.Result.TryGetProperty("matches", out var matches)
            && matches.ValueKind == JsonValueKind.Array)
            return Bound(string.Join("\n\n", matches.EnumerateArray().Take(4).Select(item => ActivityText(item, "text") ?? "")), 12000);
        return Bound(ActivityText(result.Result, "preview") ?? result.Result.GetRawText(), 12000);
    }

    private static string FormatModelActivity(LmChatResult result)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Content)) text.AppendLine(result.Content);
        foreach (var call in result.ToolCalls)
        {
            var value = call.Arguments;
            if (value.ValueKind != JsonValueKind.Object) continue;
            foreach (var field in value.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in field.Value.EnumerateArray().Take(32))
                {
                    if (item.ValueKind == JsonValueKind.String) { text.AppendLine(item.GetString()); continue; }
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var main = ActivityText(item, "question") ?? ActivityText(item, "claim")
                        ?? ActivityText(item, "reason") ?? ActivityText(item, "method");
                    if (main is not null) text.AppendLine(main);
                    if (ActivityText(item, "query") is { } query) text.Append("Suchanfrage: ").AppendLine(query);
                    if (ActivityText(item, "status") is { } status) text.Append("Status: ").AppendLine(status);
                    if (ActivityText(item, "evidenceId") is { } evidence) text.Append("Beleg: ").AppendLine(evidence);
                }
            }
        }
        return Bound(text.ToString().Trim(), 32000);
    }
}

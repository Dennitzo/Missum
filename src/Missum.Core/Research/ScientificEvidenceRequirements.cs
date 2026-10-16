using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.Core.Research;

/// <summary>Required source/numerical evidence and optional Lean recommendations. No numerical run is a formal proof.</summary>
public static class ScientificEvidenceRequirements
{
    public static string? Validate(JsonElement value) => value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 3
        || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString() is not ("web" or "lean" or "simulation"))
        || value.EnumerateArray().Select(item => item.GetString()).Distinct(StringComparer.Ordinal).Count() != value.GetArrayLength()
        ? "evidenceRequirements muss eine eindeutige Liste aus web, lean und simulation sein. Lean ist eine Empfehlung, keine Abschlussvoraussetzung; Quellen und numerische Prüfungen bleiben eigenständig."
        : null;

    public static IReadOnlySet<string> For(ResearchWorkingItem item)
    {
        var data = item.Data;
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (data.TryGetProperty("evidenceRequirements", out var requested) && Validate(requested) is null)
            methods.UnionWith(requested.EnumerateArray().Select(value => value.GetString()!));
        // Legacy projects may still declare lean. Treat it as a recommendation without
        // rewriting their data or weakening source/numerical requirements.
        methods.Remove("lean");
        // Definitions and openly scoped hypotheses are not claims of a proved or measured result.
        var scoped = Text(data, "classification") is "definition" or "technical"
            || Text(data, "classification") == "hypothesis" && Text(data, "status") is "openLimit" or "unresolved" or "planned" or "pending" or "draft";
        if (scoped) return methods;
        var delegated = item.Kind == "section" && HasIds(data, "claimIds", "hypothesisIds");
        if (!delegated && item.Kind is "section" or "claim" or "hypothesis") methods.Add("web");
        var text = Text(data, "statement") + "\n" + Text(data, "contentMarkdown");
        var mathematical = ContainsSymbolicAssertion(text);
        if (data.TryGetProperty("quantitativeValidation", out _)
            || !delegated && mathematical && (HasIds(data, "units")
                || Regex.IsMatch(Text(data, "method") + " " + text, @"\b(?:physikalisch\w*|Simulation\w*|Dynamik|Bewegung|Kraft|Energie|Feldgleichung\w*)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))) methods.Add("simulation");
        return methods;
    }

    private static bool ContainsSymbolicAssertion(string text)
    {
        const RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        // Classify actual mathematical content, not prose such as "kein Beweis" or "dieser Satz".
        foreach (Match match in Regex.Matches(text, @"\$+([^$]+)\$+|\\\[([\s\S]*?)\\\]|\\\(([\s\S]*?)\\\)|\\begin\{(?:align\*?|equation\*?)\}([\s\S]*?)\\end\{(?:align\*?|equation\*?)\}", options, TimeSpan.FromSeconds(1)))
        {
            var expression = match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value.Trim();
            if (!Regex.IsMatch(expression, @"[=<>≤≥≠]|\\(?:leq?|geq?|neq|equiv|implies)\b", options, TimeSpan.FromSeconds(1))
                || !Regex.IsMatch(expression, @"\p{L}", options, TimeSpan.FromSeconds(1))) continue;
            // Reporting r=0.93, delta=9.44 degrees or t=2 seconds is a measurement/parameter, not a universal theorem.
            if (Regex.IsMatch(expression, @"^(?:\\[A-Za-z]+|[\p{L}][\p{L}0-9_]*)(?:_\{[^}]+\})?\s*(?:[=<>≤≥]|≈|\\(?:approx|leq?|geq?))\s*[+-]?\d+(?:[.,]\d+)?(?:[eE][+-]?\d+)?(?:\s*(?:°|%|[A-Za-zµ/²³^0-9{}]+|\\(?:mathrm|text)\{[^}]+\}))?\s*$",
                options, TimeSpan.FromSeconds(1))) continue;
            return true;
        }
        return false;
    }

    private static bool HasIds(JsonElement data, params string[] fields) => fields.Any(field => data.TryGetProperty(field, out var values)
        && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0);
    private static string Text(JsonElement data, string field) => data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

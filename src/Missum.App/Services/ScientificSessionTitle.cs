using System.Text.RegularExpressions;

namespace Missum.App.Services;

/// <summary>Derives a bounded research subject from user text, never from an AI answer.</summary>
internal static class ScientificSessionTitle
{
    internal const int MaximumLength = 160;
    private const string Fallback = "Wissenschaftliches Forschungsprojekt";
    private static readonly HashSet<string> Continuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weitermachen", "Weiter", "Fortsetzen", "Mach weiter", "Weiter machen", "Weiterarbeiten",
        "Continue", "Resume", "Go on", "Neue Sitzung", "Neuer Chat", "Hallo", "Antwort", "Frage",
    };
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    internal static string? FromPrompt(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var text = prompt.Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal)
            .ReplaceLineEndings("\n").Trim();
        text = Regex.Replace(text, @"(?m)^\s*#{1,6}\s+", "", Options, RegexTimeout);
        var normalized = Normalize(text);
        if (normalized.Length == 0 || Continuations.Contains(normalized)) return null;

        // Keep the topic following the request label, including when it begins
        // on the next line. Research instructions below that topic are not a name.
        text = Regex.Replace(text,
            @"^(?:Bitte\s+)?(?:Erstelle|Verfasse|Schreibe|Erarbeite)\s+(?:(?:mir|uns)\s+)?(?:ein(?:e[nr]?)?\s+)?(?:(?:wissenschaftliche[nr]?|ausführliche[nr]?|detaillierte[nr]?)\s+)*(?:Publikation|Forschungsarbeit|Forschungsprojekt|Studie|Abhandlung|Artikel)(?:\s+(?:zum\s+Thema|über|zu))?\s*[:\-–—]?\s*",
            "", Options, RegexTimeout);
        text = Regex.Replace(text,
            @"^(?:Please\s+)?(?:Create|Write|Prepare|Develop)\s+(?:a[n]?\s+)?(?:(?:scientific|detailed|research)\s+)*(?:publication|paper|article|study|research\s+project)(?:\s+(?:on|about))?\s*[:\-–—]?\s*",
            "", Options, RegexTimeout);
        text = Regex.Replace(text,
            @"^(?:Bitte\s+)?(?:Analysiere|Untersuche|Erkläre|Erforsche)\s+(?:(?:mir|uns)\s+)?",
            "", Options, RegexTimeout);
        var topic = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        var sentenceEnd = Regex.Match(topic, @"[.!?](?=\s+[\p{Lu}])", RegexOptions.CultureInvariant, RegexTimeout);
        if (sentenceEnd.Success) topic = topic[..sentenceEnd.Index];
        topic = Regex.Replace(topic,
            @"\s+(?:kombinieren|vereinen|verbinden|zusammenführen)(?=\s+(?:und|um|zu)\b|\s*[.!?]|$).*", "", Options, RegexTimeout);
        topic = Normalize(topic);
        if (topic.Length == 0) return Fallback;
        if (Continuations.Contains(topic)) return null;
        if (topic.Length <= MaximumLength) return topic;

        // A title may be concise, but it must never contain half of a word or
        // a broken Unicode character. Compact tabs perform their own ellipsis.
        var boundary = topic.LastIndexOf(' ', MaximumLength);
        return boundary > 0 ? topic[..boundary].TrimEnd(' ', ',', ':', ';', '-', '–', '—') : Fallback;
    }

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null,
        StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '\t', '`', '"', '\'', '.', '!', '?', ';', ':', ',', '-', '–', '—', '#');
}

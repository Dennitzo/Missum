using System.Text;
using System.Text.RegularExpressions;

namespace Missum.App.Services;

public sealed partial class ScientificPublicationService
{
    internal static string PreparePublicationProse(string markdown)
    {
        // Existing explanations remain authoritative. Raw formal syntax is a project artifact,
        // not a readable proof explanation; do not invent an interpretation or claim validity.
        markdown = Regex.Replace(markdown, @"(?m)^(?<label>\*\*Missum[^\r\n]*?)(?: · Revision \d+)", "${label}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        markdown = Regex.Replace(markdown, @"(?m)^(?<label>Arbeitsfassung|Geprüfter Forschungsstand|Forschungsbericht) · Revision \d+", "${label}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var lines = markdown.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            var opening = Regex.Match(lines[index], @"^[ ]{0,3}(?<fence>`{3,}|~{3,})(?<language>[^\s]*)[^\r\n]*$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!opening.Success) { result.AppendLine(lines[index]); continue; }
            var marker = opening.Groups["fence"].Value;
            var end = index + 1;
            for (; end < lines.Length; end++)
            {
                var trimmed = lines[end].Trim();
                if (trimmed.Length >= marker.Length && trimmed.All(character => character == marker[0])) break;
            }
            var language = opening.Groups["language"].Value;
            if (language.Equals("lean", StringComparison.OrdinalIgnoreCase) || language.Equals("lean4", StringComparison.OrdinalIgnoreCase))
                result.AppendLine("> Redaktioneller Hinweis: Der hier im Manuskript enthaltene Lean-Quelltext ist als mathematischer Beweis mit Voraussetzungen, nachvollziehbaren Zwischenschritten und Schlussfolgerung auszuarbeiten.").AppendLine();
            else if (end >= lines.Length) { result.AppendLine(string.Join('\n', lines[index..])); break; }
            else result.AppendLine(string.Join('\n', lines[index..(end + 1)]));
            index = end;
        }
        return result.ToString();
    }
}

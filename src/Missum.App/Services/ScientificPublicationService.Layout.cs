using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

public sealed partial class ScientificPublicationService
{
    internal static ResearchWorkingItem[] OrderedPublicationSections(ResearchWorkingState state)
    {
        var sections = state.Items.Where(item => item.Kind == "section" && item.OwnerAgentId is null
            && Text(item.Data, "status") != "withdrawn" && !string.IsNullOrWhiteSpace(Text(item.Data, "contentMarkdown"))).ToArray();
        if (sections.Length < 2) return sections;
        var missing = sections.Where(item => !ExplicitSectionOrder(item).HasValue).ToArray();
        var duplicateOrders = sections.Where(item => ExplicitSectionOrder(item).HasValue)
            .GroupBy(item => ExplicitSectionOrder(item)!.Value).Where(group => group.Count() > 1).SelectMany(group => group).ToArray();
        if (missing.Length == 0 && duplicateOrders.Length == 0)
            return sections.OrderBy(item => ExplicitSectionOrder(item)!.Value).ToArray();
        // Old manuscripts sometimes used the numbered heading as their order and
        // left every order at zero. Use natural numbers in that case, never the
        // lexical order in which "10" precedes "1". Unnumbered semantic outlines
        // retain the explicitly authored order and are not alphabetized.
        if (sections.All(item => HeadingNumber(Text(item.Data, "title")) is not null))
            return sections.OrderBy(item => HeadingNumber(Text(item.Data, "title")), PublicationNumberComparer.Instance)
                .ThenBy(item => Number(item.Data, "order")).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
        var affected = missing.Concat(duplicateOrders).DistinctBy(item => item.Id).ToArray();
        throw new ScientificPublicationContentException("Die fachliche Abschnittsreihenfolge ist nicht eindeutig: "
            + string.Join(", ", affected.Select(item => item.Id))
            + ". Ordne neue Themen fachlich in die vorhandene Gliederung ein und setze für alle betroffenen Abschnitte eindeutige numerische order-Werte mit research.update; fehlende Werte gelten nicht als erste Position.",
            state: state, sections: affected.Select(item => new ScientificPresentationSectionFailure(item.Id, item.Revision)).ToArray());
    }

    private static double? ExplicitSectionOrder(ResearchWorkingItem item) => item.Data.TryGetProperty("order", out var order)
        && order.ValueKind == JsonValueKind.Number && order.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;

    private static void AppendOutlineMetadata(StringBuilder text, ResearchWorkingItem[] sections)
    {
        // Only a complete historical numbered outline can designate removable
        // chapter prefixes. A title such as "3 Dimensionen" remains scientific
        // text; arbitrary leading numbers are not interpreted by the renderer.
        if (sections.Length < 2 || sections.Any(item => HeadingNumber(Text(item.Data, "title")) is null)
            || !sections.Any(item => HeadingNumber(Text(item.Data, "title"))!.Parts is [1])) return;
        var titles = sections.Select(item => OneLine(Text(item.Data, "title"))).ToArray();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(titles)));
        text.Append("<!-- MISSUM_PUBLICATION_OUTLINE:").Append(payload).AppendLine(" -->");
    }

    private sealed record PublicationHeadingNumber(int[] Parts, string Suffix);

    private static PublicationHeadingNumber? HeadingNumber(string title)
    {
        var match = Regex.Match(title, @"^\s*(\d+(?:\.\d+)*)([a-z]?)(?:[.)])?\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success) return null;
        var result = new List<int>();
        foreach (var number in match.Groups[1].Value.Split('.'))
        {
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            result.Add(value);
        }
        return new(result.ToArray(), match.Groups[2].Value);
    }

    internal static string StripHeadingNumber(string title) => Regex.Replace(title, @"^\s*\d+[a-z]?(?:\.\d+[a-z]?)*[.)]?\s+", "",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();

    private sealed class PublicationNumberComparer : IComparer<PublicationHeadingNumber?>
    {
        internal static PublicationNumberComparer Instance { get; } = new();
        public int Compare(PublicationHeadingNumber? left, PublicationHeadingNumber? right)
        {
            if (left is null || right is null) return left is null ? right is null ? 0 : -1 : 1;
            for (var index = 0; index < Math.Min(left.Parts.Length, right.Parts.Length); index++)
                if (left.Parts[index] != right.Parts[index]) return left.Parts[index].CompareTo(right.Parts[index]);
            var length = left.Parts.Length.CompareTo(right.Parts.Length);
            return length != 0 ? length : StringComparer.Ordinal.Compare(left.Suffix, right.Suffix);
        }
    }

    private static void AppendMeasuredPlots(StringBuilder text, IEnumerable<(string ExperimentId, string Path, string Hash)> candidates,
        HashSet<string> usedFigures, bool includeHeading)
    {
        var headingWritten = false;
        foreach (var image in SelectMeasuredPlots(candidates, usedFigures))
        {
            if (!headingWritten)
            {
                if (includeHeading) text.AppendLine("## Simulationsabbildungen").AppendLine();
                text.AppendLine("Die folgenden Plots stammen aus erfolgreich protokollierten Forschungsausführungen. Sie illustrieren das jeweilige Modell; sie sind für sich allein kein Beweis.").AppendLine();
                headingWritten = true;
            }
            var label = Path.GetFileNameWithoutExtension(image.Path).Replace('_', ' ').Replace('-', ' ');
            text.Append("![").Append(EscapeLabel(label)).Append(" · Experiment ").Append(EscapeLabel(image.ExperimentId))
                .Append("](").Append(image.Path.Replace(" ", "%20", StringComparison.Ordinal)).AppendLine(")").AppendLine();
        }
    }

    private static (string ExperimentId, string Path, string Hash)[] LatestMeasuredPlots(IEnumerable<ResearchExperiment> experiments)
    {
        var selected = new Dictionary<string, (string ExperimentId, string Path, string Hash)>(StringComparer.Ordinal);
        foreach (var experiment in experiments.OrderByDescending(item => item.UpdatedAt).ThenBy(item => item.Id, StringComparer.Ordinal))
        foreach (var image in MeasuredImageHashes(experiment).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            selected.TryAdd(image.Key, (experiment.Id, image.Key, image.Value));
        return selected.Values.ToArray();
    }

    private static IEnumerable<(string ExperimentId, string Path, string Hash)> SelectMeasuredPlots(
        IEnumerable<(string ExperimentId, string Path, string Hash)> candidates, HashSet<string> usedFigures)
    {
        foreach (var image in candidates)
        {
            // The same artifact can occur in several execution receipts. The
            // newest receipt wins; no arbitrary workspace image is discovered.
            if (!usedFigures.Add(image.Path)) continue;
            if (usedFigures.Count > ScientificPublicationImages.MaximumImages)
                throw new ScientificPublicationContentException($"Die Publikation enthält mehr als {ScientificPublicationImages.MaximumImages} verschiedene Simulationsabbildungen. Teile die Abbildungen fachlich in zusätzliche Publikationen auf; keine erforderliche Abbildung wurde still ausgelassen.");
            yield return image;
        }
    }

    private static IEnumerable<string> AutomaticPlotHashes(ResearchWorkingState state, ResearchResultSnapshot results)
    {
        var sections = OrderedPublicationSections(state);
        var used = DeclaredPlotPaths(sections);
        var latest = LatestMeasuredPlots(results.Experiments.Where(item => item.ProjectId == state.ProjectId));
        foreach (var section in sections)
        {
            foreach (var image in SelectMeasuredPlots(latest.Where(item => Ids(section.Data, "experimentIds").Contains(item.ExperimentId)), used))
                yield return image.Hash;
        }
        foreach (var image in SelectMeasuredPlots(latest, used)) yield return image.Hash;
    }

    private static Dictionary<string, string> CanonicalImageHashes(PublicationSnapshot snapshot)
    {
        var experiments = snapshot.Results.Experiments.Where(item => item.ProjectId == snapshot.Project.Id).ToArray();
        var hashes = LatestMeasuredPlots(experiments).ToDictionary(item => item.Path, item => item.Hash, StringComparer.Ordinal);
        // An explicitly referenced older receipt must still match that receipt,
        // even when a newer execution overwrote the same work/artifact filename.
        foreach (var section in OrderedPublicationSections(snapshot.WorkingState!))
            if (section.Data.TryGetProperty("figureCaptions", out var figures) && figures.ValueKind == JsonValueKind.Array)
                foreach (var figure in figures.EnumerateArray())
                {
                    var experiment = experiments.FirstOrDefault(item => item.Id == Text(figure, "experimentId"));
                    var path = Text(figure, "artifactPath").Replace('\\', '/');
                    if (experiment is not null && MeasuredImageHashes(experiment).TryGetValue(path, out var hash)) hashes[path] = hash;
                }
        return hashes;
    }

    private static HashSet<string> DeclaredPlotPaths(IEnumerable<ResearchWorkingItem> sections)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in sections)
            if (section.Data.TryGetProperty("figureCaptions", out var captions) && captions.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var caption in captions.EnumerateArray()) paths.Add(Text(caption, "artifactPath").Replace('\\', '/'));
        return paths;
    }
}

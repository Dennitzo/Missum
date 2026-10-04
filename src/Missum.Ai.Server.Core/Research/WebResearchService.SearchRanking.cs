using Missum.Ai.Contracts;
using System.Text.RegularExpressions;

namespace Missum.Ai.Server.Core.Research;

public sealed partial class WebResearchService
{
    private static readonly Regex SearchWords = new(@"[\p{L}\p{N}]+",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SearchOperators = new(@"(?<!\w)(?:site|filetype|inurl|intitle|after|before):\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SearchExcludedWords = new(@"(?<!\w)-[\p{L}\p{N}]+",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex SearchDoiPath = new(@"(?:^|/)(?<doi>10\.\d{4,9}/[^?#\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> SearchStopWords = new(
        ["the", "and", "for", "with", "from", "into", "that", "this", "how", "what", "which", "per", "are", "was", "can",
         "der", "die", "das", "und", "für", "fuer", "mit", "von", "zum", "zur", "den", "dem", "des", "ein", "eine", "einer",
         "eines", "einem", "ist", "sind", "wie", "was", "auf", "aus", "bei", "nach", "oder"], StringComparer.OrdinalIgnoreCase);

    internal static List<WebSearchResult> RankSearchResults(string query, IReadOnlyList<WebSearchResult> results, int maximum)
    {
        var queryWords = SearchWordSet(SearchExcludedWords.Replace(SearchOperators.Replace(query, " "), " "));
        // This is a lexical ordering hint, not a scientific truth/authority
        // assessment. Never discard a result merely for weak term coverage.
        var ranked = results.Select((result, index) =>
        {
            var titleWords = SearchWordSet(result.Title.Length > 8_000 ? result.Title[..8_000] : result.Title);
            var snippet = result.Snippet ?? string.Empty;
            var snippetWords = SearchWordSet(snippet.Length > 16_000 ? snippet[..16_000] : snippet);
            return new
            {
                Result = result,
                Index = index,
                Coverage = queryWords.Count(word => titleWords.Contains(word) || snippetWords.Contains(word)),
                TitleCoverage = queryWords.Count(titleWords.Contains),
            };
        }).OrderByDescending(static item => item.Coverage)
            .ThenByDescending(static item => item.TitleCoverage)
            // Preserve the existing engine preference only for equally covered
            // results. A precise Bing hit must survive an unrelated CSE hit.
            .ThenBy(static item => item.Result.Source?.Equals("bing", StringComparison.OrdinalIgnoreCase) == true ? 1 : 0)
            .ThenBy(static item => item.Index);

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var selected = new List<WebSearchResult>();
        foreach (var item in ranked)
        {
            if (!identities.Add(SearchResultIdentity(item.Result.Url))) continue;
            selected.Add(item.Result);
            if (selected.Count == maximum) break;
        }
        return selected;
    }

    private static HashSet<string> SearchWordSet(string text) => SearchWords.Matches(text)
        .Select(static match => match.Value)
        .Where(static word => word.Length >= 3 && word.Any(char.IsLetter) && !SearchStopWords.Contains(word))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string SearchResultIdentity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "url:" + url;
        // DOI resolver, /article/10... and /doi/(abs|full|pdf)/10... all
        // identify the same work. Abstract references are deliberately ignored:
        // mentioning another work's DOI does not make the result that work.
        var doi = SearchDoiPath.Match(Uri.UnescapeDataString(uri.AbsolutePath));
        if (doi.Success) return "doi:" + doi.Groups["doi"].Value.TrimEnd('/').ToLowerInvariant();
        var canonical = new UriBuilder(uri) { Fragment = string.Empty };
        return "url:" + (string.IsNullOrEmpty(uri.Query) ? canonical.Uri.AbsoluteUri.TrimEnd('/') : canonical.Uri.AbsoluteUri);
    }
}

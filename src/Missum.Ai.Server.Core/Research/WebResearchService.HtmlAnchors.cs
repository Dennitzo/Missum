using System.Net;
using System.Text.RegularExpressions;

namespace Missum.Ai.Server.Core.Research;

public sealed partial class WebResearchService
{
    internal readonly record struct FetchedTargetContent(string Content, string? FragmentStatus);
    private static readonly HashSet<string> HtmlVoidElements = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr" };
    private static readonly HashSet<string> HtmlAnchorBlocks = new(StringComparer.OrdinalIgnoreCase)
        { "section", "article", "div", "dl", "dd", "p", "li", "pre", "tr", "table" };

    internal static FetchedTargetContent ExtractFetchedContentForTarget(byte[] bytes, string mediaType, string? charset,
        string? fragment, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fragment) || fragment == "#")
            return new(ExtractFetchedContent(bytes, mediaType, charset, cancellationToken), null);
        if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            return new(ExtractFetchedContent(bytes, mediaType, charset, cancellationToken), "unsupported");
        var html = ScriptAndStyleRegex().Replace(DecodeContent(bytes, charset), " ");
        var anchor = Uri.UnescapeDataString(fragment.TrimStart('#'));
        var range = FindHtmlAnchorRange(html, anchor, cancellationToken);
        if (range is { } selected)
        {
            var text = NormalizeHtml(html[selected.Start..selected.End]);
            if (!string.IsNullOrWhiteSpace(text)) return new(text, "resolved");
        }
        // The diagnostic stays outside the source text. A failed target must
        // never manufacture an excerpt or silently claim the section was read.
        return new(NormalizeHtml(html), "not_found");
    }

    private static (int Start, int End)? FindHtmlAnchorRange(string html, string anchor, CancellationToken cancellationToken)
    {
        var stack = new List<(string Name, int Start)>();
        foreach (Match token in HtmlTagRegex().Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = HtmlAnchorTagRegex().Match(token.Value);
            if (!tag.Success) continue;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            if (tag.Groups["closing"].Length > 0)
            {
                var index = stack.FindLastIndex(element => element.Name == name);
                if (index >= 0) stack.RemoveRange(index, stack.Count - index);
                continue;
            }
            if (HtmlAnchorAttributeRegex().Matches(token.Value).Any(attribute =>
                (string.Equals(attribute.Groups["attribute"].Value, "id", StringComparison.OrdinalIgnoreCase)
                    || name == "a" && string.Equals(attribute.Groups["attribute"].Value, "name", StringComparison.OrdinalIgnoreCase))
                && string.Equals(WebUtility.HtmlDecode(attribute.Groups["value"].Value), anchor, StringComparison.Ordinal)))
            {
                if (name == "dt")
                {
                    if (FindHtmlElementEnd(html, token.Index, name) is not { } end) return null;
                    // Sphinx places the definition text in the sibling dd.
                    // Do not include another API's following dt/dd pair.
                    var next = HtmlTagRegex().Match(html, end);
                    var nextTag = HtmlAnchorTagRegex().Match(next.Value);
                    while (next.Success && !nextTag.Success)
                    {
                        next = next.NextMatch();
                        nextTag = HtmlAnchorTagRegex().Match(next.Value);
                    }
                    if (nextTag.Success && nextTag.Groups["closing"].Length == 0
                        && string.Equals(nextTag.Groups["name"].Value, "dd", StringComparison.OrdinalIgnoreCase))
                    {
                        if (FindHtmlElementEnd(html, next.Index, "dd") is not { } bodyEnd) return null;
                        end = bodyEnd;
                    }
                    return (token.Index, end);
                }
                if (HtmlHeadingLevel(name) > 0)
                    return FindHtmlElementEnd(html, token.Index, name) is { } headingEnd
                        ? (token.Index, FindHtmlHeadingEnd(html, headingEnd, HtmlHeadingLevel(name))) : null;
                if (HtmlAnchorBlocks.Contains(name))
                    return FindHtmlElementEnd(html, token.Index, name) is { } blockEnd ? (token.Index, blockEnd) : null;
                var heading = stack.LastOrDefault(element => HtmlHeadingLevel(element.Name) > 0);
                if (heading.Name is not null)
                    return FindHtmlElementEnd(html, heading.Start, heading.Name) is { } containingHeadingEnd
                        ? (heading.Start, FindHtmlHeadingEnd(html, containingHeadingEnd, HtmlHeadingLevel(heading.Name))) : null;
                var block = stack.LastOrDefault(element => HtmlAnchorBlocks.Contains(element.Name));
                if (block.Name is not null && FindHtmlElementEnd(html, block.Start, block.Name) is { } parentEnd)
                    return (block.Start, parentEnd);
                // Older manuals place an empty named anchor immediately before
                // its heading, rather than on a containing section element.
                if (name == "a")
                    foreach (Match following in HtmlTagRegex().Matches(html, token.Index + token.Length))
                    {
                        var followingTag = HtmlAnchorTagRegex().Match(following.Value);
                        if (!followingTag.Success || followingTag.Groups["closing"].Length > 0) continue;
                        var followingName = followingTag.Groups["name"].Value;
                        if (HtmlVoidElements.Contains(followingName)) continue;
                        var followingLevel = HtmlHeadingLevel(followingName);
                        return followingLevel > 0 && FindHtmlElementEnd(html, following.Index, followingName) is { } followingHeadingEnd
                            ? (token.Index, FindHtmlHeadingEnd(html, followingHeadingEnd, followingLevel)) : null;
                    }
                return null;
            }
            if (!HtmlVoidElements.Contains(name) && !token.Value.EndsWith("/>", StringComparison.Ordinal))
            {
                if (stack.Count >= 2_048) return null;
                stack.Add((name, token.Index));
            }
        }
        return null;
    }

    private static int? FindHtmlElementEnd(string html, int start, string name)
    {
        var depth = 0;
        foreach (Match token in HtmlTagRegex().Matches(html, start))
        {
            var tag = HtmlAnchorTagRegex().Match(token.Value);
            if (!tag.Success || !string.Equals(tag.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
            depth += tag.Groups["closing"].Length == 0 ? 1 : -1;
            if (depth == 0) return token.Index + token.Length;
        }
        return null;
    }

    private static int FindHtmlHeadingEnd(string html, int start, int level)
    {
        var nestedSections = 0;
        foreach (Match token in HtmlTagRegex().Matches(html, start))
        {
            var tag = HtmlAnchorTagRegex().Match(token.Value);
            if (!tag.Success) continue;
            var name = tag.Groups["name"].Value;
            if (string.Equals(name, "section", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "article", StringComparison.OrdinalIgnoreCase))
            {
                if (tag.Groups["closing"].Length == 0) nestedSections++;
                else if (nestedSections == 0) return token.Index;
                else nestedSections--;
            }
            var nextLevel = HtmlHeadingLevel(name);
            if (tag.Groups["closing"].Length == 0 && nextLevel > 0 && nextLevel <= level) return token.Index;
        }
        return html.Length;
    }

    private static int HtmlHeadingLevel(string name) => name.Length == 2 && name[0] is 'h' or 'H' && name[1] is >= '1' and <= '6' ? name[1] - '0' : 0;

    [GeneratedRegex(@"^<\s*(?<closing>/?)\s*(?<name>[A-Za-z][A-Za-z0-9:-]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlAnchorTagRegex();

    // Consume complete attributes, including unrelated quoted values, so a
    // string such as title="see id='other'" cannot impersonate an HTML ID.
    [GeneratedRegex("(?<attribute>[^\\s\"'<>/=]+)(?:\\s*=\\s*(?:\"(?<value>[^\"]*)\"|'(?<value>[^']*)'|(?<value>[^\\s>]+)))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlAnchorAttributeRegex();
}

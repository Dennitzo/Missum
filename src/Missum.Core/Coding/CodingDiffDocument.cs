using System.Globalization;
using System.Text.RegularExpressions;

namespace Missum.Core.Coding;

public enum CodingDiffLineKind { Metadata, Context, Added, Removed }
public sealed record CodingDiffLine(CodingDiffLineKind Kind, string Text, int? OldLine, int? NewLine, string Path);

/// <summary>Parses off the UI thread; presentation materializes only a bounded page of rows.</summary>
public sealed partial class CodingDiffDocument
{
    public const int PageSize = 300;
    private readonly CodingDiffLine[] _lines;
    private CodingDiffDocument(CodingDiffLine[] lines) => _lines = lines;
    public int LineCount => _lines.Length;
    public int PageCount => Math.Max(1, (_lines.Length + PageSize - 1) / PageSize);
    public IReadOnlyList<CodingDiffLine> Page(int page) => _lines.Skip(Math.Clamp(page, 0, PageCount - 1) * PageSize).Take(PageSize).ToArray();

    public static CodingDiffDocument Parse(string diff, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var result = new List<CodingDiffLine>();
        int? oldLine = null, newLine = null;
        var oldRemaining = 0;
        var newRemaining = 0;
        var path = "";
        var lines = diff.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = lines[index];
            if (line.Length == 0 && index == lines.Length - 1) continue;
            var hunk = HunkHeader().Match(line);
            if (hunk.Success)
            {
                oldLine = Number(hunk.Groups[1].Value); newLine = Number(hunk.Groups[3].Value);
                oldRemaining = hunk.Groups[2].Success ? Number(hunk.Groups[2].Value) ?? 0 : 1;
                newRemaining = hunk.Groups[4].Success ? Number(hunk.Groups[4].Value) ?? 0 : 1;
                result.Add(new(CodingDiffLineKind.Metadata, line, null, null, path));
                continue;
            }
            var insideHunk = oldRemaining > 0 || newRemaining > 0;
            if (line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
                || (!insideHunk && (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))))
            {
                if (line.StartsWith("diff ", StringComparison.Ordinal)) path = "";
                else if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var candidate = line[4..].Split('\t')[0].Trim('"');
                    if (candidate != "/dev/null") path = candidate;
                }
                oldLine = newLine = null; oldRemaining = newRemaining = 0;
                result.Add(new(CodingDiffLineKind.Metadata, line, null, null, path));
            }
            else if (line.StartsWith('+'))
            {
                result.Add(new(CodingDiffLineKind.Added, line[1..], null, insideHunk ? newLine : null, path));
                if (newLine.HasValue) newLine++; newRemaining = Math.Max(0, newRemaining - 1);
            }
            else if (line.StartsWith('-'))
            {
                result.Add(new(CodingDiffLineKind.Removed, line[1..], insideHunk ? oldLine : null, null, path));
                if (oldLine.HasValue) oldLine++; oldRemaining = Math.Max(0, oldRemaining - 1);
            }
            else if (line.StartsWith(' '))
            {
                result.Add(new(CodingDiffLineKind.Context, line[1..], insideHunk ? oldLine : null, insideHunk ? newLine : null, path));
                if (oldLine.HasValue) oldLine++; if (newLine.HasValue) newLine++;
                oldRemaining = Math.Max(0, oldRemaining - 1); newRemaining = Math.Max(0, newRemaining - 1);
            }
            else result.Add(new(CodingDiffLineKind.Metadata, line, null, null, path));
        }
        return new(result.ToArray());
    }

    private static int? Number(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    [GeneratedRegex(@"^@@\s+-(\d+)(?:,(\d+))?\s+\+(\d+)(?:,(\d+))?\s+@@", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}

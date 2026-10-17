using System.Text;

namespace Missum.App.Controls;

/// <summary>Lossless source blocks; completed blocks are immutable while a tail streams.</summary>
internal sealed class NativeTextBlockIndex
{
    internal sealed record Block(int Start, string Source);
    private const int TargetCharacters = 2048;
    private string _source = "";
    private readonly List<Block> _blocks = [];
    internal IReadOnlyList<Block> Blocks => _blocks;

    internal void Update(string source)
    {
        if (ReferenceEquals(source, _source) || source == _source) return;
        var append = source.StartsWith(_source, StringComparison.Ordinal);
        var start = append && _blocks.Count > 0 ? _blocks[^1].Start : 0;
        if (append && _blocks.Count > 0) _blocks.RemoveAt(_blocks.Count - 1);
        else _blocks.Clear();
        foreach (var block in Split(source[start..], start)) _blocks.Add(block);
        _source = source;
    }

    private static IEnumerable<Block> Split(string source, int origin)
    {
        var builder = new StringBuilder();
        var start = origin;
        var fence = '\0';
        var lineStart = true;
        var openTail = false;
        foreach (var segment in NativeMathSyntax.Split(source))
        {
            if (segment.IsMath)
            {
                // A complete formula remains atomic, including original delimiters.
                builder.Append(segment.Text);
                lineStart = segment.Text.EndsWith('\n');
                continue;
            }
            var text = segment.Text;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (lineStart && (character is '`' or '~') && index + 2 < text.Length
                    && text[index + 1] == character && text[index + 2] == character)
                    fence = fence == character ? '\0' : fence == '\0' ? character : fence;
                builder.Append(character);
                lineStart = character == '\n' || lineStart && character is ' ' or '\t';
                var paragraphEnd = character == '\n' && index + 1 < text.Length && text[index + 1] == '\n';
                if (openTail || fence != '\0' || !paragraphEnd && builder.Length < TargetCharacters || !char.IsWhiteSpace(character)) continue;
                // Do not split an open explicit formula or Markdown code span.
                // Split() reports incomplete formula tails as literal: retain
                // their current paragraph until it closes or generation ends.
                var candidate = builder.ToString();
                if (HasOpenDelimiter(candidate)) { openTail = true; continue; }
                yield return new Block(start, candidate);
                start += candidate.Length;
                builder.Clear();
            }
        }
        if (builder.Length > 0) yield return new Block(start, builder.ToString());
    }

    private static bool HasOpenDelimiter(string source)
    {
        var display = false;
        var inline = false;
        var round = 0;
        var square = 0;
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                switch (source[index + 1])
                {
                    case '(': round++; break;
                    case ')': round = Math.Max(0, round - 1); break;
                    case '[': square++; break;
                    case ']': square = Math.Max(0, square - 1); break;
                }
                index++; continue;
            }
            if (source[index] != '$') continue;
            if (index + 1 < source.Length && source[index + 1] == '$') { display = !display; index++; }
            else if (!display) inline = !inline;
        }
        return display || inline || round > 0 || square > 0;
    }
}

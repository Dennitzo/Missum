namespace Missum.App.Controls;

/// <summary>A lossless Markdown fragment. Math text includes its original delimiters.</summary>
public readonly record struct MathSegment(string Text, bool IsMath, bool Display)
{
    /// <summary>Optional presentation for conservatively recognized legacy notation; Text remains lossless.</summary>
    public string? RenderLatex { get; init; }
}

/// <summary>
/// Recognizes the same inline and display delimiters as the Go-WinUI chat renderer.
/// Parsing is deliberately separate from typesetting: incomplete streaming input remains readable.
/// </summary>
public static class NativeMathSyntax
{
    public static bool ContainsMath(string text) => Split(text).Any(static segment => segment.IsMath);

    public static IReadOnlyList<MathSegment> Split(string text) => SplitCore(text, recognizeLegacy: false);

    internal static IReadOnlyList<MathSegment> SplitForRendering(string text) => SplitCore(text, recognizeLegacy: true);

    private static List<MathSegment> SplitCore(string text, bool recognizeLegacy)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<MathSegment> result = [];
        var plainStart = 0;
        var index = 0;
        var legacyBlockedUntil = 0;
        while (index < text.Length)
        {
            // Protect Markdown code before looking for TeX. An unfinished code block is also
            // protected so streamed source code never briefly turns into a formula.
            if ((text[index] is '`' or '~') && IsFenceStart(text, index, out var fenceLength))
            {
                index = EndOfFence(text, index, fenceLength);
                continue;
            }
            if (text[index] == '`' && !IsEscaped(text, index))
            {
                index = EndOfCodeSpan(text, index);
                continue;
            }

            if (recognizeLegacy && NativeLooseMath.TrySkipLiteral(text, index, out var literalEnd))
            {
                index = literalEnd;
                continue;
            }

            if (!TryDelimiter(text, index, out var opening, out var closing, out var display))
            {
                var legacyEnd = index;
                if (recognizeLegacy && index >= legacyBlockedUntil
                    && NativeLooseMath.TryRead(text, index, out var legacy, out legacyEnd))
                {
                    if (index > plainStart) result.Add(new(text[plainStart..index], false, false));
                    result.Add(legacy);
                    index = plainStart = legacyEnd;
                    continue;
                }
                // A malformed legacy expression must not hide later explicit TeX on this line.
                if (recognizeLegacy && legacyEnd > index) legacyBlockedUntil = legacyEnd;
                index++;
                continue;
            }

            var closeIndex = FindClosing(text, index + opening.Length, closing, display);
            if (closeIndex < 0)
            {
                if (recognizeLegacy) legacyBlockedUntil = EndOfLine(text, index + opening.Length);
                // An unfinished display block is one literal fragment, including any inner
                // single dollars. A possible price must not hide a later explicit formula.
                index = display ? text.Length : opening == "$" && char.IsDigit(text[index + 1])
                    ? index + 1 : EndOfLine(text, index + opening.Length);
                continue;
            }
            var body = text.AsSpan(index + opening.Length, closeIndex - index - opening.Length);
            if (body.IsWhiteSpace() || (opening == "$" && !IsInlineDollarBody(body, text, closeIndex)))
            {
                if (recognizeLegacy) legacyBlockedUntil = EndOfLine(text, index + opening.Length);
                index += opening.Length;
                continue;
            }

            if (index > plainStart) result.Add(new(text[plainStart..index], false, false));
            var mathStart = index;
            index = closeIndex + closing.Length;
            result.Add(new(text[mathStart..index], true, display));
            plainStart = index;
        }
        if (plainStart < text.Length) result.Add(new(text[plainStart..], false, false));
        return result;
    }

    private static bool TryDelimiter(string text, int index, out string opening, out string closing, out bool display)
    {
        opening = closing = string.Empty;
        display = false;
        if (IsEscaped(text, index)) return false;
        if (text[index] == '\\' && index + 1 < text.Length && text[index + 1] is '[' or '(')
        {
            display = text[index + 1] == '[';
            opening = display ? @"\[" : @"\(";
            closing = display ? @"\]" : @"\)";
            return true;
        }
        if (text[index] != '$') return false;
        if (index + 1 < text.Length && text[index + 1] == '$')
        {
            opening = closing = "$$";
            display = true;
            return true;
        }
        // Whitespace next to a single-dollar opener is prose/currency, not inline math.
        if (index + 1 >= text.Length || char.IsWhiteSpace(text[index + 1])) return false;
        opening = closing = "$";
        return true;
    }

    private static int FindClosing(string text, int start, string delimiter, bool display)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (!display && text[index] is '\r' or '\n') return -1;
            if (text[index] == '`' && !IsEscaped(text, index)) return -1;
            if (!text.AsSpan(index).StartsWith(delimiter, StringComparison.Ordinal) || IsEscaped(text, index)) continue;
            if (delimiter == "$" && ((index > 0 && text[index - 1] == '$') || (index + 1 < text.Length && text[index + 1] == '$')))
                continue;
            return index;
        }
        return -1;
    }

    private static bool IsInlineDollarBody(ReadOnlySpan<char> body, string text, int closing)
    {
        if (char.IsWhiteSpace(body[0]) || char.IsWhiteSpace(body[^1])) return false;
        // '$5–$10' and '$5 and $10' are prices. A closing delimiter followed by a
        // digit belongs to the next price; explicit numeric formulas such as '$5$' remain valid.
        return closing + 1 >= text.Length || !char.IsDigit(text[closing + 1]);
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        for (var cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--) slashes++;
        return slashes % 2 == 1;
    }

    private static bool IsFenceStart(string text, int index, out int length)
    {
        length = RunLength(text, index, text[index]);
        if (length < 3 || !HasFenceIndent(text, index)) return false;
        // CommonMark does not allow backticks in a backtick fence's information string.
        if (text[index] == '`')
        {
            var lineEnd = EndOfLine(text, index + length);
            if (text.AsSpan(index + length, lineEnd - index - length).Contains('`')) return false;
        }
        return true;
    }

    private static bool HasFenceIndent(string text, int index)
    {
        var cursor = index - 1;
        var spaces = 0;
        while (cursor >= 0 && text[cursor] != '\n' && text[cursor] != '\r')
        {
            if (text[cursor--] != ' ' || ++spaces > 3) return false;
        }
        return true;
    }

    private static int EndOfFence(string text, int start, int fenceLength)
    {
        var marker = text[start];
        var cursor = EndOfLine(text, start + fenceLength);
        while (cursor < text.Length)
        {
            if (text[cursor] is '\r' or '\n') { cursor++; continue; }
            var lineEnd = EndOfLine(text, cursor);
            var markerStart = cursor;
            while (markerStart < lineEnd && text[markerStart] == ' ') markerStart++;
            if (markerStart < lineEnd && markerStart - cursor <= 3 && text[markerStart] == marker)
            {
                var length = RunLength(text, markerStart, marker);
                if (length >= fenceLength && text.AsSpan(markerStart + length, lineEnd - markerStart - length).IsWhiteSpace())
                    return lineEnd;
            }
            cursor = lineEnd;
        }
        return text.Length;
    }

    private static int EndOfCodeSpan(string text, int start)
    {
        var length = RunLength(text, start, '`');
        var cursor = start + length;
        while (cursor < text.Length)
        {
            if (text[cursor] != '`') { cursor++; continue; }
            var closingLength = RunLength(text, cursor, '`');
            if (closingLength == length) return cursor + closingLength;
            cursor += closingLength;
        }
        return text.Length;
    }

    private static int EndOfLine(string text, int start)
    {
        var cursor = start;
        while (cursor < text.Length && text[cursor] is not ('\r' or '\n')) cursor++;
        return cursor;
    }

    private static int RunLength(string text, int start, char marker)
    {
        var cursor = start;
        while (cursor < text.Length && text[cursor] == marker) cursor++;
        return cursor - start;
    }
}

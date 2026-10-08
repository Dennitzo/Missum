using System.Text;

namespace Missum.App.Controls;

/// <summary>Presentation-only recovery for clear scientific notation in older, unmarked replies.</summary>
internal static class NativeLooseMath
{
    internal static bool TryRead(string text, int start, out MathSegment segment, out int end)
    {
        segment = default;
        end = start;
        if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '\\')) return false;
        if (!char.IsLetterOrDigit(text[start]) && text[start] != '(') return false;
        var reader = new Reader(text, start);
        var latex = reader.ReadExpression();
        if (!reader.HasEvidence) return false;
        if (latex is null || reader.Incomplete || reader.ExceededLimit)
        {
            // Do not typeset isolated RHS fragments after an unfinished equation/operator.
            end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            return false;
        }
        var finish = reader.Position;
        while (finish > start && text[finish - 1] is ' ' or '\t') finish--;
        if (finish <= start || finish < text.Length && (char.IsLetterOrDigit(text[finish])
            || text[finish] is '_' or '^' or '\\'
            || text[finish] == '.' && finish + 1 < text.Length && char.IsLetter(text[finish + 1]))) return false;
        end = finish;
        segment = new(text[start..end], true, false) { RenderLatex = latex };
        return true;
    }

    internal static bool TrySkipLiteral(string text, int start, out int end)
    {
        end = start;
        if (text.AsSpan(start).StartsWith("](", StringComparison.Ordinal))
        {
            var depth = 1;
            end = start + 2;
            while (end < text.Length && text[end] is not ('\r' or '\n') && depth > 0)
            {
                if (text[end] == '(') depth++;
                if (text[end] == ')') depth--;
                end++;
            }
            return true;
        }
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '[' or '<' or '"' or '\'' or ':')) return false;
        if (!char.IsLetterOrDigit(text[start]) && text[start] is not ('/' or '\\')
            && !text.AsSpan(start).StartsWith("./", StringComparison.Ordinal)
            && !text.AsSpan(start).StartsWith("../", StringComparison.Ordinal)) return false;
        var tokenEnd = start;
        var scanLimit = Math.Min(text.Length, start + 4096);
        while (tokenEnd < scanLimit && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] is not ('`' or '|' or '>')) tokenEnd++;
        var token = text.AsSpan(start, tokenEnd - start);
        var firstSlash = token.IndexOfAny('/', '\\');
        var fileDot = token.LastIndexOf('.');
        var path = token.StartsWith("./", StringComparison.Ordinal) || token.StartsWith("../", StringComparison.Ordinal)
            || token.StartsWith("/", StringComparison.Ordinal) || token.StartsWith(@"\\", StringComparison.Ordinal)
            || token.Length > 2 && char.IsAsciiLetter(token[0]) && token[1] == ':' && token[2] is '/' or '\\'
            || firstSlash > 0 && token[..firstSlash].IndexOfAny('=', '^', '(') < 0
                && IsIdentifier(token[..firstSlash])
                && (firstSlash == 1 || !IsScientificSymbol(token[..firstSlash]));
        var url = token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || token.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || token.Contains('@') || IsBareDomain(token);
        var equals = token.IndexOf('=');
        var assignment = equals > 0 && IsIdentifier(token[..equals]) && !IsScientificSymbol(token[..equals]);
        var file = fileDot > 0 && fileDot + 1 < token.Length
            && token[..fileDot].IndexOfAny('=', '(', ')') < 0
            && token[(fileDot + 1)..].ToString().TrimEnd(',', ';', ')', ']', '"', '\'', '.').ToLowerInvariant() is
                "cs" or "py" or "tex" or "md" or "json" or "xml" or "yaml" or "yml" or "pdf" or "png" or "jpg" or "txt" or "exe" or "dll";
        if (!path && !url && !file && !assignment) return false;
        // A recognized long literal is skipped once, without rescanning its suffix at each character.
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd]) && text[tokenEnd] is not ('`' or '|' or '>')) tokenEnd++;
        end = tokenEnd;
        return end > start;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return false;
        foreach (var character in text)
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '.' or '-')) return false;
        return true;
    }

    private static bool IsScientificSymbol(ReadOnlySpan<char> text)
    {
        if (text.Length == 1 && char.IsLetter(text[0])) return true;
        if (text.Length == 2 && text[0] is 'd' or 'Δ' && char.IsLetter(text[1])) return true;
        if (text.Length < 3 || !char.IsLetter(text[0]) || text[1] != '_') return false;
        var label = text[2..];
        if (label.Length == 1 || label.SequenceEqual("std") || label.SequenceEqual("eff")
            || label.SequenceEqual("max") || label.SequenceEqual("min") || label.SequenceEqual("eq")
            || label.SequenceEqual("crit") || label.SequenceEqual("tot")) return true;
        foreach (var character in label) if (!char.IsUpper(character)) return false;
        return true;
    }

    private static bool IsBareDomain(ReadOnlySpan<char> token)
    {
        var suffix = token.IndexOfAny('/', '?', '#');
        var host = suffix < 0 ? token : token[..suffix];
        var dot = host.LastIndexOf('.');
        if (dot <= 0 || host.Length - dot is < 3 or > 25) return false;
        foreach (var character in host[..dot])
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-')) return false;
        foreach (var character in host[(dot + 1)..]) if (!char.IsAsciiLetter(character)) return false;
        return true;
    }

    private sealed class Reader(string text, int start)
    {
        private const int MaximumDepth = 16;
        private readonly int _limit = Math.Min(text.Length, start + 1024);
        private int _position = start;
        private int _depth;
        public int Position => _position;
        public bool HasEvidence { get; private set; }
        public bool Incomplete { get; private set; }
        public bool ExceededLimit => _position >= _limit && _limit < text.Length && text[_limit] is not ('\r' or '\n');

        public string? ReadExpression()
        {
            var left = ReadSum();
            if (left is null) return null;
            var result = new StringBuilder(left);
            while (true)
            {
                SkipSpace();
                if (_position >= _limit) break;
                var relation = text[_position] switch
                {
                    '=' => "=", '~' => @"\sim", '≈' => @"\approx", '≠' => @"\ne",
                    '≤' => @"\le", '≥' => @"\ge", '∝' => @"\propto", _ => null,
                };
                if (relation is null) break;
                _position++;
                var right = ReadSum();
                if (right is null) { Incomplete = true; return null; }
                result.Append(' ').Append(relation).Append(' ').Append(right);
            }
            return result.ToString();
        }

        private string? ReadSum()
        {
            var left = ReadProduct();
            if (left is null) return null;
            var result = new StringBuilder(left);
            while (true)
            {
                SkipSpace();
                if (_position >= _limit || text[_position] is not ('+' or '-' or '−')) break;
                var operation = text[_position++] == '+' ? '+' : '-';
                var right = ReadProduct();
                if (right is null) { Incomplete = true; return null; }
                result.Append(operation).Append(right);
            }
            return result.ToString();
        }

        private string? ReadProduct()
        {
            var left = ReadPower();
            if (left is null) return null;
            var result = new StringBuilder(left);
            while (true)
            {
                var previousEnd = _position;
                SkipSpace();
                if (_position >= _limit || text.AsSpan(_position).StartsWith("**", StringComparison.Ordinal)) break;
                var character = text[_position];
                if (character is '*' or '·' or '×' or '/')
                {
                    _position++;
                    if (character is '·' or '×') HasEvidence = true;
                    if (character == '/' && (left.StartsWith('d') || text.AsSpan(_position).StartsWith("dt", StringComparison.Ordinal))) HasEvidence = true;
                    var right = ReadPower();
                    if (right is null) { Incomplete = true; return null; }
                    result.Append(character == '/' ? "/" : @"\cdot ").Append(right);
                }
                else if (previousEnd == _position && (char.IsLetter(character) || character == '(')
                    && (char.IsDigit(text[previousEnd - 1]) || text[previousEnd - 1] == ')'
                        || character == '(' && IsScientificSymbol(left))
                    || _position > previousEnd && IsScientificSymbol(left) && IsScriptedSymbolAhead())
                {
                    var right = ReadPower();
                    if (right is null) break;
                    result.Append(' ').Append(right);
                }
                else break;
            }
            return result.ToString();
        }

        private bool IsScriptedSymbolAhead()
        {
            if (_position >= _limit || !char.IsLetter(text[_position])) return false;
            var marker = _position + 1;
            if (text[_position] is 'd' or 'Δ' && marker < _limit && char.IsLetter(text[marker])) marker++;
            return marker < _limit && (text[marker] is '_' or '^' || Superscript(text[marker]) is not null);
        }

        private string? ReadPower()
        {
            var value = ReadAtom();
            if (value is null) return null;
            var result = new StringBuilder(value);
            var hasSubscript = false;
            var hasExponent = false;
            while (_position < _limit)
            {
                var marker = text[_position];
                if (marker is '_' or '^')
                {
                    if (marker == '_' ? hasSubscript : hasExponent) { Incomplete = true; return null; }
                    if (marker == '_') hasSubscript = true; else hasExponent = true;
                    _position++;
                    var script = ReadScript(marker == '_');
                    if (script is null) { Incomplete = true; return null; }
                    if (marker == '^' || script.Length == 1 || script.All(char.IsUpper)
                        || script is "std" or "eff" or "max" or "min" or "tot" or "eq" or "crit") HasEvidence = true;
                    result.Append(marker).Append('{').Append(script).Append('}');
                }
                else if (Superscript(marker) is not null)
                {
                    if (hasExponent) { Incomplete = true; return null; }
                    hasExponent = true;
                    var script = new StringBuilder();
                    while (_position < _limit && Superscript(text[_position]) is { } exponent)
                    { script.Append(exponent); _position++; }
                    if (!script.ToString().Any(char.IsDigit)) { Incomplete = true; return null; }
                    HasEvidence = true;
                    result.Append("^{").Append(script).Append('}');
                }
                else break;
            }
            return result.ToString();
        }

        private string? ReadAtom()
        {
            SkipSpace();
            if (_position >= _limit || text[_position] is '\r' or '\n') return null;
            var character = text[_position];
            if (character is '+' or '-' or '−')
            {
                _position++;
                if (++_depth > MaximumDepth) return null;
                var atom = ReadAtom();
                _depth--;
                return atom is null ? null : (character == '+' ? "+" : "-") + atom;
            }
            if (character == '(')
            {
                _position++;
                if (++_depth > MaximumDepth) return null;
                var expression = ReadExpression();
                _depth--;
                SkipSpace();
                if (expression is null || _position >= _limit || text[_position] != ')')
                { Incomplete = true; return null; }
                _position++;
                return "(" + expression + ")";
            }
            if (char.IsAsciiDigit(character))
            {
                var numberStart = _position++;
                while (_position < _limit && char.IsAsciiDigit(text[_position])) _position++;
                if (_position + 1 < _limit && text[_position] is '.' or ',' && char.IsAsciiDigit(text[_position + 1]))
                {
                    _position++;
                    while (_position < _limit && char.IsAsciiDigit(text[_position])) _position++;
                }
                var number = text[numberStart.._position].Replace(",", "{,}", StringComparison.Ordinal);
                if (_position < _limit && text[_position] is 'e' or 'E')
                {
                    var exponentStart = _position + 1;
                    var exponentEnd = exponentStart;
                    if (exponentEnd < _limit && text[exponentEnd] is '+' or '-') exponentEnd++;
                    var digitsStart = exponentEnd;
                    while (exponentEnd < _limit && char.IsAsciiDigit(text[exponentEnd])) exponentEnd++;
                    if (exponentEnd > digitsStart)
                    {
                        _position = exponentEnd;
                        HasEvidence = true;
                        number += @"\cdot 10^{" + text[exponentStart..exponentEnd] + "}";
                    }
                }
                return number;
            }
            if (!char.IsLetter(character)) return null;
            var symbolStart = _position++;
            while (_position < _limit && char.IsLetter(text[_position])) _position++;
            var symbol = text[symbolStart.._position];
            if (symbol is "ln" or "log" or "sin" or "cos" or "tan" or "exp")
            {
                HasEvidence = true;
                if (++_depth > MaximumDepth) { Incomplete = true; return null; }
                var argument = ReadPower();
                _depth--;
                if (argument is null) { Incomplete = true; return null; }
                return "\\" + symbol + " " + argument;
            }
            if (symbol.Length == 2 && symbol[0] == 'Δ') { HasEvidence = true; return symbol; }
            if (symbol.Length == 1 || symbol.Length == 2 && symbol[0] == 'd' || symbol is "mc") return symbol;
            _position = symbolStart;
            return null;
        }

        private string? ReadScript(bool subscript)
        {
            if (_position >= _limit) return null;
            var braced = text[_position] == '{';
            if (braced) _position++;
            var scriptStart = _position;
            if (!subscript && _position < _limit && text[_position] is '+' or '-' or '−') _position++;
            if (!braced && !subscript)
            {
                if (_position < _limit && char.IsAsciiDigit(text[_position]))
                    while (_position < _limit && char.IsAsciiDigit(text[_position])) _position++;
                else if (_position < _limit && char.IsLetter(text[_position])) _position++;
            }
            else while (_position < _limit && char.IsLetterOrDigit(text[_position])) _position++;
            if (_position == scriptStart || _position - scriptStart > 8) return null;
            var script = text[scriptStart.._position].Replace('−', '-');
            if (script is "+" or "-") return null;
            if (braced)
            {
                if (_position >= _limit || text[_position] != '}') return null;
                _position++;
            }
            return script;
        }

        private void SkipSpace()
        {
            while (_position < _limit && text[_position] is ' ' or '\t') _position++;
        }

        private static char? Superscript(char character) => character switch
        {
            '⁰' => '0', '¹' => '1', '²' => '2', '³' => '3', '⁴' => '4', '⁵' => '5',
            '⁶' => '6', '⁷' => '7', '⁸' => '8', '⁹' => '9', '⁻' => '-', '⁺' => '+', _ => null,
        };
    }
}

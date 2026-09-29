using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>Small native lexer and reusable Inline renderer; it never changes the source text.</summary>
public sealed class NativeCodeHighlighter
{
    private const int MaximumHighlightedCharacters = 131_072;
    private const int MaximumRuns = 2_048;
    [ThreadStatic]
    private static Dictionary<TokenKind, SolidColorBrush>? _threadColors;
    private readonly TextBlock _target;
    private readonly Dictionary<TokenKind, SolidColorBrush> _colors;
    private readonly SolidColorBrush _plainForeground;
    private readonly List<(TokenKind Kind, Run Run)> _runs = [];
    private string? _source;
    private string? _language;

    public NativeCodeHighlighter(TextBlock target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _plainForeground = target.Foreground as SolidColorBrush ?? Brush(0xDC, 0xDC, 0xDC);
        // Brushes belong to the UI thread; one palette also keeps large diffs inexpensive.
        _colors = _threadColors ??= new()
        {
            [TokenKind.Keyword] = Brush(0xC5, 0x86, 0xC0),
            [TokenKind.Type] = Brush(0x4E, 0xC9, 0xB0),
            [TokenKind.String] = Brush(0xCE, 0x91, 0x78),
            [TokenKind.Number] = Brush(0xB5, 0xCE, 0xA8),
            [TokenKind.Comment] = Brush(0x6A, 0x99, 0x55),
            [TokenKind.Function] = Brush(0xDC, 0xDC, 0xAA),
            [TokenKind.Property] = Brush(0x9C, 0xDC, 0xFE),
            [TokenKind.Tag] = Brush(0x56, 0x9C, 0xD6),
        };
        // A Text value and an Inline collection should never compete on the same TextBlock.
        _target.Text = "";
    }

    public void UpdateText(string source, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var normalized = NormalizeLanguage(language);
        if (source == _source && normalized == _language) return;
        _source = source;
        _language = normalized;
        var spans = Tokenize(source, normalized);
        for (var index = 0; index < spans.Count; index++)
        {
            var span = spans[index];
            if (index < _runs.Count && _runs[index].Kind == span.Kind)
            {
                var run = _runs[index].Run;
                if (!source.AsSpan(span.Start, span.Length).SequenceEqual(run.Text.AsSpan()))
                    run.Text = source.Substring(span.Start, span.Length);
                continue;
            }
            var replacement = new Run { Text = source.Substring(span.Start, span.Length), Foreground = span.Kind == TokenKind.Plain ? _plainForeground : _colors[span.Kind] };
            if (index < _runs.Count)
            {
                _target.Inlines.RemoveAt(index);
                _target.Inlines.Insert(index, replacement);
                _runs[index] = (span.Kind, replacement);
            }
            else
            {
                _target.Inlines.Add(replacement);
                _runs.Add((span.Kind, replacement));
            }
        }
        while (_runs.Count > spans.Count)
        {
            var index = _runs.Count - 1;
            _target.Inlines.RemoveAt(index);
            _runs.RemoveAt(index);
        }
    }

    public static string NormalizeLanguage(string? language)
    {
        var value = (language ?? "").Trim().Trim('{', '}', '.').Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return value switch
        {
            "cs" or "c#" or "csharp" or "dotnet" => "csharp",
            "js" or "jsx" or "javascript" or "mjs" or "cjs" or "node" => "javascript",
            "ts" or "tsx" or "typescript" => "typescript",
            "py" or "python" or "python3" => "python",
            "ps" or "ps1" or "psm1" or "pwsh" or "powershell" => "powershell",
            "sh" or "bash" or "shell" or "zsh" or "fish" => "shell",
            "xml" or "xaml" or "html" or "htm" or "svg" or "xhtml" or "csproj" => "markup",
            "yml" or "yaml" => "yaml",
            "json" or "jsonc" or "json5" => "json",
            "css" or "scss" or "less" => "css",
            "c" or "cpp" or "c++" or "h" or "hpp" => "cpp",
            "rs" or "rust" => "rust",
            "golang" or "go" => "go",
            "kt" or "kotlin" => "kotlin",
            "sql" or "postgresql" or "mysql" or "sqlite" => "sql",
            "txt" or "text" or "plain" or "plaintext" or "console" or "output" or "log" => "plain",
            "patch" => "diff",
            _ => value,
        };
    }

    public static string LanguageForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var file = path.Replace('\\', '/').Split('/').Last().Trim('"');
        if (file.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "dockerfile";
        var extension = Path.GetExtension(file).TrimStart('.');
        return extension.ToLowerInvariant() switch
        {
            "props" or "targets" or "resx" or "config" => "markup",
            "bat" or "cmd" => "shell",
            "ino" => "cpp",
            _ => NormalizeLanguage(extension),
        };
    }

    private static List<TokenSpan> Tokenize(string source, string language)
    {
        var result = new List<TokenSpan>();
        if (source.Length == 0) return result;
        if (language == "plain") { result.Add(new(0, source.Length, TokenKind.Plain)); return result; }
        if (language.Length == 0)
        {
            var first = source.AsSpan().TrimStart();
            if (first.StartsWith("<", StringComparison.Ordinal)) language = "markup";
            else if (first.StartsWith("{", StringComparison.Ordinal) || first.StartsWith("[", StringComparison.Ordinal)) language = "json";
        }
        var limit = Math.Min(source.Length, MaximumHighlightedCharacters);
        if (limit < source.Length && limit > 0 && char.IsHighSurrogate(source[limit - 1]) && char.IsLowSurrogate(source[limit])) limit--;
        var position = 0;
        var insideTag = false;
        var expectTag = false;
        var markup = language == "markup";
        var hashComments = language is "python" or "powershell" or "shell" or "yaml" or "toml" or "ini" or "dockerfile";
        var cComments = language is not ("python" or "shell" or "yaml" or "toml" or "ini" or "sql" or "markup" or "powershell");
        var keywords = Keywords.GetValueOrDefault(language, GenericKeywords);
        while (position < limit && result.Count < MaximumRuns)
        {
            var start = position;
            var character = source[position];
            var kind = TokenKind.Plain;
            if (markup && Starts(source, position, "<!--", limit))
            {
                position = DelimitedEnd(source, position + 4, "-->", limit); kind = TokenKind.Comment;
            }
            else if (markup && Starts(source, position, "<![CDATA[", limit))
            {
                position = DelimitedEnd(source, position + 9, "]]>", limit);
            }
            else if (markup && character == '<')
            {
                position++; if (position < limit && source[position] is '/' or '!' or '?') position++;
                insideTag = true; expectTag = true; kind = TokenKind.Tag;
            }
            else if (markup && character == '>')
            {
                position++; insideTag = false; expectTag = false; kind = TokenKind.Tag;
            }
            else if (markup && character == '&')
            {
                position++;
                while (position < limit && !char.IsWhiteSpace(source[position]) && source[position] is not (';' or '<')) position++;
                if (position < limit && source[position] == ';') position++;
                kind = TokenKind.Number;
            }
            else if (!markup && language == "powershell" && Starts(source, position, "<#", limit))
            {
                position = DelimitedEnd(source, position + 2, "#>", limit); kind = TokenKind.Comment;
            }
            else if (!markup && ((hashComments && character == '#') || (cComments && Starts(source, position, "//", limit))
                || (language == "sql" && Starts(source, position, "--", limit))))
            {
                position = LineEnd(source, position, limit); kind = TokenKind.Comment;
            }
            else if (!markup && (cComments || language == "sql") && Starts(source, position, "/*", limit))
            {
                position = DelimitedEnd(source, position + 2, "*/", limit); kind = TokenKind.Comment;
            }
            else if ((!markup || insideTag) && TryString(source, position, limit, language, out var stringEnd))
            {
                position = stringEnd;
                var next = NextNonWhitespace(source, position, limit);
                kind = language == "json" && next < limit && source[next] == ':' ? TokenKind.Property : TokenKind.String;
            }
            else if (!markup && (language is "shell" or "powershell") && character == '$')
            {
                position++;
                if (position < limit && source[position] == '{') position = DelimitedEnd(source, position + 1, "}", limit);
                else while (position < limit && (IdentifierPart(source[position]) || source[position] is ':' or '?' or '@')) position++;
                kind = TokenKind.Property;
            }
            else if ((!markup || insideTag) && (char.IsDigit(character) || character == '.' && position + 1 < limit && char.IsDigit(source[position + 1])))
            {
                position = NumberEnd(source, position, limit);
                kind = TokenKind.Number;
            }
            else if ((!markup || insideTag) && IdentifierStart(character))
            {
                position++;
                while (position < limit && (IdentifierPart(source[position]) || markup && (source[position] is ':' or '-' or '.')
                    || (language is "powershell" or "shell" or "css" or "yaml") && source[position] == '-')) position++;
                var word = source[start..position];
                var next = NextNonWhitespace(source, position, limit);
                if (markup) { kind = expectTag ? TokenKind.Tag : TokenKind.Property; expectTag = false; }
                else if (keywords.Contains(word)) kind = TokenKind.Keyword;
                else if (Types.Contains(word)) kind = TokenKind.Type;
                else if (next < limit && source[next] == '(') kind = TokenKind.Function;
                else if (next < limit && source[next] == ':' && (language is "json" or "yaml" or "css" or "javascript" or "typescript")) kind = TokenKind.Property;
                else if (char.IsUpper(character) && (language is "csharp" or "cpp" or "java" or "kotlin" or "typescript" or "rust")) kind = TokenKind.Type;
            }
            else
            {
                position++;
                // Keep whitespace intact but group it to avoid thousands of one-character runs.
                if (char.IsWhiteSpace(character)) while (position < limit && char.IsWhiteSpace(source[position])) position++;
            }
            AddSpan(result, start, position - start, kind);
        }
        // Huge/generated blocks remain complete, selectable and copyable beyond the color budget.
        if (position < source.Length) AddSpan(result, position, source.Length - position, TokenKind.Plain);
        return result;
    }

    private static bool TryString(string source, int start, int limit, string language, out int end)
    {
        end = start;
        var quoteAt = start;
        var verbatim = false;
        if (language == "csharp")
        {
            while (quoteAt < limit && source[quoteAt] is '$' or '@')
            {
                verbatim |= source[quoteAt] == '@';
                quoteAt++;
            }
        }
        else if (language == "python")
        {
            while (quoteAt < Math.Min(start + 2, limit) && source[quoteAt] is 'r' or 'R' or 'f' or 'F' or 'b' or 'B' or 'u' or 'U') quoteAt++;
        }
        var hereString = language == "powershell" && quoteAt + 1 < limit && source[quoteAt] == '@' && (source[quoteAt + 1] is '\'' or '"');
        if (hereString) quoteAt++;
        if (quoteAt >= limit) return false;
        var quote = source[quoteAt];
        var template = quote == (char)96 && (language is "javascript" or "typescript" or "shell" or "go");
        if (quote is not ('\'' or '"') && !template) return false;
        if (hereString)
        {
            end = DelimitedEnd(source, quoteAt + 1, "\n" + quote + "@", limit);
            return true;
        }
        var delimiterLength = 1;
        if ((language is "python" or "csharp") && quoteAt + 2 < limit && source[quoteAt + 1] == quote && source[quoteAt + 2] == quote)
        {
            delimiterLength = 3;
            if (language == "csharp") while (quoteAt + delimiterLength < limit && source[quoteAt + delimiterLength] == quote) delimiterLength++;
        }
        var position = quoteAt + delimiterLength;
        var backslashEscapes = !verbatim && language is not ("powershell" or "markup" or "sql")
            && !(language == "shell" && quote == '\'') && !(language == "go" && quote == (char)96)
            && !(language == "csharp" && delimiterLength > 1);
        while (position < limit)
        {
            if (source[position] == quote)
            {
                if (delimiterLength > 1)
                {
                    var repeated = 0;
                    while (position + repeated < limit && source[position + repeated] == quote && repeated < delimiterLength) repeated++;
                    if (repeated == delimiterLength) { end = position + repeated; return true; }
                }
                else if ((verbatim || language is "sql" or "powershell") && position + 1 < limit && source[position + 1] == quote) { position += 2; continue; }
                else { end = position + 1; return true; }
            }
            if ((backslashEscapes && source[position] == '\\')
                || language == "powershell" && source[position] == (char)96)
                position = Math.Min(limit, position + 2);
            else position++;
        }
        end = limit; // Open strings still receive stable color during streaming.
        return true;
    }

    private static void AddSpan(List<TokenSpan> spans, int start, int length, TokenKind kind)
    {
        if (length == 0) return;
        if (spans.Count > 0 && spans[^1].Kind == kind)
        {
            var previous = spans[^1];
            spans[^1] = previous with { Length = previous.Length + length };
        }
        else spans.Add(new(start, length, kind));
    }

    private static int LineEnd(string source, int start, int limit)
    {
        while (start < limit && source[start] is not ('\r' or '\n')) start++;
        return start;
    }
    private static int NumberEnd(string source, int start, int limit)
    {
        var position = start;
        if (position + 1 < limit && source[position] == '0' && source[position + 1] is 'x' or 'X' or 'b' or 'B')
        {
            var hex = source[position + 1] is 'x' or 'X';
            position += 2;
            while (position < limit && (source[position] == '_' || (hex ? char.IsAsciiHexDigit(source[position]) : source[position] is '0' or '1'))) position++;
        }
        else
        {
            if (source[position] == '.') position++;
            while (position < limit && (char.IsDigit(source[position]) || source[position] == '_')) position++;
            if (position + 1 < limit && source[position] == '.' && char.IsDigit(source[position + 1]))
            {
                position++;
                while (position < limit && (char.IsDigit(source[position]) || source[position] == '_')) position++;
            }
            if (position < limit && source[position] is 'e' or 'E')
            {
                var exponent = position + 1;
                if (exponent < limit && source[exponent] is '+' or '-') exponent++;
                var digits = exponent;
                while (exponent < limit && char.IsDigit(source[exponent])) exponent++;
                if (exponent > digits) position = exponent;
            }
        }
        while (position < limit && source[position] is 'f' or 'F' or 'd' or 'D' or 'm' or 'M' or 'u' or 'U' or 'l' or 'L') position++;
        return position;
    }
    private static int NextNonWhitespace(string source, int start, int limit)
    {
        while (start < limit && char.IsWhiteSpace(source[start])) start++;
        return start;
    }
    private static int DelimitedEnd(string source, int start, string marker, int limit)
    {
        var found = source.AsSpan(start, limit - start).IndexOf(marker, StringComparison.Ordinal);
        return found < 0 ? limit : start + found + marker.Length;
    }
    private static bool Starts(string source, int start, string value, int limit) =>
        source.AsSpan(start, limit - start).StartsWith(value, StringComparison.Ordinal);
    private static bool IdentifierStart(char value) => char.IsLetter(value) || value == '_';
    private static bool IdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromArgb(255, red, green, blue));
    private static HashSet<string> Words(string source, bool ignoreCase = false) =>
        new(source.Split(' ', StringSplitOptions.RemoveEmptyEntries), ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly HashSet<string> GenericKeywords = Words("if else for foreach while do switch case break continue return throw try catch finally class struct enum interface namespace public private protected internal static const readonly new using import from export async await true false null undefined let var def function yield");
    private static readonly HashSet<string> Types = Words("bool boolean byte char decimal double float int long object sbyte short string uint ulong ushort void dynamic number bigint symbol never any unknown Int32 Int64 String Boolean Task ValueTask List Dictionary IEnumerable Span Type Exception");
    private static readonly Dictionary<string, HashSet<string>> Keywords = new(StringComparer.Ordinal)
    {
        ["csharp"] = Words("abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern false finally fixed for foreach goto if implicit in init interface internal is lock namespace new null operator out override params partial private protected public readonly record ref required return sealed set get sizeof stackalloc static struct switch this throw true try typeof unchecked unsafe using value virtual volatile when where while with yield global"),
        ["javascript"] = Words("async await break case catch class const continue debugger default delete do else export extends false finally for from function get if import in instanceof let new null of return set static super switch this throw true try typeof undefined var void while with yield"),
        ["typescript"] = Words("abstract any as asserts async await break case catch class const constructor continue declare default delete do else enum export extends false finally for from function get if implements import in infer instanceof interface is keyof let module namespace never new null of private protected public readonly require return set static super switch this throw true try type typeof undefined unique unknown var void while with yield"),
        ["python"] = Words("and as assert async await break case class continue def del elif else except False finally for from global if import in is lambda match None nonlocal not or pass raise return True try while with yield"),
        ["json"] = Words("true false null Infinity NaN"),
        ["powershell"] = Words("begin break catch class continue data default define do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow true false null", true),
        ["shell"] = Words("if then else elif fi for do done while until case esac function select in time export readonly local declare typeset return break continue true false exec source"),
        ["sql"] = Words("select from where join inner outer left right full on as and or not null is in exists like between group by having order asc desc distinct limit offset union all insert into values update set delete create alter drop table view index primary foreign key references unique default constraint begin commit rollback case when then else end with recursive returning true false", true),
        ["yaml"] = Words("true false null yes no on off", true),
        ["toml"] = Words("true false"),
        ["dockerfile"] = Words("from run cmd label expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell", true),
        ["cpp"] = Words("alignas alignof asm auto break case catch class concept const constexpr consteval constinit continue co_await co_return co_yield default delete do else enum explicit export extern false for friend if inline mutable namespace new noexcept nullptr operator private protected public register reinterpret_cast requires return signed sizeof static static_assert static_cast struct switch template this thread_local throw true try typedef typeid typename union unsigned using virtual volatile while"),
        ["java"] = Words("abstract assert break case catch class const continue default do else enum extends final finally for if implements import instanceof interface native new null package private protected public record return sealed static strictfp super switch synchronized this throw throws transient true false try var void volatile while yield"),
        ["go"] = Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil"),
        ["rust"] = Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while"),
        ["kotlin"] = Words("as break class continue do else false for fun if in interface is null object package return super this throw true try typealias typeof val var when while by catch constructor delegate dynamic field file finally get import init param property receiver set setparam where actual abstract annotation companion const crossinline data enum expect external final infix inline inner internal lateinit noinline open operator out override private protected public reified sealed suspend tailrec vararg"),
    };

    private enum TokenKind { Plain, Keyword, Type, String, Number, Comment, Function, Property, Tag }
    private readonly record struct TokenSpan(int Start, int Length, TokenKind Kind);
}

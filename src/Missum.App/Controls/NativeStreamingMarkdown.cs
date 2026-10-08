using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Missum.App.Controls;

/// <summary>Native Markdown that retains its paragraphs and inline runs while text streams in.</summary>
public sealed partial class NativeStreamingMarkdown : StackPanel
{
    internal const double BodyFontSize = 16;
    private readonly List<SectionView> _sections = [];
    private string _text = string.Empty;
    internal IReadOnlyList<Grid> RenderedTables => _sections.Where(section => section.Specification.Kind == SectionKind.Table)
        .Select(section => section.Table!.Grid).ToArray();

    public NativeStreamingMarkdown(string text)
    {
        Spacing = 9;
        UpdateText(text);
    }

    /// <summary>Updates on the owning UI thread without rebuilding unchanged text or code blocks.</summary>
    public void UpdateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_text, text, StringComparison.Ordinal)) return;
        _text = text;
        var specifications = ParseSections(text);
        var next = new SectionView?[specifications.Count];
        var used = new HashSet<SectionView>();

        // Reserve exact matches first, including blocks moved by an inserted or removed paragraph.
        var exact = new Dictionary<SectionSpec, Queue<SectionView>>();
        foreach (var section in _sections)
        {
            if (!exact.TryGetValue(section.Specification, out var matches))
            {
                matches = new Queue<SectionView>();
                exact.Add(section.Specification, matches);
            }
            matches.Enqueue(section);
        }
        for (var index = 0; index < specifications.Count; index++)
        {
            if (!exact.TryGetValue(specifications[index], out var matches) || matches.Count == 0) continue;
            next[index] = matches.Dequeue();
            used.Add(next[index]!);
        }

        for (var index = 0; index < specifications.Count; index++)
        {
            if (next[index] is not null) continue;
            var specification = specifications[index];
            SectionView? reusable = null;
            if (index < _sections.Count && !used.Contains(_sections[index]) && _sections[index].Specification.Kind == specification.Kind)
                reusable = _sections[index];
            reusable ??= _sections.FirstOrDefault(section => !used.Contains(section) && section.Specification.Kind == specification.Kind);
            var section = reusable ?? CreateSection(specification.Kind);
            UpdateSection(section, specification);
            next[index] = section;
            used.Add(section);
        }

        for (var index = 0; index < next.Length; index++)
        {
            var element = next[index]!.Element;
            if (index < Children.Count && ReferenceEquals(Children[index], element)) continue;
            var oldIndex = Children.IndexOf(element);
            if (oldIndex >= 0) Children.RemoveAt(oldIndex);
            Children.Insert(index, element);
        }
        while (Children.Count > next.Length) Children.RemoveAt(Children.Count - 1);
        _sections.Clear();
        _sections.AddRange(next.Select(section => section!));
    }

    private static SectionView CreateSection(SectionKind kind)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily(kind == SectionKind.Code ? "Cascadia Mono" : "Segoe UI Variable Text"),
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            FontSize = kind == SectionKind.Code ? 15 : BodyFontSize,
            LineHeight = 26,
            Foreground = NativeThemeBrushes.Text,
        };
        if (kind is SectionKind.Paragraph or SectionKind.Math or SectionKind.Table) return new SectionView(text, text);

        var language = new TextBlock { FontSize = 12, Foreground = NativeThemeBrushes.MutedText, VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button
        {
            Content = "Code kopieren",
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4),
        };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(language);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(header);
        var codeHost = new ContentControl { Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(codeHost);
        var border = new Border { Child = panel, Background = NativeThemeBrushes.Resource("MissumLayerBrush", 32), CornerRadius = new CornerRadius(10), Padding = new Thickness(14) };
        var section = new SectionView(border, text) { Language = language, CodeHost = codeHost };
        copy.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(section.Specification.Text);
            Clipboard.SetContent(package);
        };
        return section;
    }

    private static void UpdateSection(SectionView section, SectionSpec specification)
    {
        if (section.Specification == specification) return;
        section.Specification = specification;
        if (specification.Kind == SectionKind.Math)
        {
            section.Element = new ScrollViewer { Content = new NativeFormulaView(specification.Text, true, BodyFontSize),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Margin = new Thickness(0, 6, 0, 6) };
            return;
        }
        if (specification.Kind == SectionKind.Table)
        {
            section.Table ??= new NativeTableView();
            section.Table.Update(specification.Text, specification.MathTokenPrefix);
            section.Element = section.Table;
            return;
        }
        if (specification.Kind == SectionKind.Code)
        {
            section.Language!.Text = specification.Language;
            if (NativeCodeHighlighter.NormalizeLanguage(specification.Language) == "diff")
            {
                if (section.Diff is null) section.Diff = new NativeDiffView(specification.Text);
                else section.Diff.UpdateDiff(specification.Text);
                if (!ReferenceEquals(section.CodeHost!.Content, section.Diff)) section.CodeHost.Content = section.Diff;
            }
            else
            {
                section.CodeHighlighter ??= new NativeCodeHighlighter(section.Text);
                section.CodeHighlighter.UpdateText(specification.Text, specification.Language);
                if (!ReferenceEquals(section.CodeHost!.Content, section.Text)) section.CodeHost.Content = section.Text;
            }
            return;
        }

        if (!string.IsNullOrEmpty(specification.MathTokenPrefix))
        {
            section.MathText ??= new NativeMathParagraph();
            section.MathText.FontSize = SectionFontSize(specification.HeadingLevel);
            section.MathText.FontWeight = specification.HeadingLevel > 0
                ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            section.MathText.UpdateTokenizedText(specification.Text, specification.MathTokenPrefix);
            section.Element = section.MathText;
            return;
        }
        section.MathText = null;
        section.Element = section.Text;
        section.Text.FontSize = SectionFontSize(specification.HeadingLevel);
        section.Text.LineHeight = section.Text.FontSize * 26 / BodyFontSize;
        section.Text.FontWeight = specification.HeadingLevel > 0
            ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        var pieces = ParseInlines(specification.Text);
        for (var index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            if (index < section.Inlines.Count && section.Inlines[index].Kind == piece.Kind)
            {
                var existing = section.Inlines[index];
                if (!string.Equals(existing.Run.Text, piece.Text, StringComparison.Ordinal)) existing.Run.Text = piece.Text;
                if (existing.Element is Hyperlink hyperlink && hyperlink.NavigateUri != piece.Uri) hyperlink.NavigateUri = piece.Uri;
                continue;
            }
            var run = new Run { Text = piece.Text, FontFamily = new FontFamily(piece.Kind == InlineKind.Code ? "Cascadia Mono" : "Segoe UI Variable Text") };
            Inline element = piece.Kind switch
            {
                InlineKind.Bold => new Bold { Inlines = { run } },
                InlineKind.Link => new Hyperlink { NavigateUri = piece.Uri, Foreground = NativeThemeBrushes.ReadableAccent, Inlines = { run } },
                _ => run,
            };
            var replacement = new InlineView(piece.Kind, element, run);
            if (index < section.Inlines.Count)
            {
                section.Text.Inlines.RemoveAt(index);
                section.Text.Inlines.Insert(index, element);
                section.Inlines[index] = replacement;
            }
            else
            {
                section.Text.Inlines.Add(element);
                section.Inlines.Add(replacement);
            }
        }
        while (section.Inlines.Count > pieces.Count)
        {
            var index = section.Inlines.Count - 1;
            section.Inlines.RemoveAt(index);
            section.Text.Inlines.RemoveAt(index);
        }
    }

    internal static List<SectionSpec> ParseSections(string text)
    {
        var result = new List<SectionSpec>();
        var pending = new StringBuilder();
        var tokenPrefix = MathTokenPrefix(text);
        foreach (var segment in NativeMathSyntax.SplitForRendering(text))
        {
            if (segment.IsMath && segment.Display)
            {
                result.AddRange(ParseTextSections(pending.ToString(), tokenPrefix)); pending.Clear();
                result.Add(new SectionSpec(SectionKind.Math, segment.Text, 0, "latex"));
            }
            else pending.Append(segment.IsMath ? EncodeMathToken(segment, tokenPrefix) : segment.Text);
        }
        result.AddRange(ParseTextSections(pending.ToString(), tokenPrefix));
        return result;
    }

    private static List<SectionSpec> ParseTextSections(string text, string tokenPrefix)
    {
        var result = new List<SectionSpec>();
        var code = new StringBuilder();
        var insideCode = false;
        var fenceCharacter = '`';
        var fenceLength = 3;
        var language = string.Empty;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var fence = Regex.Match(line, @"^ {0,3}(?<fence>`{3,}|~{3,})(?<info>.*)$", RegexOptions.CultureInvariant);
            var validOpening = fence.Success && (fence.Groups["fence"].Value[0] != '`' || !fence.Groups["info"].Value.Contains('`'));
            if (fence.Success && (!insideCode && validOpening || insideCode && fence.Groups["fence"].Value[0] == fenceCharacter && fence.Groups["fence"].Length >= fenceLength && string.IsNullOrWhiteSpace(fence.Groups["info"].Value)))
            {
                if (insideCode)
                {
                    result.Add(new SectionSpec(SectionKind.Code, code.ToString().TrimEnd('\n'), 0, language)); code.Clear();
                }
                else
                {
                    language = fence.Groups["info"].Value.Trim();
                    fenceCharacter = fence.Groups["fence"].Value[0]; fenceLength = fence.Groups["fence"].Length;
                }
                insideCode = !insideCode;
                continue;
            }
            if (insideCode)
            {
                code.Append(line).Append('\n');
                continue;
            }
            // Publication delimiters are transport metadata, including in a
            // delegated Science answer. Literal examples inside code stay visible.
            if (line.Trim() is "<!-- MISSUM_PUBLICATION_BEGIN -->" or "<!-- MISSUM_PUBLICATION_END -->") continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryParseTable(lines, lineIndex, out var table, out var nextLine))
            {
                result.Add(new SectionSpec(SectionKind.Table, table.Markdown, 0, string.Empty,
                    table.Markdown.Contains(tokenPrefix, StringComparison.Ordinal) ? tokenPrefix : string.Empty));
                lineIndex = nextLine - 1;
                continue;
            }
            var isHeading = TryParseHeading(line, out var headingLevel, out var headingText);
            var source = isHeading ? headingText : BulletPattern().Replace(line, "•  ");
            // Only the global parser may recognize formulas. Re-parsing the raw line here
            // would turn examples inside multiline code or unfinished display math into TeX.
            result.Add(new SectionSpec(SectionKind.Paragraph, source, headingLevel, string.Empty,
                source.Contains(tokenPrefix, StringComparison.Ordinal) ? tokenPrefix : string.Empty));
        }
        // An opening fence is sufficient: live code must be visible before the closing fence arrives.
        if (insideCode) result.Add(new SectionSpec(SectionKind.Code, code.ToString().TrimEnd('\n'), 0, language));
        return result;
    }

    /// <summary>Only a matching Markdown header and delimiter promote pipe text to a table.</summary>
    internal static bool TryParseTable(IReadOnlyList<string> lines, int start, out TableSpec table, out int nextLine)
    {
        table = default;
        nextLine = start;
        var headers = SplitTableCells(lines[start]);
        if (headers.Count == 0 || headers.All(string.IsNullOrWhiteSpace)
            || headers.Count == 1 && !(lines[start].Trim().StartsWith('|') && lines[start].Trim().EndsWith('|'))) return false;
        var separator = start + 1;
        while (separator < lines.Count && string.IsNullOrWhiteSpace(lines[separator])) separator++;
        if (separator >= lines.Count) return false;
        var delimiter = SplitTableCells(lines[separator]);
        if (delimiter.Count != headers.Count || delimiter.Any(cell => !TableDelimiterPattern().IsMatch(cell))) return false;
        var alignments = delimiter.Select(cell => cell.StartsWith(':') && cell.EndsWith(':') ? TextAlignment.Center
            : cell.EndsWith(':') ? TextAlignment.Right : TextAlignment.Left).ToArray();
        var rows = new List<IReadOnlyList<string>>();
        var markdown = new StringBuilder(lines[start]).Append('\n').Append(lines[separator]);
        nextLine = separator + 1;
        while (nextLine < lines.Count)
        {
            var rowLine = nextLine;
            while (rowLine < lines.Count && string.IsNullOrWhiteSpace(lines[rowLine])) rowLine++;
            if (rowLine >= lines.Count) break;
            var cells = SplitTableCells(lines[rowLine]);
            // The final, unfinished row is visible while it streams. Other malformed
            // rows remain prose, instead of silently discarding or merging columns.
            if (cells.Count == 0 || cells.Count > headers.Count
                || cells.Count != headers.Count && (rowLine != lines.Count - 1 || !lines[rowLine].TrimStart().StartsWith('|'))) break;
            if (cells.Count < headers.Count)
                cells.AddRange(Enumerable.Repeat(string.Empty, headers.Count - cells.Count));
            rows.Add(cells);
            markdown.Append('\n').Append(lines[rowLine]);
            nextLine = rowLine + 1;
        }
        table = new TableSpec(headers, alignments, rows, markdown.ToString());
        return true;
    }

    internal static List<string> SplitTableCells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var codeFence = 0;
        var separators = 0;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '`')
            {
                var ticks = 1;
                while (index + ticks < line.Length && line[index + ticks] == '`') ticks++;
                if (codeFence == 0) codeFence = ticks;
                else if (codeFence == ticks) codeFence = 0;
                cell.Append('`', ticks);
                index += ticks - 1;
                continue;
            }
            if (character != '|' || codeFence != 0) { cell.Append(character); continue; }
            var backslashes = 0;
            for (var before = index - 1; before >= 0 && line[before] == '\\'; before--) backslashes++;
            if (backslashes % 2 != 0)
            {
                cell.Length--;
                cell.Append('|');
                continue;
            }
            cells.Add(cell.ToString().Trim());
            cell.Clear();
            separators++;
        }
        if (separators == 0) return [];
        cells.Add(cell.ToString().Trim());
        if (cells.Count > 0 && cells[0].Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }

    internal static (string Text, string Prefix) TokenizeMath(string text)
    {
        var prefix = MathTokenPrefix(text);
        var source = new StringBuilder();
        foreach (var segment in NativeMathSyntax.SplitForRendering(text))
            source.Append(segment.IsMath ? EncodeMathToken(segment, prefix) : segment.Text);
        return (source.ToString(), prefix);
    }

    private static string MathTokenPrefix(string text)
    {
        var prefix = "\uE000MISSUM_MATH_";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix = "\uE000" + prefix;
        return prefix;
    }

    private static string EncodeMathToken(MathSegment segment, string prefix) => segment.RenderLatex is { } renderLatex
        ? prefix + "L" + Convert.ToBase64String(Encoding.UTF8.GetBytes(segment.Text)) + ":"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(renderLatex)) + "\uE001"
        : prefix + (segment.Display ? "D" : "I") + Convert.ToBase64String(Encoding.UTF8.GetBytes(segment.Text)) + "\uE001";

    internal static IReadOnlyList<MathInlineSpec> ParseMathInlines(string source, string tokenPrefix)
    {
        var result = new List<MathInlineSpec>();
        // Markdown sees safe placeholders first, so emphasis and links may surround a
        // formula without losing their opening/closing markup at the formula boundary.
        foreach (var piece in ParseInlines(source))
        {
            var offset = 0;
            while (offset < piece.Text.Length)
            {
                var start = piece.Text.IndexOf(tokenPrefix, offset, StringComparison.Ordinal);
                var end = start < 0 ? -1 : piece.Text.IndexOf('\uE001', start + tokenPrefix.Length);
                if (end < 0)
                {
                    result.Add(new(piece.Text[offset..], false, false, piece.Kind, piece.Uri));
                    break;
                }
                if (start > offset) result.Add(new(piece.Text[offset..start], false, false, piece.Kind, piece.Uri));
                var payload = piece.Text[(start + tokenPrefix.Length)..end];
                if (payload.Length < 2 || payload[0] is not ('I' or 'D' or 'L'))
                {
                    result.Add(new(piece.Text[start..(end + 1)], false, false, piece.Kind, piece.Uri));
                    offset = end + 1;
                    continue;
                }
                try
                {
                    if (payload[0] == 'L')
                    {
                        var separator = payload.IndexOf(':');
                        if (separator < 2 || separator == payload.Length - 1) throw new FormatException("Incomplete legacy math token.");
                        var original = Encoding.UTF8.GetString(Convert.FromBase64String(payload[1..separator]));
                        var renderLatex = Encoding.UTF8.GetString(Convert.FromBase64String(payload[(separator + 1)..]));
                        result.Add(new(original, true, false, piece.Kind, piece.Uri, renderLatex));
                    }
                    else
                    {
                        var latex = Encoding.UTF8.GetString(Convert.FromBase64String(payload[1..]));
                        result.Add(new(latex, true, payload[0] == 'D', piece.Kind, piece.Uri));
                    }
                }
                catch (FormatException)
                {
                    result.Add(new(piece.Text[start..(end + 1)], false, false, piece.Kind, piece.Uri));
                }
                offset = end + 1;
            }
        }
        return result;
    }

    private static List<InlineSpec> ParseInlines(string source)
    {
        var result = new List<InlineSpec>();
        var offset = 0;
        foreach (Match match in InlinePattern().Matches(source))
        {
            if (match.Index > offset) result.Add(new InlineSpec(InlineKind.Plain, source[offset..match.Index]));
            if (match.Groups["bold"].Success) result.Add(new InlineSpec(InlineKind.Bold, match.Groups["bold"].Value));
            else if (match.Groups["code"].Success) result.Add(new InlineSpec(InlineKind.Code, match.Groups["code"].Value));
            else if (Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                result.Add(new InlineSpec(InlineKind.Link, match.Groups["label"].Value, uri));
            else result.Add(new InlineSpec(InlineKind.Plain, match.Value));
            offset = match.Index + match.Length;
        }
        if (offset < source.Length) result.Add(new InlineSpec(InlineKind.Plain, source[offset..]));
        return result;
    }

    internal static double SectionFontSize(int headingLevel) => headingLevel switch
    {
        1 => BodyFontSize + 8, 2 => BodyFontSize + 6, 3 => BodyFontSize + 4,
        4 => BodyFontSize + 2, 5 => BodyFontSize + 1, _ => BodyFontSize,
    };

    internal static bool TryParseHeading(string line, out int level, out string content)
    {
        var match = HeadingPattern().Match(line);
        level = match.Success ? match.Groups["marker"].Length : 0;
        content = match.Success ? HeadingClosingPattern().Replace(match.Groups["content"].Value, "").TrimEnd(' ', '\t') : line;
        return match.Success;
    }

    // Model reasoning often indents its scientific draft by four spaces.
    // Fenced/inline code is already protected; indentation alone does not turn
    // text into a code block in this renderer. Recognize headings there too.
    // Horizontal whitespace keeps each streamed line an independent boundary.
    [GeneratedRegex(@"^[ \t]*(?<marker>#{1,6})(?:[ \t]+(?<content>.*)|)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"(?:^#+[ \t]*$|[ \t]+#+[ \t]*$)", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingClosingPattern();

    [GeneratedRegex(@"^\s*[-*]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^:?-{3,}:?$", RegexOptions.CultureInvariant)]
    private static partial Regex TableDelimiterPattern();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<uri>https?://[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlinePattern();

    internal enum SectionKind { Paragraph, Code, Math, Table }
    internal enum InlineKind { Plain, Bold, Code, Link }
    internal readonly record struct MathInlineSpec(string Text, bool IsMath, bool Display, InlineKind Kind, Uri? Uri, string? RenderLatex = null);
    internal readonly record struct SectionSpec(SectionKind Kind, string Text, int HeadingLevel, string Language, string MathTokenPrefix = "");
    internal readonly record struct TableSpec(IReadOnlyList<string> Headers, IReadOnlyList<TextAlignment> Alignments,
        IReadOnlyList<IReadOnlyList<string>> Rows, string Markdown);
    private readonly record struct InlineSpec(InlineKind Kind, string Text, Uri? Uri = null);
    private sealed record InlineView(InlineKind Kind, Inline Element, Run Run);

    private sealed class SectionView(FrameworkElement element, TextBlock text)
    {
        public SectionSpec Specification { get; set; }
        public FrameworkElement Element { get; set; } = element;
        public NativeMathParagraph? MathText { get; set; }
        public NativeTableView? Table { get; set; }
        public TextBlock Text { get; } = text;
        public TextBlock? Language { get; init; }
        public ContentControl? CodeHost { get; init; }
        public NativeDiffView? Diff { get; set; }
        public NativeCodeHighlighter? CodeHighlighter { get; set; }
        public List<InlineView> Inlines { get; } = [];
    }

    /// <summary>Wrapped cells retain their native text and formula controls as rows grow.</summary>
    private sealed class NativeTableView : UserControl
    {
        private readonly Grid _grid = new();
        private readonly List<(Border Border, NativeMathParagraph Text)> _cells = [];
        private int _columns;
        public Grid Grid => _grid;

        public NativeTableView()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Content = new Border { BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 62), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Child = _grid };
        }

        public void Update(string markdown, string tokenPrefix)
        {
            if (string.IsNullOrEmpty(tokenPrefix)) tokenPrefix = MathTokenPrefix(markdown);
            var lines = markdown.Split('\n');
            if (!TryParseTable(lines, 0, out var specification, out _)) return;
            if (_columns != specification.Headers.Count)
            {
                _columns = specification.Headers.Count;
                _grid.ColumnDefinitions.Clear();
                _grid.Children.Clear();
                _cells.Clear();
                for (var column = 0; column < _columns; column++)
                    _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            var rowCount = specification.Rows.Count + 1;
            while (_grid.RowDefinitions.Count < rowCount) _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            while (_grid.RowDefinitions.Count > rowCount) _grid.RowDefinitions.RemoveAt(_grid.RowDefinitions.Count - 1);
            var cellCount = rowCount * _columns;
            for (var index = 0; index < cellCount; index++)
            {
                var row = index / _columns;
                var column = index % _columns;
                if (index >= _cells.Count)
                {
                    var text = new NativeMathParagraph { FontSize = BodyFontSize, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var border = new Border { Child = text, Padding = new Thickness(11, 8, 11, 8), BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 55),
                        BorderThickness = new Thickness(0, 0, 0, 1), Background = NativeThemeBrushes.Resource(row == 0 ? "MissumHoverBrush" : row % 2 == 0 ? "MissumWindowBrush" : "MissumLayerBrush", 35) };
                    _cells.Add((border, text));
                    _grid.Children.Add(border);
                    Grid.SetRow(border, row);
                    Grid.SetColumn(border, column);
                }
                var cell = _cells[index];
                cell.Text.FontWeight = row == 0 ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                cell.Text.Foreground = NativeThemeBrushes.Text;
                cell.Text.TextAlignment = specification.Alignments[column];
                cell.Text.UpdateTokenizedText(row == 0 ? specification.Headers[column] : specification.Rows[row - 1][column], tokenPrefix);
            }
            while (_cells.Count > cellCount)
            {
                _grid.Children.Remove(_cells[^1].Border);
                _cells.RemoveAt(_cells.Count - 1);
            }
        }
    }
}

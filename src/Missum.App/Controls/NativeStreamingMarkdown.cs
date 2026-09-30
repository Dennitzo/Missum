using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>Native Markdown that retains its paragraphs and inline runs while text streams in.</summary>
public sealed partial class NativeStreamingMarkdown : StackPanel
{
    private readonly List<SectionView> _sections = [];
    private string _text = string.Empty;
    private bool _streaming;
    private readonly Run _cursor = new() { Text = "▍" };
    private readonly SolidColorBrush _cursorBrush = new();
    private readonly DispatcherTimer _cursorTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private InlineCollection? _cursorInlines;
    private readonly TextBlock _cursorHost = new() { FontSize = 16, LineHeight = 26, IsHitTestVisible = false };

    public void SetStreaming(bool streaming)
    {
        if (_streaming == streaming) return;
        _streaming = streaming;
        if (streaming) { AttachCursor(); if (IsLoaded) _cursorTimer.Start(); }
        else { _cursorTimer.Stop(); DetachCursor(); }
    }

    private void DetachCursor()
    {
        _cursorInlines?.Remove(_cursor);
        _cursorInlines = null;
        Children.Remove(_cursorHost);
    }

    private void AttachCursor()
    {
        if (!_streaming) return;
        if (Application.Current.Resources.TryGetValue("MissumAccentBrush", out var accent) && accent is SolidColorBrush brush)
            _cursorBrush.Color = brush.Color;
        else _cursorBrush.Color = Microsoft.UI.Colors.MediumPurple;
        _cursor.Foreground = _cursorBrush;
        // Keep the cursor on its own line, including after formulas and code blocks.
        Children.Add(_cursorHost);
        _cursorInlines = _cursorHost.Inlines;
        _cursorInlines.Add(_cursor);
    }


    public NativeStreamingMarkdown(string text)
    {
        Spacing = 9;
        _cursorTimer.Tick += (_, _) => _cursorBrush.Opacity = _cursorBrush.Opacity > 0 ? 0 : 1;
        Loaded += (_, _) => { if (_streaming) _cursorTimer.Start(); };
        Unloaded += (_, _) => _cursorTimer.Stop();
        UpdateText(text);
    }

    /// <summary>Updates on the owning UI thread without rebuilding unchanged text or code blocks.</summary>
    public void UpdateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_text, text, StringComparison.Ordinal)) return;
        DetachCursor();
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
        AttachCursor();
    }

    private static SectionView CreateSection(SectionKind kind)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = new FontFamily(kind == SectionKind.Code ? "Cascadia Mono" : "Segoe UI Variable Text"),
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            FontSize = kind == SectionKind.Code ? 15 : 16,
            LineHeight = 26,
            Foreground = Gray(kind == SectionKind.Code ? (byte)220 : (byte)248),
        };
        if (kind is SectionKind.Paragraph or SectionKind.Math) return new SectionView(text, text);

        var language = new TextBlock { FontSize = 12, Foreground = Gray(160), VerticalAlignment = VerticalAlignment.Center };
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
        var border = new Border { Child = panel, Background = Gray(32), CornerRadius = new CornerRadius(10), Padding = new Thickness(14) };
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
            section.Element = new ScrollViewer { Content = new NativeFormulaView(specification.Text, true, 20),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Margin = new Thickness(0, 6, 0, 6) };
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
            section.MathText.FontSize = specification.HeadingLevel > 0 ? 24 - specification.HeadingLevel : 16;
            section.MathText.FontWeight = specification.HeadingLevel > 0
                ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            section.MathText.UpdateTokenizedText(specification.Text, specification.MathTokenPrefix);
            section.Element = section.MathText;
            return;
        }
        section.MathText = null;
        section.Element = section.Text;
        section.Text.FontSize = specification.HeadingLevel > 0 ? 24 - specification.HeadingLevel : 16;
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
                InlineKind.Link => new Hyperlink { NavigateUri = piece.Uri, Inlines = { run } },
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

    private static List<SectionSpec> ParseSections(string text)
    {
        var result = new List<SectionSpec>();
        var pending = new StringBuilder();
        var tokenPrefix = MathTokenPrefix(text);
        foreach (var segment in NativeMathSyntax.Split(text))
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
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
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
            if (string.IsNullOrWhiteSpace(line)) continue;
            var heading = HeadingPattern().Match(line);
            var source = heading.Success ? heading.Groups[2].Value : BulletPattern().Replace(line, "•  ");
            // Only the global parser may recognize formulas. Re-parsing the raw line here
            // would turn examples inside multiline code or unfinished display math into TeX.
            result.Add(new SectionSpec(SectionKind.Paragraph, source, heading.Success ? heading.Groups[1].Length : 0, string.Empty,
                source.Contains(tokenPrefix, StringComparison.Ordinal) ? tokenPrefix : string.Empty));
        }
        // An opening fence is sufficient: live code must be visible before the closing fence arrives.
        if (insideCode) result.Add(new SectionSpec(SectionKind.Code, code.ToString().TrimEnd('\n'), 0, language));
        return result;
    }

    internal static (string Text, string Prefix) TokenizeMath(string text)
    {
        var prefix = MathTokenPrefix(text);
        var source = new StringBuilder();
        foreach (var segment in NativeMathSyntax.Split(text))
            source.Append(segment.IsMath ? EncodeMathToken(segment, prefix) : segment.Text);
        return (source.ToString(), prefix);
    }

    private static string MathTokenPrefix(string text)
    {
        var prefix = "\uE000MISSUM_MATH_";
        while (text.Contains(prefix, StringComparison.Ordinal)) prefix = "\uE000" + prefix;
        return prefix;
    }

    private static string EncodeMathToken(MathSegment segment, string prefix) => prefix + (segment.Display ? "D" : "I")
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(segment.Text)) + "\uE001";

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
                if (payload.Length < 2 || payload[0] is not ('I' or 'D'))
                {
                    result.Add(new(piece.Text[start..(end + 1)], false, false, piece.Kind, piece.Uri));
                    offset = end + 1;
                    continue;
                }
                try
                {
                    var latex = Encoding.UTF8.GetString(Convert.FromBase64String(payload[1..]));
                    result.Add(new(latex, true, payload[0] == 'D', piece.Kind, piece.Uri));
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

    private static SolidColorBrush Gray(byte value) => new(Color.FromArgb(255, value, value, value));

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^\s*[-*]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|`(?<code>[^`]+)`|\[(?<label>[^\]]+)\]\((?<uri>https?://[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlinePattern();

    private enum SectionKind { Paragraph, Code, Math }
    internal enum InlineKind { Plain, Bold, Code, Link }
    internal readonly record struct MathInlineSpec(string Text, bool IsMath, bool Display, InlineKind Kind, Uri? Uri);
    private readonly record struct SectionSpec(SectionKind Kind, string Text, int HeadingLevel, string Language, string MathTokenPrefix = "");
    private readonly record struct InlineSpec(InlineKind Kind, string Text, Uri? Uri = null);
    private sealed record InlineView(InlineKind Kind, Inline Element, Run Run);

    private sealed class SectionView(FrameworkElement element, TextBlock text)
    {
        public SectionSpec Specification { get; set; }
        public FrameworkElement Element { get; set; } = element;
        public NativeMathParagraph? MathText { get; set; }
        public TextBlock Text { get; } = text;
        public TextBlock? Language { get; init; }
        public ContentControl? CodeHost { get; init; }
        public NativeDiffView? Diff { get; set; }
        public NativeCodeHighlighter? CodeHighlighter { get; set; }
        public List<InlineView> Inlines { get; } = [];
    }
}

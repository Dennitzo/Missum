using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

public sealed partial class NativeStreamingMarkdown
{
    private (int Start, int End)? _spokenRange;
    private IReadOnlyList<SpeechSection?> _speechSections = [];
    private readonly List<SpeechHighlightView> _speechHighlights = [];
    private ScrollViewer? _highlightedFormula;
    private TextBlock? _speechFallback;

    internal void SetSpeechHighlight(int start, int length)
    {
        if (start < 0 || length <= 0 || start > Source.Length || length > Source.Length - start)
        {
            ClearSpeechHighlight();
            return;
        }

        var range = (start, start + length);
        if (_spokenRange == range) return;
        _spokenRange = range;
        ApplySpeechHighlight();
    }

    internal void ClearSpeechHighlight()
    {
        if (_spokenRange is null) return;
        _spokenRange = null;
        ApplySpeechHighlight();
    }

    internal bool TryGetSpeechSectionStart(FrameworkElement target, out int start)
    {
        for (DependencyObject? element = target; element is not null && element != this;
            element = VisualTreeHelper.GetParent(element))
        {
            var index = _sections.FindIndex(section => section.Element == element);
            if (index >= 0 && _speechSections[index] is { } location)
            {
                start = location.ToSource(0);
                return true;
            }
        }
        start = 0;
        return target == _speechFallback;
    }

    private void ApplySpeechHighlight()
    {
        if (_spokenRange is null && _speechHighlights.Count == 0 && _highlightedFormula is null) return;
        var next = new List<(IList<TextHighlighter> Collection, TextRange Range)>();
        ScrollViewer? formula = null;
        if (_spokenRange is { } range && Source.StartsWith(_publishedSource, StringComparison.Ordinal))
        {
            if (_speechFallback is { } fallback && range.Start < _publishedSource.Length)
            {
                next.Add((fallback.TextHighlighters, new TextRange { StartIndex = range.Start,
                    Length = Math.Min(range.End, _publishedSource.Length) - range.Start }));
            }

            for (var index = 0; index < _sections.Count; index++)
            {
                if (_speechSections[index] is not { } location || !location.Intersects(range.Start, range.End)) continue;
                var section = _sections[index];
                if (section.Table is { } table && location.Rows is { } rows)
                {
                    for (var row = 0; row < rows.Count; row++)
                    {
                        var (markup, rowLocation) = rows[row];
                        if (!rowLocation.Intersects(range.Start, range.End)) continue;
                        var cellRanges = ProjectTableRowSpeechRanges(markup, section.Specification.MathTokenPrefix,
                            rowLocation, range.Start, range.End);
                        foreach (var (cell, cellRange) in table.CellsInRow(row).Zip(cellRanges))
                            if (cellRange is { } visibleCell) next.Add((cell.TextHighlighters, visibleCell));
                    }
                }
                else if (section.Specification.Kind == SectionKind.Math && section.Element is ScrollViewer math)
                    formula = math;
                else if (ProjectSpeechRange(section.Specification, location, range.Start, range.End) is { } visible)
                    next.Add((section.MathText?.TextHighlighters ?? section.Text.TextHighlighters, visible));
            }
        }

        for (var index = _speechHighlights.Count - 1; index >= 0; index--)
        {
            var active = _speechHighlights[index];
            var match = next.FindIndex(item => ReferenceEquals(item.Collection, active.Collection)
                && item.Range.StartIndex == active.Range.StartIndex && item.Range.Length == active.Range.Length);
            if (match >= 0)
            {
                next.RemoveAt(match);
                continue;
            }
            active.Collection.Remove(active.Highlighter);
            _speechHighlights.RemoveAt(index);
        }
        foreach (var (collection, visibleRange) in next)
        {
            var highlighter = new TextHighlighter
            {
                Background = NativeThemeBrushes.Resource("MissumAccentBrush", Microsoft.UI.Colors.MediumPurple),
                Foreground = NativeThemeBrushes.Resource("MissumAccentTextBrush", Microsoft.UI.Colors.Black),
            };
            highlighter.Ranges.Add(visibleRange);
            collection.Add(highlighter);
            _speechHighlights.Add(new(collection, highlighter, visibleRange));
        }

        if (_highlightedFormula == formula) return;
        if (_highlightedFormula is not null) _highlightedFormula.ClearValue(Control.BackgroundProperty);
        _highlightedFormula = formula;
        if (formula is not null)
            formula.Background = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", Microsoft.UI.Colors.MediumPurple);
    }

    internal static IEnumerable<TextRange?> ProjectTableRowSpeechRanges(string markup, string mathTokenPrefix,
        SpeechSection row, int start, int end)
    {
        var sources = new List<TableCellSource>();
        var cells = SplitTableCells(markup, sources);
        for (var index = 0; index < cells.Count; index++)
        {
            var cell = cells[index];
            var location = new SpeechSection(row.Offsets, row.Start, cell.Length, Cell: sources[index]);
            yield return ProjectSpeechRange(new(SectionKind.Paragraph, cell, 0, string.Empty, mathTokenPrefix), location, start, end);
        }
    }

    internal static TextRange? ProjectSpeechRange(SectionSpec section, SpeechSection location, int start, int end)
    {
        if (section.Kind != SectionKind.Paragraph || !location.Intersects(start, end)) return null;
        IEnumerable<(int MarkupStart, int MarkupLength, bool Formula)> parts = section.MathTokenPrefix.Length == 0
            ? ParseInlines(section.Text).Select(piece => (piece.Start, piece.Text.Length, false))
            : ParseMathInlines(section.Text, section.MathTokenPrefix).Select(piece => (piece.MarkupStart, piece.MarkupLength, piece.IsMath));
        var offset = 0;
        var first = -1;
        var last = 0;
        foreach (var (markupStart, markupLength, formula) in parts)
        {
            // RichTextBlock's highlight indices count text and skip inline UI containers.
            if (formula) continue;
            var markupEnd = markupStart + markupLength;
            var visibleStart = Math.Max(markupStart, location.SyntheticPrefix);
            if (visibleStart < markupEnd)
            {
                var pieceStart = visibleStart - location.SyntheticPrefix;
                var pieceEnd = markupEnd - location.SyntheticPrefix;
                var rawStart = location.ToSource(pieceStart);
                var rawEnd = location.ToSource(pieceEnd);
                var overlapStart = Math.Max(start, rawStart);
                var overlapEnd = Math.Min(end, rawEnd);
                if (overlapStart < overlapEnd)
                {
                    var visibleOffset = offset + visibleStart - markupStart;
                    var startInPiece = location.MarkupDistance(pieceStart, pieceEnd, overlapStart);
                    var endInPiece = location.MarkupDistance(pieceStart, pieceEnd, overlapEnd);
                    if (startInPiece < endInPiece)
                    {
                        if (first < 0) first = visibleOffset + startInPiece;
                        last = visibleOffset + endInPiece;
                    }
                }
            }
            offset += markupLength;
        }

        return first < 0 ? null : new TextRange { StartIndex = first, Length = last - first };
    }

    internal sealed record SpeechSection(SpeechOffsets Offsets, int Start, int Length, int SyntheticPrefix = 0,
        IReadOnlyList<(string Markup, SpeechSection Location)>? Rows = null, TableCellSource? Cell = null)
    {
        internal bool Intersects(int start, int end) => ToSource(0) < end && ToSource(Length) > start;

        internal int ToSource(int index) => Offsets.ToSource(Start + (Cell?.ToRow(index) ?? index));

        internal int MarkupDistance(int start, int end, int rawPosition)
        {
            if (Cell?.Escapes is null) return rawPosition - ToSource(start);
            var low = start;
            var high = end;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (ToSource(middle) < rawPosition) low = middle + 1;
                else high = middle;
            }
            return low - start;
        }
    }

    internal sealed record TableCellSource(int Start, IReadOnlyList<int>? Escapes)
    {
        internal int ToRow(int index)
        {
            var position = Start + index;
            if (Escapes is null) return position;
            foreach (var escape in Escapes)
            {
                if (escape > position) break;
                position++;
            }
            return position;
        }
    }

    /// <summary>Maps tokenized Markdown positions back to the original message, across inline math replacements.</summary>
    internal sealed class SpeechOffsets(int sourceStart)
    {
        private readonly List<(int TokenEnd, int SourceEnd)> _changes = [];

        internal void Replace(int tokenStart, int tokenLength, int start, int length) =>
            _changes.Add((tokenStart + tokenLength, start + length));

        internal int ToSource(int position)
        {
            // Projection only visits boundaries of complete tokens, never a character inside one.
            var low = 0;
            var high = _changes.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (_changes[middle].TokenEnd <= position) low = middle + 1;
                else high = middle;
            }
            if (low == 0) return sourceStart + position;
            var change = _changes[low - 1];
            return change.SourceEnd + position - change.TokenEnd;
        }
    }

    private sealed record SpeechHighlightView(IList<TextHighlighter> Collection, TextHighlighter Highlighter, TextRange Range);
}

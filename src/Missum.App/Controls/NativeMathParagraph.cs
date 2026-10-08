using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

/// <summary>RichTextBlock supplies true inline UI containers, so formulas wrap with the surrounding sentence.</summary>
public sealed class NativeMathParagraph : UserControl
{
    private readonly Paragraph _paragraph = new();
    private readonly RichTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, LineHeight = 26 };
    private readonly List<(NativeStreamingMarkdown.MathInlineSpec Spec, Inline Inline, Run? Run)> _runs = [];
    private double _renderedFontSize;
    internal InlineCollection TrailingInlines => _paragraph.Inlines;
    internal TextAlignment TextAlignment { get => _text.TextAlignment; set => _text.TextAlignment = value; }

    public NativeMathParagraph()
    {
        FontFamily = new FontFamily("Segoe UI Variable Text");
        FontSize = 16;
        FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        Foreground = NativeThemeBrushes.Text;
        _text.FontFamily = FontFamily;
        _text.Blocks.Add(_paragraph);
        Content = _text;
    }

    public void UpdateText(string text)
    {
        var tokenized = NativeStreamingMarkdown.TokenizeMath(text);
        UpdateTokenizedText(tokenized.Text, tokenized.Prefix);
    }

    internal void UpdateTokenizedText(string text, string mathTokenPrefix)
    {
        var fontChanged = _renderedFontSize != FontSize;
        _text.FontSize = FontSize;
        _text.FontWeight = FontWeight;
        _text.Foreground = Foreground;
        _text.FontFamily = FontFamily;
        _renderedFontSize = FontSize;
        var pieces = NativeStreamingMarkdown.ParseMathInlines(text, mathTokenPrefix);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (i < _runs.Count && _runs[i].Spec == piece && (!piece.IsMath || !fontChanged)) continue;
            if (!piece.IsMath && i < _runs.Count && _runs[i].Run is { } oldText && _runs[i].Spec.Kind == piece.Kind)
            {
                oldText.Text = piece.Text;
                if (_runs[i].Inline is Hyperlink link) link.NavigateUri = piece.Uri;
                _runs[i] = (piece, _runs[i].Inline, oldText);
                continue;
            }
            Run? run = null;
            Inline content;
            if (piece.IsMath)
                content = new InlineUIContainer { Child = new NativeFormulaView(piece.Text, piece.Display, FontSize, piece.Uri, piece.RenderLatex) };
            else
            {
                run = new Run { Text = piece.Text, FontFamily = new FontFamily(piece.Kind == NativeStreamingMarkdown.InlineKind.Code ? "Cascadia Mono" : "Segoe UI Variable Text") };
                content = run;
            }
            Inline inline = piece.Kind switch
            {
                NativeStreamingMarkdown.InlineKind.Bold => new Bold { Inlines = { content } },
                // WinUI Hyperlink rejects InlineUIContainer; the native formula button owns the link instead.
                NativeStreamingMarkdown.InlineKind.Link when !piece.IsMath => new Hyperlink { NavigateUri = piece.Uri, Foreground = NativeThemeBrushes.ReadableAccent, Inlines = { content } },
                _ => content,
            };
            if (i < _runs.Count) { _paragraph.Inlines.RemoveAt(i); _paragraph.Inlines.Insert(i, inline); _runs[i] = (piece, inline, run); }
            else { _paragraph.Inlines.Add(inline); _runs.Add((piece, inline, run)); }
        }
        while (_runs.Count > pieces.Count) { _paragraph.Inlines.RemoveAt(_runs.Count - 1); _runs.RemoveAt(_runs.Count - 1); }
        // A fraction may need more depth than a text glyph. Reserve its natural height
        // after baseline alignment rather than shrinking or clipping the formula image.
        var formulaHeight = _runs.Select(run => FormulaHeight(run.Inline)).DefaultIfEmpty().Max();
        _text.LineHeight = Math.Max(FontSize * 26 / 16, formulaHeight + FontSize * 0.2);
    }

    private static double FormulaHeight(Inline inline) => inline switch
    {
        InlineUIContainer { Child: NativeFormulaView formula } => formula.RenderedHeight,
        Span span => span.Inlines.Select(FormulaHeight).DefaultIfEmpty().Max(),
        _ => 0
    };
}

using CSharpMath.Atom;
using CSharpMath.Atom.Atoms;
using CSharpMath.Rendering.FrontEnd;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using DrawingColor = System.Drawing.Color;

namespace Missum.App.Controls;

/// <summary>Extends CSharpMath with measured, padded TeX boxes, including nested boxes.</summary>
internal sealed class NativeBoxedMathPainter : MathPainter
{
    // A background tag, never painted. The two rules reserve vertical space in the
    // typesetter itself; adding a rectangle after rasterization would not do that.
    private static readonly DrawingColor BoxMarker = DrawingColor.FromArgb(1, 77, 105, 115);
    private static bool _registered;

    internal static void RegisterCommands()
    {
        lock (LaTeXSettings.Commands)
        {
            if (_registered) return;
            if (!LaTeXSettings.Commands.Any(pair => pair.Key == @"\boxed"))
            {
                LaTeXSettings.Commands.Add(@"\boxed", (parser, accumulate, stopChar) =>
                {
                    parser.SkipSpaces();
                    if (!parser.HasCharacters || (stopChar != '\0' && parser.Chars[parser.NextChar] == stopChar))
                        return LaTeXSettings.Err(@"Missing argument for \boxed");
                    return parser.ReadArgument().Bind(inner =>
                    {
                        // A bare leading/trailing Space does not generate a
                        // display. CSharpMath's CollectionWidth subtracts the
                        // first display's X and therefore trims those margins.
                        // Empty, zero-raised displays retain both measured
                        // endpoints; the body stays its own math list so its
                        // leading/trailing operators acquire no extra spacing.
                        var zero = Length.Point * 0;
                        var padded = new MathList(
                            new RaiseBox(zero, new MathList()),
                            new Space(Length.EmWidth / 4),
                            new RaiseBox(zero, inner),
                            new Space(Length.EmWidth / 4),
                            new RaiseBox(zero, new MathList()));
                        // Overline is the outer atom, so scripts are laid out using
                        // the full box ascent/descent. ColorBox restores uncramped
                        // typesetting for the body and identifies only this frame.
                        return LaTeXSettings.Ok(new Overline(new MathList(
                            new ColorBox(BoxMarker, new MathList(new Underline(padded))))));
                    });
                });
            }
            _registered = true;
        }
    }

    public override ICanvas WrapCanvas(SKCanvas canvas) => new BoxCanvas(canvas, AntiAlias);

    private sealed class BoxCanvas(SKCanvas canvas, bool antiAlias) : ICanvas
    {
        private readonly SkiaCanvas _inner = new(canvas, antiAlias);
        private readonly List<PendingBox> _boxes = [];

        public float Width => _inner.Width;
        public float Height => _inner.Height;
        public DrawingColor DefaultColor { get => _inner.DefaultColor; set => _inner.DefaultColor = value; }
        public DrawingColor? CurrentColor { get => _inner.CurrentColor; set => _inner.CurrentColor = value; }
        public PaintStyle CurrentStyle { get => _inner.CurrentStyle; set => _inner.CurrentStyle = value; }
        public CSharpMath.Rendering.FrontEnd.Path StartNewPath() => _inner.StartNewPath();
        public void StrokeRect(float left, float top, float width, float height) => _inner.StrokeRect(left, top, width, height);
        public void Save() => _inner.Save();
        public void Translate(float dx, float dy) => _inner.Translate(dx, dy);
        public void Scale(float sx, float sy) => _inner.Scale(sx, sy);
        public void Restore() => _inner.Restore();

        public void FillRect(float left, float top, float width, float height)
        {
            if (CurrentColor == BoxMarker)
            {
                // Store device coordinates: inner boxes and fraction/script
                // layout change the canvas transform before their rules draw.
                var bounds = canvas.TotalMatrix.MapRect(SKRect.Create(left, top, width, height));
                _boxes.Add(new PendingBox(bounds));
                return;
            }
            _inner.FillRect(left, top, width, height);
        }

        public void DrawLine(float x1, float y1, float x2, float y2, float lineThickness)
        {
            _inner.DrawLine(x1, y1, x2, y2, lineThickness);
            if (_boxes.Count == 0 || lineThickness <= 0) return;

            var matrix = canvas.TotalMatrix;
            var start = matrix.MapPoint(x1, y1);
            var end = matrix.MapPoint(x2, y2);
            if (Math.Abs(start.Y - end.Y) > .01f) return;
            var thicknessVector = matrix.MapPoint(x1, y1 + lineThickness);
            var thickness = Math.Abs(thicknessVector.Y - start.Y);
            if (thickness <= 0 || !float.IsFinite(thickness)) return;
            var left = Math.Min(start.X, end.X);
            var right = Math.Max(start.X, end.X);
            var epsilon = Math.Max(.02f, thickness / 32);

            for (var index = _boxes.Count - 1; index >= 0; index--)
            {
                var box = _boxes[index];
                if (Math.Abs(left - box.Bounds.Left) > epsilon || Math.Abs(right - box.Bounds.Right) > epsilon)
                    continue;

                if (box.BottomRule is null)
                {
                    // Underline's measured descent includes half its rule.
                    if (Math.Abs(start.Y + thickness / 2 - box.Bounds.Bottom) <= epsilon)
                        box.BottomRule = start.Y;
                    continue;
                }

                // The enclosing Overline is outside ColorBox's body bounds and
                // is drawn last. Inner formulas' own rules cannot close this box.
                if (start.Y >= box.Bounds.Top - epsilon) continue;
                DrawSides(box.Bounds, start.Y, box.BottomRule.Value, thickness);
                _boxes.RemoveAt(index);
                break;
            }
        }

        private void DrawSides(SKRect bounds, float top, float bottom, float thickness)
        {
            var color = CurrentColor ?? DefaultColor;
            using var paint = new SKPaint
            {
                Color = new SKColor(color.R, color.G, color.B, color.A),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = thickness,
                IsAntialias = antiAlias,
                StrokeCap = SKStrokeCap.Butt
            };
            canvas.Save();
            try
            {
                canvas.ResetMatrix();
                // Keep strokes inside the measured width and join the existing
                // top/bottom rules at their centers without cropping the corners.
                canvas.DrawLine(bounds.Left + thickness / 2, top, bounds.Left + thickness / 2, bottom, paint);
                canvas.DrawLine(bounds.Right - thickness / 2, top, bounds.Right - thickness / 2, bottom, paint);
            }
            finally { canvas.Restore(); }
        }

        private sealed class PendingBox(SKRect bounds)
        {
            internal SKRect Bounds { get; } = bounds;
            internal float? BottomRule { get; set; }
        }
    }
}

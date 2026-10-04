using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using System.Numerics;
using NativePath = Microsoft.UI.Xaml.Shapes.Path;

namespace Missum.App.Controls;

/// <summary>A stable, decorative planet identity drawn entirely with native vectors.</summary>
public sealed class NativeSubagentAvatar : Grid
{
    private readonly Canvas _drawing = new() { Width = 32, Height = 32 };
    private NativePlanetPalette.Appearance _appearance;

    internal int Variant { get; private set; } = -1;
    internal Color IconColor => _appearance.Body;
    internal const int PaletteCount = NativePlanetPalette.PaletteCount;
    internal const string PaletteVersion = NativePlanetPalette.PaletteVersion;

    public NativeSubagentAvatar(string agentId, double size = 14, int? planetIndex = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Width = Height = size;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _drawing });
        SetAgentId(agentId, planetIndex);
    }

    internal void SetAgentId(string agentId, int? planetIndex = null)
    {
        var variant = planetIndex ?? VariantFor(agentId);
        ArgumentOutOfRangeException.ThrowIfNegative(variant);
        if (variant == Variant) return;
        Variant = variant;
        _appearance = NativePlanetPalette.Describe(variant);
        _drawing.Children.Clear();
        DrawPlanet();
    }

    internal static int VariantFor(string agentId)
    {
        // Real agents receive a persisted index. This fallback remains stable for decorative previews.
        var hash = 2166136261u;
        foreach (var character in agentId ?? string.Empty)
            hash = unchecked((hash ^ character) * 16777619u);
        return (int)(hash & int.MaxValue);
    }

    private void DrawPlanet()
    {
        var appearance = _appearance;
        if (appearance.Orbit > 0) DrawOrbit(front: false);

        var fill = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        fill.GradientStops.Add(new GradientStop { Color = appearance.Light, Offset = 0 });
        fill.GradientStops.Add(new GradientStop { Color = appearance.Body, Offset = .48 });
        fill.GradientStops.Add(new GradientStop { Color = appearance.Dark, Offset = 1 });
        var body = AddEllipse(_drawing, 16, 16, 22, 22, appearance.Body);
        body.Fill = fill;

        var surface = new Canvas { Width = 32, Height = 32 };
        _drawing.Children.Add(surface);
        var surfaceVisual = ElementCompositionPreview.GetElementVisual(surface);
        var boundary = surfaceVisual.Compositor.CreateEllipseGeometry();
        boundary.Center = new Vector2(16, 16);
        boundary.Radius = new Vector2(10.8f, 10.8f);
        surfaceVisual.Clip = surfaceVisual.Compositor.CreateGeometricClip(boundary);
        DrawSurface(surface);
        AddEllipse(_drawing, 11.7, 9.9, 3.4, 1.8, appearance.Light).Opacity = .6;
        if (appearance.Orbit > 0) DrawOrbit(front: true);
        DrawSatellites();
    }

    private void DrawSurface(Canvas surface)
    {
        var light = _appearance.Accent;
        var dark = _appearance.Dark;
        switch (_appearance.Surface)
        {
            case 0: // Oceans and irregular continents.
                var land = NativePlanetPalette.Mix(_appearance.Body, Color.FromArgb(255, 164, 226, 165), .62);
                AddPolygon(surface, land, new(8, 6), new(15, 7), new(13, 11), new(16, 15), new(12, 18), new(8, 14));
                AddPolygon(surface, land, new(20, 16), new(25, 15), new(25, 22), new(19, 25), new(18, 21));
                AddEllipse(surface, 20, 9, 4.2, 2.7, land);
                break;
            case 1: // Gas bands.
                for (var band = 0; band < 4; band++)
                    AddCurve(surface, band % 2 == 0 ? light : dark, 2.4,
                        new(4, 9 + band * 4.3), new(12, 5 + band * 4.3), new(20, 13 + band * 4.3), new(28, 9 + band * 4.3));
                AddEllipse(surface, 21, 18, 5.2, 2.4, dark);
                break;
            case 2: // Rocky crater fields.
                AddCrater(surface, 11, 11, 5.5);
                AddCrater(surface, 20, 16, 7);
                AddCrater(surface, 12, 23, 4.8);
                AddCrater(surface, 22, 8, 3.1);
                break;
            case 3: // Warm fault lines.
                var lava = NativePlanetPalette.Mix(light, Color.FromArgb(255, 255, 172, 79), .58);
                AddPolyline(surface, lava, 2, new(8, 5), new(13, 12), new(10, 18), new(15, 27));
                AddPolyline(surface, lava, 1.8, new(24, 6), new(19, 13), new(23, 20), new(20, 27));
                AddPolyline(surface, lava, 1.5, new(11, 17), new(19, 14), new(25, 17));
                break;
            case 4: // Ice caps and glacier veins.
                AddPolygon(surface, light, new(4, 4), new(28, 4), new(24, 10), new(19, 8), new(16, 12), new(10, 9));
                AddPolygon(surface, light, new(4, 28), new(28, 28), new(23, 22), new(18, 25), new(12, 21), new(8, 24));
                AddPolyline(surface, light, 1.3, new(8, 15), new(13, 17), new(17, 14));
                AddPolyline(surface, dark, 1.1, new(19, 12), new(18, 18), new(23, 20));
                break;
            case 5: // Atmospheric spirals.
                AddCurve(surface, light, 3.2, new(6, 17), new(8, 3), new(27, 4), new(25, 18));
                AddCurve(surface, dark, 3.1, new(25, 18), new(21, 31), new(6, 25), new(10, 15));
                AddCurve(surface, light, 2.2, new(10, 15), new(14, 9), new(23, 13), new(17, 20));
                break;
            case 6: // Oblique dune ridges.
                for (var ridge = 0; ridge < 4; ridge++)
                    AddPolyline(surface, ridge % 2 == 0 ? light : dark, 2.3,
                        new(3 + ridge * 5, 29), new(7 + ridge * 5, 16), new(16 + ridge * 5, 3));
                break;
            default: // Faceted mineral continents.
                AddPolygon(surface, light, new(5, 7), new(16, 5), new(14, 15), new(6, 19));
                AddPolygon(surface, dark, new(16, 5), new(27, 11), new(22, 18), new(14, 15));
                AddPolygon(surface, NativePlanetPalette.Mix(light, _appearance.Body, .5), new(14, 15), new(22, 18), new(19, 27), new(9, 24));
                AddPolyline(surface, light, 1.1, new(5, 20), new(14, 15), new(20, 28));
                break;
        }
    }

    private void DrawOrbit(bool front)
    {
        var orbit = _appearance.Orbit;
        var angle = orbit switch { 2 => -32, 3 => 32, 4 => -12, 5 => 64, 6 => -21, 7 => 17, _ => 0 };
        var height = orbit switch { 4 => 4, 5 => 8, 7 => 24, _ => 10 };
        var width = orbit == 7 ? 29 : 29.8;
        var ringColor = NativePlanetPalette.Mix(_appearance.Accent, _appearance.Body, front ? .12 : .6);
        if (!front)
        {
            var ellipse = new Ellipse
            {
                Width = width,
                Height = height,
                Stroke = new SolidColorBrush(ringColor),
                StrokeThickness = orbit == 6 ? 2.8 : 1.9,
                RenderTransform = new RotateTransform { Angle = angle, CenterX = width / 2d, CenterY = height / 2d },
            };
            if (orbit == 7) ellipse.StrokeDashArray = new DoubleCollection { 1.1, 1.5 };
            Canvas.SetLeft(ellipse, 16 - width / 2d);
            Canvas.SetTop(ellipse, 16 - height / 2d);
            _drawing.Children.Add(ellipse);
            return;
        }
        var path = AddCurve(_drawing, ringColor, orbit == 6 ? 2.8 : 1.9,
            new(16 - width / 2d, 16), new(16 - width / 2d, 16 + height * .69),
            new(16 + width / 2d, 16 + height * .69), new(16 + width / 2d, 16));
        path.RenderTransform = new RotateTransform { Angle = angle, CenterX = 16, CenterY = 16 };
        if (orbit == 7) path.StrokeDashArray = new DoubleCollection { 1.1, 1.5 };
        if (orbit == 6)
        {
            var inner = AddCurve(_drawing, _appearance.Dark, .8,
                new(16 - width / 2d, 16), new(16 - width / 2d, 16 + height * .69),
                new(16 + width / 2d, 16 + height * .69), new(16 + width / 2d, 16));
            inner.RenderTransform = new RotateTransform { Angle = angle, CenterX = 16, CenterY = 16 };
        }
    }

    private void DrawSatellites()
    {
        switch (_appearance.Satellite)
        {
            case 1:
                AddEllipse(_drawing, 28.1, 4.1, 4.2, 4.2, _appearance.Accent);
                AddEllipse(_drawing, 28.7, 4.6, 1.5, 1.5, _appearance.Dark);
                break;
            case 2:
                AddEllipse(_drawing, 3.4, 27.5, 3.3, 3.3, _appearance.Light);
                AddEllipse(_drawing, 6.8, 30, 2, 2, _appearance.Accent);
                break;
            case 3:
                AddPolyline(_drawing, _appearance.Accent, 1.5, new(28.5, 1.7), new(28.5, 7));
                AddPolyline(_drawing, _appearance.Accent, 1.5, new(25.8, 4.3), new(31.1, 4.3));
                AddEllipse(_drawing, 3.1, 27.7, 2.8, 2.8, _appearance.Light);
                break;
        }
    }

    private void AddCrater(Canvas surface, double x, double y, double size)
    {
        AddEllipse(surface, x, y, size, size, _appearance.Dark);
        AddEllipse(surface, x - .6, y - .7, size - 1.6, size - 1.6,
            NativePlanetPalette.Mix(_appearance.Body, _appearance.Light, .2));
    }

    private static Ellipse AddEllipse(Canvas canvas, double centerX, double centerY, double width, double height, Color color)
    {
        var ellipse = new Ellipse { Width = width, Height = height, Fill = new SolidColorBrush(color) };
        Canvas.SetLeft(ellipse, centerX - width / 2);
        Canvas.SetTop(ellipse, centerY - height / 2);
        canvas.Children.Add(ellipse);
        return ellipse;
    }

    private static void AddPolygon(Canvas canvas, Color color, params Point[] points)
    {
        var polygon = new Polygon { Fill = new SolidColorBrush(color), Points = new PointCollection() };
        foreach (var point in points) polygon.Points.Add(point);
        canvas.Children.Add(polygon);
    }

    private static void AddPolyline(Canvas canvas, Color color, double thickness, params Point[] points)
    {
        var polyline = new Polyline
        {
            Stroke = new SolidColorBrush(color), StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round, Points = new PointCollection(),
        };
        foreach (var point in points) polyline.Points.Add(point);
        canvas.Children.Add(polyline);
    }

    private static NativePath AddCurve(Canvas canvas, Color color, double thickness,
        Point start, Point first, Point second, Point end)
    {
        var figure = new PathFigure { StartPoint = start };
        figure.Segments.Add(new BezierSegment { Point1 = first, Point2 = second, Point3 = end });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var path = new NativePath { Data = geometry, Stroke = new SolidColorBrush(color), StrokeThickness = thickness };
        canvas.Children.Add(path);
        return path;
    }
}

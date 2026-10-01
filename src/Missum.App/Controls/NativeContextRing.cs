using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Missum.App.Controls;

public sealed class NativeContextRing : Grid
{
    private readonly Ellipse _track = new() { StrokeThickness = 2.5, Margin = new Thickness(2) };
    private readonly Microsoft.UI.Xaml.Shapes.Path _used = new() { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private double _value;
    public double Maximum { get; set; } = 100;
    public double Value { get => _value; set { _value = value; Render(); } }

    public NativeContextRing()
    {
        Width = Height = 20; IsHitTestVisible = false;
        Children.Add(_track); Children.Add(_used); Loaded += (_, _) => Render();
    }

    private void Render()
    {
        var resources = Application.Current.Resources;
        _track.Stroke = resources.TryGetValue("MissumStrokeBrush", out var track) && track is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        _used.Stroke = resources.TryGetValue("MissumAccentBrush", out var accent) && accent is Brush accentBrush ? accentBrush : new SolidColorBrush(Microsoft.UI.Colors.MediumPurple);
        var ratio = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        _used.Visibility = ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        var angle = Math.Max(.02, Math.Min(.9999, ratio)) * 2 * Math.PI;
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new(10, 2) };
        figure.Segments.Add(new ArcSegment { Point = new(10 + 8 * Math.Sin(angle), 10 - 8 * Math.Cos(angle)), Size = new(8, 8), IsLargeArc = angle > Math.PI, SweepDirection = SweepDirection.Clockwise });
        geometry.Figures.Add(figure); _used.Data = geometry;
    }
}

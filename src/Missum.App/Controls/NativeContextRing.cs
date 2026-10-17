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
    private double _maximum = 100;
    private double _renderedRatio = double.NaN;
    private App? _themeApplication;
    public double Maximum
    {
        get => _maximum;
        set { if (_maximum.Equals(value)) return; _maximum = value; Render(); }
    }
    public double Value
    {
        get => _value;
        set { if (_value.Equals(value)) return; _value = value; Render(); }
    }

    public NativeContextRing()
    {
        Width = Height = 20; IsHitTestVisible = false;
        Children.Add(_track); Children.Add(_used);
        Loaded += (_, _) =>
        {
            if (_themeApplication is null && Application.Current is App application)
            {
                _themeApplication = application;
                application.ThemeChanged += OnPaletteChanged;
            }
            Render();
        };
        Unloaded += (_, _) =>
        {
            if (_themeApplication is { } application) application.ThemeChanged -= OnPaletteChanged;
            _themeApplication = null;
        };
        ActualThemeChanged += (_, _) => RefreshBrushes();
    }

    private void OnPaletteChanged(object? sender, EventArgs args) => RefreshBrushes();

    private void RefreshBrushes()
    {
        var resources = Application.Current.Resources;
        var trackBrush = resources.TryGetValue("MissumStrokeBrush", out var track) && track is Brush brush ? brush : new SolidColorBrush(Microsoft.UI.Colors.Gray);
        var usedBrush = NativeIconPalette.BrushFor("navigation");
        if (!ReferenceEquals(_track.Stroke, trackBrush)) _track.Stroke = trackBrush;
        if (!ReferenceEquals(_used.Stroke, usedBrush)) _used.Stroke = usedBrush;
    }

    private void Render()
    {
        RefreshBrushes();
        var ratio = Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (_renderedRatio.Equals(ratio)) return;
        _renderedRatio = ratio;
        var visibility = ratio > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_used.Visibility != visibility) _used.Visibility = visibility;
        var angle = Math.Max(.02, Math.Min(.9999, ratio)) * 2 * Math.PI;
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = new(10, 2) };
        figure.Segments.Add(new ArcSegment { Point = new(10 + 8 * Math.Sin(angle), 10 - 8 * Math.Cos(angle)), Size = new(8, 8), IsLargeArc = angle > Math.PI, SweepDirection = SweepDirection.Clockwise });
        geometry.Figures.Add(figure); _used.Data = geometry;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>A decorative track around a real native slider; input and accessibility remain native.</summary>
internal sealed class NativeComposerSlider : Grid
{
    private readonly Slider _slider;
    private readonly Border _fill;
    private readonly Canvas _highlights = new() { Height = 24, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly SolidColorBrush _accent = NativeThemeBrushes.Resource("MissumAccentBrush", Microsoft.UI.Colors.MediumPurple);
    private readonly LinearGradientBrush _gradient = new() { StartPoint = new(0, .5), EndPoint = new(1, .5) };
    private readonly int _stops;
    private long? _accentCallback;

    internal NativeComposerSlider(Slider slider, int stops = 0)
    {
        ArgumentNullException.ThrowIfNull(slider);
        _slider = slider;
        _stops = Math.Max(0, stops);
        MinHeight = 32;
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        slider.Height = 32;
        slider.MinHeight = 32;
        slider.Margin = new(0);
        slider.Padding = new(0);
        slider.Foreground = transparent;
        slider.Background = transparent;
        slider.CornerRadius = new(14);
        slider.Resources["SliderTrackThemeHeight"] = 24d;
        slider.Resources["SliderPreContentMargin"] = 4d;
        slider.Resources["SliderPostContentMargin"] = 4d;
        slider.Resources["SliderHorizontalHeight"] = 32d;
        slider.Resources["SliderHorizontalThumbWidth"] = 28d;
        slider.Resources["SliderHorizontalThumbHeight"] = 28d;
        slider.Resources["SliderThumbCornerRadius"] = new CornerRadius(16);
        slider.Resources["SliderThumbBorderBrush"] = transparent;
        slider.Resources["SliderOuterThumbBackground"] = transparent;
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            slider.Resources["SliderTrackValueFill" + state] = transparent;
            slider.Resources["SliderTrackFill" + state] = transparent;
            slider.Resources["SliderThumbBackground" + state] = transparent;
        }
        Children.Add(new Border { Height = 24, CornerRadius = new(12), VerticalAlignment = VerticalAlignment.Center,
            Background = NativeThemeBrushes.Resource("MissumHoverBrush", 69), IsHitTestVisible = false });
        _fill = new Border { Height = 24, CornerRadius = new(12), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, Background = _gradient, IsHitTestVisible = false };
        Children.Add(_fill);
        Children.Add(_highlights);
        Children.Add(slider);
        slider.ValueChanged += (_, _) => RefreshTrack();
        SizeChanged += (_, _) => RefreshTrack();
        Loaded += (_, _) =>
        {
            _accentCallback ??= _accent.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => RefreshPalette());
            RefreshPalette();
            RefreshTrack();
        };
        Unloaded += (_, _) =>
        {
            if (_accentCallback is not { } callback) return;
            _accent.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, callback);
            _accentCallback = null;
        };
        RefreshPalette();
    }

    private void RefreshPalette()
    {
        var accent = _accent.Color;
        _gradient.GradientStops.Clear();
        _gradient.GradientStops.Add(new() { Offset = 0, Color = Mix(accent, Microsoft.UI.Colors.Black, .24) });
        _gradient.GradientStops.Add(new() { Offset = .48, Color = Mix(accent, Microsoft.UI.Colors.White, .28) });
        _gradient.GradientStops.Add(new() { Offset = 1, Color = accent });
    }

    private void RefreshTrack()
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var fraction = Math.Clamp((_slider.Value - _slider.Minimum) / Math.Max(1, _slider.Maximum - _slider.Minimum), 0, 1);
        var fillWidth = 14 + Math.Max(0, width - 28) * fraction;
        _fill.Width = Math.Min(width, fillWidth + (fraction >= 1 ? 14 : 0));
        _highlights.Width = width;
        _highlights.Children.Clear();
        for (var index = 0; index < 19; index++)
        {
            var x = 9 + Math.Max(0, width - 18) * ((index * 37 % 101) / 101d);
            if (x > fillWidth - 11) continue;
            var size = index % 5 == 0 ? 2.5 : 1.5;
            var dot = new Ellipse { Width = size, Height = size, Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Opacity = index % 3 == 0 ? .7 : .3 };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, 4 + (index * 7 % 16));
            _highlights.Children.Add(dot);
        }
        for (var index = 0; index < _stops; index++)
        {
            var value = _stops <= 1 ? 0 : index / (double)(_stops - 1);
            if (Math.Abs(value - fraction) < .025) continue;
            var dot = new Ellipse { Width = 3, Height = 3, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = .45 };
            Canvas.SetLeft(dot, 12.5 + Math.Max(0, width - 28) * value);
            Canvas.SetTop(dot, 10.5);
            _highlights.Children.Add(dot);
        }
    }

    private static Color Mix(Color color, Color other, double amount) => Color.FromArgb(255,
        (byte)Math.Round(color.R + (other.R - color.R) * amount),
        (byte)Math.Round(color.G + (other.G - color.G) * amount),
        (byte)Math.Round(color.B + (other.B - color.B) * amount));
}

using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Missum.App;

public sealed partial class MainWindow
{
    private Grid? _liquidGlassScene;
    private SolidColorBrush? _liquidGlassSceneBase;
    private readonly List<RadialGradientBrush> _liquidGlassLights = [];

    /// <summary>True while the glass scene with its light fields is shown.</summary>
    internal bool IsLiquidGlassShown => _liquidGlassScene is not null && App.Current.LiquidGlass is not null;

    /// <summary>Smoke-only access to the real navigation path and the page it shows.</summary>
    internal Task NavigateForSmokeAsync(string route) => NavigateToAsync(route);

    internal object? CurrentPage => ContentFrame.Content;

    /// <summary>
    /// Liquid Glass is a startup decision (the setting applies after a restart). The
    /// glass panes are native in-app acrylic, which can only frost what the app draws
    /// itself, so the window gets an opaque scene behind all content: the classic window
    /// color plus soft light fields in the accent and background colors. The original
    /// Mica backdrop stays untouched; without the setting nothing here is created.
    /// </summary>
    private void InstallLiquidGlassScene()
    {
        if (!App.Current.IsLiquidGlassEnabled || Content is not Grid root) return;
        _liquidGlassSceneBase = new SolidColorBrush(Microsoft.UI.Colors.Black);
        _liquidGlassScene = new Grid { IsHitTestVisible = false, Background = _liquidGlassSceneBase };
        var lightCount = LiquidGlassAppearance.SceneLights(false, 0, 0).Count;
        for (var index = 0; index < lightCount; index++)
        {
            var light = new RadialGradientBrush { MappingMode = BrushMappingMode.RelativeToBoundingBox };
            _liquidGlassLights.Add(light);
            _liquidGlassScene.Children.Add(new Rectangle { Fill = light });
        }
        Grid.SetRowSpan(_liquidGlassScene, Math.Max(1, root.RowDefinitions.Count));
        root.Children.Insert(0, _liquidGlassScene);
    }

    /// <summary>Keeps the scene colors in step with theme, accent and background changes.</summary>
    private void UpdateLiquidGlass()
    {
        if (_liquidGlassScene is null || _liquidGlassSceneBase is null) return;
        if (App.Current.LiquidGlass is not { } glass)
        {
            // High contrast: a plain opaque contrast background without lights.
            _liquidGlassSceneBase.Color = App.Current.ResolveHighContrastPalette().Background;
            foreach (var light in _liquidGlassLights) light.GradientStops.Clear();
            return;
        }

        _liquidGlassSceneBase.Color = Color(LiquidGlassAppearance.SceneBase(Argb(glass.Window)));
        var lights = LiquidGlassAppearance.SceneLights(glass.Light, Argb(glass.Accent), Argb(glass.Background));
        for (var index = 0; index < _liquidGlassLights.Count && index < lights.Count; index++)
        {
            var brush = _liquidGlassLights[index];
            var light = lights[index];
            brush.Center = new Point(light.CenterX, light.CenterY);
            brush.GradientOrigin = brush.Center;
            brush.RadiusX = light.RadiusX;
            brush.RadiusY = light.RadiusY;
            var color = Color(light.Color);
            brush.GradientStops.Clear();
            brush.GradientStops.Add(new GradientStop { Color = color, Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(color, (byte)(color.A / 3)), Offset = 0.55 });
            brush.GradientStops.Add(new GradientStop { Color = WithAlpha(color, 0), Offset = 1 });
        }
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) => Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static uint Argb(Windows.UI.Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static Windows.UI.Color Color(uint argb) =>
        Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

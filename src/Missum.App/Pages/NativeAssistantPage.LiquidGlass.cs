using System.Numerics;
using Missum.Core.Models;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private const float ComposerGlassRadius = 26f;
    private ComposerGlassLights? _composerGlassLights;
    private Thickness _conversationGlassBasePadding;

    /// <summary>
    /// Liquid Glass chrome (startup decision, applies after a restart): the sidebar and
    /// inspector float as rounded native glass panes, and the composer floats over the
    /// conversation so messages scroll beneath its glass. Behind the composer, composition
    /// visuals add a lift shadow, an accent glow, an accent reflection near the send button
    /// and a top sheen; the composer acrylic frosts them, so the accent shows in the glass.
    /// Without the setting nothing here changes the classic layout.
    /// </summary>
    private void ApplyLiquidGlassChrome()
    {
        if (!App.Current.IsLiquidGlassEnabled) return;
        var pane = GlassResource("MissumGlassPaneBrush");
        var control = GlassResource("MissumGlassControlBrush");
        var input = GlassResource("MissumGlassInputBrush");
        var rim = GlassResource("MissumGlassRimBrush");
        var accentRim = GlassResource("MissumGlassAccentRimBrush");
        if (pane is null || control is null || input is null || rim is null || accentRim is null) return;

        Sidebar.Background = pane;
        Sidebar.BorderBrush = rim;
        Sidebar.BorderThickness = new Thickness(1);
        Sidebar.CornerRadius = new CornerRadius(20);
        Sidebar.Margin = new Thickness(10, 10, 4, 10);
        Inspector.Background = pane;
        Inspector.BorderBrush = rim;
        Inspector.BorderThickness = new Thickness(1);
        Inspector.CornerRadius = new CornerRadius(20);
        Inspector.Margin = new Thickness(4, 4, 10, 10);
        AssistantFrame.BorderThickness = new Thickness(0);
        ScrollToBottomButton.Background = control;
        ScrollToBottomButton.BorderBrush = rim;

        ComposerSurface.Background = input;
        ComposerSurface.BorderBrush = accentRim;
        ComposerSurface.BorderThickness = new Thickness(1.5);
        // The composer floats over the conversation: the conversation spans both rows and
        // reserves the composer height as bottom padding, so the last message stays readable.
        Grid.SetRowSpan(ConversationScroll, 2);
        _conversationGlassBasePadding = ConversationScroll.Padding;
        ComposerDock.SizeChanged += (_, _) => UpdateConversationGlassPadding();
        ComposerLightHost.SizeChanged += (_, _) => UpdateComposerGlassLights();
        App.Current.ThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(UpdateComposerGlassLights);
    }

    /// <summary>Horizontal margin of the floating inspector glass pane; 0 without Liquid Glass.</summary>
    private double InspectorGlassMarginWidth =>
        App.Current.IsLiquidGlassEnabled ? Inspector.Margin.Left + Inspector.Margin.Right : 0;

    private static Brush? GlassResource(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    /// <summary>Glass control surface for code-built chrome (tabs); the solid brush without Liquid Glass.</summary>
    private static Brush GlassControlOr(Brush solid) =>
        App.Current.IsLiquidGlassEnabled && GlassResource("MissumGlassControlBrush") is { } glass ? glass : solid;

    /// <summary>Glass rim for code-built chrome; the given border without Liquid Glass.</summary>
    private static Brush GlassRimOr(Brush border) =>
        App.Current.IsLiquidGlassEnabled && GlassResource("MissumGlassRimBrush") is { } rim ? rim : border;

    private void UpdateConversationGlassPadding()
    {
        var padding = _conversationGlassBasePadding;
        ConversationScroll.Padding = new Thickness(padding.Left, padding.Top, padding.Right,
            padding.Bottom + ComposerDock.ActualHeight + 8);
    }

    private void UpdateComposerGlassLights()
    {
        if (!App.Current.IsLiquidGlassEnabled || _disposed) return;
        var size = new Vector2((float)ComposerLightHost.ActualWidth, (float)ComposerLightHost.ActualHeight);
        if (size.X <= 0 || size.Y <= 0) return;
        _composerGlassLights ??= ComposerGlassLights.Create(ComposerLightHost);
        _composerGlassLights.Update(size, App.Current.LiquidGlass);
    }

    /// <summary>
    /// Composition visuals behind the composer glass: an accent glow around it, a soft lift
    /// shadow below it, an accent reflection near the send button and a sheen along the top.
    /// All are radial gradients (no LayerVisual shadows, which clip to the layer and render
    /// black outside their content); the composer acrylic frosts the parts beneath it.
    /// </summary>
    private sealed class ComposerGlassLights
    {
        private readonly ContainerVisual _root;
        private readonly CompositionRoundedRectangleGeometry _clipGeometry;
        private readonly (SpriteVisual Visual, CompositionRadialGradientBrush Brush) _glow;
        private readonly (SpriteVisual Visual, CompositionRadialGradientBrush Brush) _lift;
        private readonly (SpriteVisual Visual, CompositionRadialGradientBrush Brush) _reflection;
        private readonly (SpriteVisual Visual, CompositionRadialGradientBrush Brush) _sheen;

        private ComposerGlassLights(Compositor compositor)
        {
            _root = compositor.CreateContainerVisual();
            _clipGeometry = compositor.CreateRoundedRectangleGeometry();
            _clipGeometry.CornerRadius = new Vector2(ComposerGlassRadius);
            _glow = Spot(compositor, clipToComposer: false);
            _lift = Spot(compositor, clipToComposer: false);
            _reflection = Spot(compositor, clipToComposer: true);
            _sheen = Spot(compositor, clipToComposer: true);
        }

        public static ComposerGlassLights Create(UIElement host)
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var lights = new ComposerGlassLights(compositor);
            ElementCompositionPreview.SetElementChildVisual(host, lights._root);
            return lights;
        }

        public void Update(Vector2 size, LiquidGlassFrame? frame)
        {
            _root.Size = size;
            _root.IsVisible = frame is not null;
            if (frame is null) return;
            var lights = LiquidGlassAppearance.ComposerLights(frame.Light, Argb(frame.Accent));
            _clipGeometry.Size = size;
            var half = size / 2;
            // Accent glow: light spilling around the glass and, frosted, into it.
            var glow = new Vector2(lights.AccentGlowSpread, lights.AccentGlowRise);
            Place(_glow, -glow, size + glow * 2, half + glow, half + glow, lights.AccentGlowColor, fadeStart: 0.62f);
            // Lift: a soft shadow ellipse below the floating composer.
            var lift = new Vector2(lights.LiftSpread);
            Place(_lift, new Vector2(0, size.Y - lift.Y), new Vector2(size.X, lift.Y * 2 + lights.LiftOffsetY),
                new Vector2(half.X, lift.Y + lights.LiftOffsetY), new Vector2(half.X, lift.Y), lights.LiftShadowColor, fadeStart: 0.25f);
            // Accent reflection where the accent send button sits (bottom right).
            Place(_reflection, Vector2.Zero, size, new Vector2(size.X - 44, size.Y - 26),
                new Vector2(Math.Min(size.X * 0.45f, 280), Math.Max(size.Y * 0.9f, 60)), lights.AccentReflectionColor, fadeStart: 0f);
            // Sheen: light caught along the top edge of the glass.
            Place(_sheen, Vector2.Zero, size, new Vector2(size.X * 0.3f, 0),
                new Vector2(size.X * 0.5f, Math.Max(size.Y * 0.5f, 36)), lights.SheenColor, fadeStart: 0f);
        }

        private (SpriteVisual Visual, CompositionRadialGradientBrush Brush) Spot(Compositor compositor, bool clipToComposer)
        {
            var brush = compositor.CreateRadialGradientBrush();
            brush.MappingMode = CompositionMappingMode.Absolute;
            var visual = compositor.CreateSpriteVisual();
            visual.Brush = brush;
            if (clipToComposer) visual.Clip = compositor.CreateGeometricClip(_clipGeometry);
            _root.Children.InsertAtTop(visual);
            return (visual, brush);
        }

        private static void Place((SpriteVisual Visual, CompositionRadialGradientBrush Brush) spot, Vector2 offset, Vector2 size,
            Vector2 center, Vector2 radius, uint argb, float fadeStart)
        {
            spot.Visual.Offset = new Vector3(offset, 0);
            spot.Visual.Size = Vector2.Max(size, Vector2.One);
            spot.Brush.EllipseCenter = center;
            spot.Brush.EllipseRadius = Vector2.Max(radius, Vector2.One);
            spot.Brush.ColorStops.Clear();
            var color = Color(argb);
            var compositor = spot.Brush.Compositor;
            spot.Brush.ColorStops.Add(compositor.CreateColorGradientStop(0, color));
            if (fadeStart > 0) spot.Brush.ColorStops.Add(compositor.CreateColorGradientStop(fadeStart, color));
            spot.Brush.ColorStops.Add(compositor.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(0, color.R, color.G, color.B)));
        }

        private static uint Argb(Windows.UI.Color color) =>
            ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

        private static Windows.UI.Color Color(uint argb) =>
            Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
}

namespace Missum.Core.Models;

/// <summary>The palette surfaces the Liquid Glass material turns translucent.</summary>
public enum LiquidGlassSurface { Window, Layer, LayerStrong, Input, Hover, Pressed, Selection }

/// <summary>The native glass materials (in-app acrylic) used for panes, controls and the composer.</summary>
public enum LiquidGlassMaterialKind { Pane, Control, Input }

/// <summary>Acrylic parameters of one glass material. Colors are 0xAARRGGBB.</summary>
public readonly record struct LiquidGlassMaterial(uint TintColor, double TintOpacity, double LuminosityOpacity, uint FallbackColor);

/// <summary>One gradient stop of a glass rim (offset 0..1, color 0xAARRGGBB).</summary>
public readonly record struct LiquidGlassStop(double Offset, uint Color);

/// <summary>A soft radial light in the scene behind the glass (relative center/radius).</summary>
public readonly record struct LiquidGlassLight(double CenterX, double CenterY, double RadiusX, double RadiusY, uint Color);

/// <summary>
/// Light and shadow behind the floating composer glass, as soft radial gradients:
/// an accent glow spreading around it, a lift shadow below it, an accent reflection
/// near the send button and a white sheen along the top. Colors are 0xAARRGGBB.
/// </summary>
public readonly record struct LiquidGlassComposerLights(
    uint LiftShadowColor, float LiftSpread, float LiftOffsetY,
    uint AccentGlowColor, float AccentGlowSpread, float AccentGlowRise,
    uint AccentReflectionColor, uint SheenColor);

/// <summary>
/// Theme-independent rules for the optional Liquid Glass look. The preference is read
/// once at startup (it applies after a restart). Glass panes are native in-app acrylic
/// that blurs an opaque scene the app draws itself (base color plus soft accent and
/// background light fields), so the glass has real content to frost. The user's colors
/// are never replaced: panes are tinted with the background-derived window color, rims
/// and lights carry the accent, and accent fills stay as they are.
/// </summary>
public static class LiquidGlassAppearance
{
    /// <summary>High contrast always wins over decorative transparency.</summary>
    public static bool IsActive(bool enabled, bool highContrast) => enabled && !highContrast;

    /// <summary>
    /// Translucent tint for the shared solid palette brushes (code-built UI, hover and
    /// pressed states) as 0xAARRGGBB; returns <paramref name="opaqueArgb"/> when inactive.
    /// </summary>
    public static uint Surface(LiquidGlassSurface surface, bool light, uint windowArgb, uint opaqueArgb, bool active)
    {
        if (!active) return opaqueArgb;
        var (frost, alpha) = (surface, light) switch
        {
            // The window surface shows the scene behind the glass.
            (LiquidGlassSurface.Window, _) => (0.0, (byte)0x00),
            (LiquidGlassSurface.Layer, false) => (0.10, (byte)0x66),
            (LiquidGlassSurface.LayerStrong, false) => (0.17, (byte)0x8C),
            (LiquidGlassSurface.Input, false) => (0.13, (byte)0x99),
            (LiquidGlassSurface.Hover, false) => (0.24, (byte)0xA0),
            (LiquidGlassSurface.Pressed, false) => (0.30, (byte)0xB4),
            (LiquidGlassSurface.Selection, false) => (0.20, (byte)0xA8),
            (LiquidGlassSurface.Layer, true) => (0.55, (byte)0x80),
            (LiquidGlassSurface.LayerStrong, true) => (0.72, (byte)0xA6),
            (LiquidGlassSurface.Input, true) => (0.78, (byte)0xB8),
            (LiquidGlassSurface.Hover, true) => (0.85, (byte)0xC8),
            (LiquidGlassSurface.Pressed, true) => (0.60, (byte)0xD0),
            (LiquidGlassSurface.Selection, true) => (0.70, (byte)0xC0),
            _ => (0.0, (byte)(opaqueArgb >> 24)),
        };
        return ((uint)alpha << 24) | Mix(windowArgb, 0xFFFFFF, frost);
    }

    /// <summary>Bright translucent edge used as the plain stroke of glass content (0xAARRGGBB).</summary>
    public static uint Edge(bool light, uint windowArgb) =>
        light ? 0xB3000000u | Mix(windowArgb, 0xFFFFFF, 0.92) : 0x3D000000u | Mix(windowArgb, 0xFFFFFF, 0.85);

    /// <summary>
    /// In-app acrylic for a glass material. The tint is the window color (theme plus
    /// background color) frosted towards white, so the background shows in every pane;
    /// the fallback (battery saver, transparency off) is the opaque classic surface.
    /// </summary>
    public static LiquidGlassMaterial Material(LiquidGlassMaterialKind kind, bool light, uint windowArgb, uint fallbackArgb)
    {
        var (frost, tint, luminosity) = (kind, light) switch
        {
            (LiquidGlassMaterialKind.Pane, false) => (0.14, 0.36, 0.64),
            (LiquidGlassMaterialKind.Control, false) => (0.22, 0.26, 0.50),
            (LiquidGlassMaterialKind.Input, false) => (0.12, 0.34, 0.62),
            (LiquidGlassMaterialKind.Pane, true) => (0.55, 0.40, 0.62),
            (LiquidGlassMaterialKind.Control, true) => (0.75, 0.30, 0.55),
            (LiquidGlassMaterialKind.Input, true) => (0.70, 0.45, 0.70),
            _ => (0.0, 0.4, 0.6),
        };
        return new(0xFF000000 | Mix(windowArgb, 0xFFFFFF, frost), tint, luminosity, 0xFF000000 | (fallbackArgb & 0x00FFFFFF));
    }

    /// <summary>
    /// Rim of a glass pane, top-left to bottom-right: a bright specular edge where the
    /// light hits, fading over the sides. With <paramref name="accentArgb"/> the far corner
    /// (where the composer's send button sits) reflects the accent color.
    /// </summary>
    public static IReadOnlyList<LiquidGlassStop> Rim(bool light, uint? accentArgb = null)
    {
        uint White(byte alpha) => ((uint)alpha << 24) | 0xFFFFFF;
        var start = light ? White(0xF2) : White(0x80);
        var middle = light ? White(0x8C) : White(0x1A);
        var low = light ? White(0x59) : White(0x0F);
        if (accentArgb is not { } accent)
            return [new(0, start), new(0.45, middle), new(0.75, low), new(1, light ? White(0xB3) : White(0x40))];
        var rgb = accent & 0x00FFFFFF;
        return
        [
            new(0, start), new(0.40, middle),
            new(0.72, ((light ? 0x66u : 0x73u) << 24) | rgb),
            new(1, ((light ? 0xE6u : 0xF2u) << 24) | rgb),
        ];
    }

    /// <summary>Opaque base of the scene behind the glass: the classic window color.</summary>
    public static uint SceneBase(uint windowArgb) => 0xFF000000 | (windowArgb & 0x00FFFFFF);

    /// <summary>
    /// Soft light fields of the scene: the accent glows from the top left behind the left
    /// sidebar and, mirrored, from the top right behind the right sidebar; the background
    /// color comes from the bottom right, plus a faint white sky light.
    /// The chat center stays calm; the lights only color what the glass frosts.
    /// A light darker than the window base is left out: it would not tint but darken, and its
    /// shallow gradient shows as dark rounded bands in the chat (e.g. a near-black background).
    /// </summary>
    public static IReadOnlyList<LiquidGlassLight> SceneLights(bool light, uint accentArgb, uint backgroundArgb, uint windowArgb)
    {
        var accent = accentArgb & 0x00FFFFFF;
        var background = Mix(backgroundArgb, light ? 0xFFFFFFu : 0x000000u, light ? 0.15 : 0.0) & 0x00FFFFFF;
        var accentGlow = ((light ? 0x33u : 0x46u) << 24) | accent;
        LiquidGlassLight[] lights =
        [
            // Halved on request (intensity and reach): the accent stays behind the sidebars
            // instead of shining into the chat text. The right glow mirrors the left one exactly.
            new(0.04, 0.10, 0.30, 0.60, accentGlow),
            new(1 - 0.04, 0.10, 0.30, 0.60, accentGlow),
            new(0.98, 0.98, 0.50, 0.65, ((light ? 0x80u : 0x99u) << 24) | background),
            new(0.70, 0.00, 0.40, 0.35, ((light ? 0xA6u : 0x1Fu) << 24) | 0xFFFFFF),
        ];
        var baseLuma = Luma(windowArgb);
        return [.. lights.Where(item => Luma(item.Color) >= baseLuma)];
    }

    /// <summary>Upper bound of <see cref="SceneLights"/>; the window creates this many light fields.</summary>
    public const int MaximumSceneLights = 4;

    private static double Luma(uint argb) =>
        0.2126 * ((argb >> 16) & 0xFF) + 0.7152 * ((argb >> 8) & 0xFF) + 0.0722 * (argb & 0xFF);

    /// <summary>
    /// Lights behind the floating composer: a lift shadow, an accent glow around the
    /// glass, an accent reflection near the send button and a white sheen along the top.
    /// The composer acrylic frosts the inner part, so the accent shows inside the glass.
    /// </summary>
    public static LiquidGlassComposerLights ComposerLights(bool light, uint accentArgb)
    {
        var accent = accentArgb & 0x00FFFFFF;
        return new(
            LiftShadowColor: light ? 0x38201830u : 0x99000000u,
            LiftSpread: light ? 22f : 28f,
            LiftOffsetY: light ? 8f : 10f,
            // Halved on request: half the intensity, and it rises only half as far into the chat.
            AccentGlowColor: ((light ? 0x2Eu : 0x3Du) << 24) | accent,
            AccentGlowSpread: light ? 44f : 56f,
            AccentGlowRise: light ? 22f : 28f,
            AccentReflectionColor: ((light ? 0x8Cu : 0xB3u) << 24) | accent,
            SheenColor: light ? 0xE6FFFFFFu : 0x40FFFFFFu);
    }

    private static uint Mix(uint baseArgb, uint overlayRgb, double weight)
    {
        static uint Channel(uint value, int shift) => (value >> shift) & 0xFF;
        uint Blend(int shift) => (uint)Math.Round(Channel(baseArgb, shift) * (1 - weight) + Channel(overlayRgb, shift) * weight);
        return (Blend(16) << 16) | (Blend(8) << 8) | Blend(0);
    }
}

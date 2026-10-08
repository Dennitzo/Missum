using Missum.Core.Models;
using Windows.UI;

namespace Missum.Tests;

public sealed class NativeLightPaletteTests
{
    [Theory]
    [InlineData("#000000")]
    [InlineData("#FFFFFF")]
    [InlineData("#181818")]
    [InlineData("#FF0000")]
    [InlineData("#0000FF")]
    public void LightTextAccentMeetsContrastOnEveryNativeSurfaceWithoutChangingTheSavedAccent(string background)
    {
        var saved = new AppSettings { Theme = AppTheme.Light, AccentColor = "#A970FF", BackgroundColor = background };
        var appearance = Missum.App.App.CreateSavedAppearance(saved, systemLight: false, highContrast: null);
        var readable = Parse(appearance.Colors["accentReadable"]);
        Assert.Equal("#A970FF", appearance.Colors["accent"]);
        Assert.Equal("#A970FF", saved.AccentColor);
        Assert.NotEqual(appearance.Colors["accent"], appearance.Colors["accentReadable"]);
        var window = Parse(appearance.Colors["window"]);
        foreach (var role in new[] { "window", "layer", "layerStrong", "input", "hover", "pressed" })
        {
            var surface = Composite(Parse(appearance.Colors[role]), window);
            Assert.True(Missum.App.App.TextContrastRatio(readable, surface) >= 4.5,
                $"Readable accent {appearance.Colors["accentReadable"]} must pass against {role}: {appearance.Colors[role]}.");
        }
    }

    [Fact]
    public void DarkAppearanceRetainsTheOriginalAccentAndNormalForegroundPalette()
    {
        var appearance = Missum.App.App.CreateSavedAppearance(new AppSettings { Theme = AppTheme.Dark, AccentColor = "#A970FF", BackgroundColor = "#181818" }, false, null);
        Assert.Equal("#A970FF", appearance.Colors["accent"]);
        Assert.Equal(appearance.Colors["accent"], appearance.Colors["accentReadable"]);
        Assert.Equal("#FFFFFF", appearance.Colors["text"]);
        Assert.Equal("#999999", appearance.Colors["mutedText"]);
    }

    [Fact]
    public void AnAlreadyReadableTextAccentIsKeptExactlyAndLowContrastAccessibilityAccentIsCorrected()
    {
        var readable = Color.FromArgb(255, 100, 37, 196);
        Assert.Equal(readable, Missum.App.App.ReadableAccentColor(readable, [Color.FromArgb(255, 255, 255, 255)]));
        var palette = Missum.App.App.CreateHighContrastPalette(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 0, 0, 0), Color.FromArgb(255, 230, 230, 230));
        var appearance = Missum.App.App.CreateSavedAppearance(new AppSettings { Theme = AppTheme.Light }, true, palette);
        Assert.Equal("#000000", appearance.Colors["text"]);
        Assert.Equal("#000000", appearance.Colors["mutedText"]);
        Assert.Equal("#E6E6E6", appearance.Colors["accent"]);
        Assert.True(Missum.App.App.TextContrastRatio(Parse(appearance.Colors["accentReadable"]), palette.Background) >= 4.5);
    }

    [Fact]
    public void ContrastMeasurementCompositesTransparentTextInsteadOfTreatingItsRgbAsOpaque()
    {
        var white = Color.FromArgb(255, 255, 255, 255);
        Assert.Equal(1d, Missum.App.App.TextContrastRatio(Color.FromArgb(0, 0, 0, 0), white), precision: 8);
        Assert.Equal(21d, Missum.App.App.TextContrastRatio(Color.FromArgb(255, 0, 0, 0), white), precision: 8);
        Assert.True(Missum.App.App.TextContrastRatio(Color.FromArgb(228, 0, 0, 0), white) > 4.5);
    }

    private static Color Parse(string value) => Color.FromArgb(value.Length == 9 ? Convert.ToByte(value[7..9], 16) : (byte)255,
        Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16));

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromArgb(255, (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)), (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }
}

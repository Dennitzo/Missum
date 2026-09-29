using Microsoft.UI;

namespace Missum.Tests;

public sealed class NativeAccentColorTests
{
    [Theory]
    [InlineData(176, 176, 176)]
    [InlineData(244, 184, 96)]
    [InlineData(37, 183, 166)]
    public void BrightAccentsUseBlackReadableForeground(byte red, byte green, byte blue)
    {
        var foreground = Missum.App.App.ContrastForeground(Windows.UI.Color.FromArgb(255, red, green, blue));
        Assert.Equal(Colors.Black, foreground);
    }

    [Theory]
    [InlineData(100, 37, 196)]
    [InlineData(30, 45, 90)]
    public void DarkAccentsUseWhiteReadableForeground(byte red, byte green, byte blue)
    {
        var foreground = Missum.App.App.ContrastForeground(Windows.UI.Color.FromArgb(255, red, green, blue));
        Assert.Equal(Colors.White, foreground);
    }

    [Theory]
    [InlineData(255, 255, 0, 0, 0, 0)]
    [InlineData(0, 70, 120, 255, 255, 255)]
    public void HighContrastPaletteUsesSystemColorsAndReadableAccentText(
        byte accentRed,
        byte accentGreen,
        byte accentBlue,
        byte textRed,
        byte textGreen,
        byte textBlue)
    {
        var background = Windows.UI.Color.FromArgb(255, 12, 13, 14);
        var foreground = Windows.UI.Color.FromArgb(255, 230, 231, 232);
        var accent = Windows.UI.Color.FromArgb(255, accentRed, accentGreen, accentBlue);

        var palette = Missum.App.App.CreateHighContrastPalette(background, foreground, accent);

        Assert.Equal(background, palette.Background);
        Assert.Equal(foreground, palette.Foreground);
        Assert.Equal(accent, palette.Accent);
        Assert.Equal(Windows.UI.Color.FromArgb(255, textRed, textGreen, textBlue), palette.AccentForeground);
    }
}

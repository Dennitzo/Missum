using Missum.App.Controls;
using SkiaSharp;

namespace Missum.Tests;

public sealed class NativeMathRendererTests
{
    [Theory]
    [InlineData(@"\frac{a+b}{c+d}")]
    [InlineData(@"\sqrt{x^2+y^2}")]
    [InlineData(@"\int_0^\infty e^{-x}\,dx=1")]
    [InlineData(@"\sum_{n=1}^{\infty}\frac{1}{n^2}=\frac{\pi^2}{6}")]
    [InlineData(@"\begin{aligned}a&=b+c\\d&=e-f\end{aligned}")]
    [InlineData(@"\begin{pmatrix}1&2\\3&4\end{pmatrix}")]
    [InlineData(@"\text{Energie}\quad E=mc^2,\quad\alpha+\beta=\gamma")]
    [InlineData(@"\alpha=\beta")]
    [InlineData(@"\alpha*\beta")]
    [InlineData(@"\alpha'=\beta")]
    public void CommonMathematicsRendersAsTransparentSharpImages(string latex)
    {
        var image = NativeMathRenderer.Render(latex, display: true);
        Assert.True(image.Error is null, image.Error);
        Assert.NotEmpty(image.Png);
        using var bitmap = SKBitmap.Decode(image.Png);
        Assert.NotNull(bitmap);
        Assert.Equal(image.Width * 2, bitmap.Width);
        Assert.Equal(image.Height * 2, bitmap.Height);
        Assert.True(bitmap.Width > 12 && bitmap.Height > 12);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 128);
        Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha == 0);
    }

    [Fact]
    public void DisplayStyleAndFontSizeAffectTypesettingAndDoNotReuseWrongCacheEntry()
    {
        const string latex = @"\sum_{n=1}^{\infty}\frac{1}{n^2}";
        var inline = NativeMathRenderer.Render(latex, display: false);
        var display = NativeMathRenderer.Render(latex, display: true);
        var large = NativeMathRenderer.Render(latex, display: true, fontSize: 30);
        Assert.Null(inline.Error);
        Assert.Null(display.Error);
        Assert.Null(large.Error);
        Assert.True(display.Height > inline.Height);
        Assert.True(large.Height > display.Height);
        Assert.True(large.Width > display.Width);
        Assert.Same(display, NativeMathRenderer.Render(latex, display: true));
    }

    [Fact]
    public void DefaultFormulaSizeUsesTheChatBodyFontSize()
    {
        Assert.Same(NativeMathRenderer.Render("E=mc^2", display: false, fontSize: 16),
            NativeMathRenderer.Render("E=mc^2", display: false));
        Assert.Same(NativeMathRenderer.Render("E=mc^2", display: true, fontSize: 16),
            NativeMathRenderer.Render("E=mc^2", display: true));
    }

    [Fact]
    public void BaselineMetricsPreserveSuperscriptHeightAndFractionDepth()
    {
        var plain = NativeMathRenderer.Render("x", display: false, fontSize: 16);
        var superscript = NativeMathRenderer.Render("x^{500}", display: false, fontSize: 16);
        var fraction = NativeMathRenderer.Render(@"\frac{x}{y}", display: false, fontSize: 16);
        foreach (var bitmap in new[] { plain, superscript, fraction })
        {
            Assert.Null(bitmap.Error);
            Assert.InRange(bitmap.BaselineOffset, 0, bitmap.Height);
            Assert.InRange(bitmap.Descent, 0, bitmap.Height);
            Assert.Equal(bitmap.Height, bitmap.BaselineOffset + bitmap.Descent, precision: 5);
        }
        Assert.True(superscript.BaselineOffset > plain.BaselineOffset);
        Assert.True(superscript.Height > plain.Height);
        Assert.True(fraction.Descent > plain.Descent);
        Assert.True(fraction.Height > plain.Height);
    }

    [Fact]
    public void FormulaScaleFollowsTextSizeWithoutClippingItsNaturalBounds()
    {
        const string latex = @"\ell_P\approx1.6\times10^{-35}";
        var small = NativeMathRenderer.Render(latex, display: false, fontSize: 16);
        var large = NativeMathRenderer.Render(latex, display: false, fontSize: 32);
        Assert.Null(small.Error);
        Assert.Null(large.Error);
        // The two-DIP outer guard stays constant; glyph metrics scale with the text em.
        Assert.InRange((large.Width - 2) / (small.Width - 2), 1.95, 2.05);
        Assert.InRange((large.Height - 2) / (small.Height - 2), 1.9, 2.1);
        Assert.InRange((large.BaselineOffset - 1) / (small.BaselineOffset - 1), 1.99, 2.01);
        using var bitmap = SKBitmap.Decode(large.Png);
        Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).Alpha);
    }

    [Fact]
    public void ThemeChangesInkButPreservesTransparentBackground()
    {
        var dark = NativeMathRenderer.Render("x", display: false, dark: true);
        var light = NativeMathRenderer.Render("x", display: false, dark: false);
        using var darkBitmap = SKBitmap.Decode(dark.Png);
        using var lightBitmap = SKBitmap.Decode(light.Png);
        Assert.Contains(darkBitmap.Pixels, pixel => pixel.Alpha > 200 && pixel.Red > 200);
        Assert.Contains(lightBitmap.Pixels, pixel => pixel.Alpha > 200 && pixel.Red < 60);
        Assert.Equal(0, lightBitmap.GetPixel(0, 0).Alpha);
    }

    [Theory]
    [InlineData(@"\frac{1}{2}", @"\\frac{1}{2}")]
    [InlineData(@"\begin{aligned}a&=b+c\\d&=e-f\end{aligned}", @"\\begin{aligned}a&=b+c\\\\d&=e-f\\end{aligned}")]
    [InlineData(@"\begin{pmatrix}1&2\\3&4\end{pmatrix}", @"\\begin{pmatrix}1&2\\\\3&4\\end{pmatrix}")]
    public void JsonEscapedCommandsNormalizeWithoutLosingMatrixRowBreaks(string expected, string escaped)
    {
        var regular = NativeMathRenderer.Render(expected, display: true);
        var normalized = NativeMathRenderer.Render(escaped, display: true);
        Assert.Null(regular.Error);
        Assert.Null(normalized.Error);
        Assert.Same(regular, normalized);
    }

    [Theory]
    [InlineData(@"\frac{1}{")]
    [InlineData(@"\thisCommandDoesNotExist{abc}")]
    [InlineData(@"\begin{pmatrix}1&2")]
    [InlineData("")]
    public void InvalidFormulaReturnsReadableFallbackInsteadOfThrowing(string latex)
    {
        var result = NativeMathRenderer.Render(latex, display: true);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Empty(result.Png);
        Assert.Equal(0, result.Width);
        Assert.Equal(0, result.Height);
    }

    [Fact]
    public void OversizedOrDeeplyNestedFormulasAreRejectedBeforeRasterization()
    {
        var longFormula = NativeMathRenderer.Render(new string('x', 8193), display: true);
        var nested = NativeMathRenderer.Render(new string('{', 40) + "x" + new string('}', 40), display: true);
        var wide = NativeMathRenderer.Render(new string('x', 2000), display: true);
        Assert.Contains("zu lang", longFormula.Error);
        Assert.Contains("verschachtelt", nested.Error);
        Assert.Contains("zu groß", wide.Error);
        Assert.Empty(longFormula.Png);
        Assert.Empty(nested.Png);
        Assert.Empty(wide.Png);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(1000)]
    public void InvalidFontSizeDoesNotAllocateAnImage(double size)
    {
        var result = NativeMathRenderer.Render("x", display: false, fontSize: size);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Png);
    }
}

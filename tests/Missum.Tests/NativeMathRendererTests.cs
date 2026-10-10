using Missum.App.Controls;
using SkiaSharp;

namespace Missum.Tests;

public sealed class NativeMathRendererTests
{
    [Theory]
    [InlineData(@"\boxed{\text{Metrik (Krümmung)} \quad \longleftrightarrow \quad \text{Energie-Impuls-Dichte}}")]
    [InlineData(@"E=\boxed{mc^2}+1")]
    [InlineData(@"\boxed{x}+\boxed{y}")]
    [InlineData(@"\boxed{a+\boxed{b}}")]
    [InlineData(@"\boxed{\frac{a+b}{c+d}}")]
    [InlineData(@"x^{\boxed{n+1}}")]
    public void BoxedMathematicsTypesetsInBothThemes(string latex)
    {
        foreach (var dark in new[] { true, false })
        {
            var image = NativeMathRenderer.Render(latex, display: true, dark: dark);
            Assert.True(image.Error is null, image.Error);
            Assert.NotEmpty(image.Png);
            using var bitmap = SKBitmap.Decode(image.Png);
            Assert.Contains(bitmap.Pixels, pixel => pixel.Alpha > 128);
            Assert.Equal(0, bitmap.GetPixel(0, 0).Alpha);
        }
    }

    [Fact]
    public void ABoxReservesSpaceForItsFrameAndKeepsTransparentBackground()
    {
        var plain = NativeMathRenderer.Render("x", display: true);
        var boxed = NativeMathRenderer.Render(@"\boxed{x}", display: true);
        Assert.Null(boxed.Error);
        Assert.True(boxed.Width > plain.Width + 3);
        Assert.True(boxed.Height > plain.Height + 3);
        Assert.True(boxed.BaselineOffset > plain.BaselineOffset);
        Assert.True(boxed.Descent > plain.Descent);
        Assert.Same(boxed, NativeMathRenderer.Render(@"\boxed{x}", display: true));
    }

    [Theory]
    [InlineData(@"\boxed{\frac{1}{2}")]
    [InlineData(@"\boxed{\thisCommandDoesNotExist{x}}")]
    [InlineData(@"\boxed")]
    public void InvalidBoxedMathematicsRetainsTheNormalFailurePath(string latex)
        => Assert.NotNull(NativeMathRenderer.Render(latex, display: true).Error);

    [Theory]
    [InlineData(@"\int \frac{d^{D}q}{(2\pi)^{D}}\; \frac{1}{q^{2}(q+k)^{2}} \;=\; \frac{i}{16\pi^{2}}\left[-\frac{1}{\varepsilon} \;+\; \text{endliche Konstanten} \;+\; \ln\!\Bigl(\frac{\mu^{2}}{-k^{2}}\Bigr) \;+\; \ldots\right]")]
    [InlineData(@"\int d^{4}x\;\sqrt{-g}\;\Big[a\, R_{\mu\nu\rho\sigma}R^{\mu\nu\rho\sigma} \;+\; b\, R_{\mu\nu}R^{\mu\nu} \;+\; c\, R^{2} \;+\; \ldots\Bigr]")]
    public void StoredPhysicsFormulasTypesetInsteadOfFallingBackToSource(string latex)
    {
        foreach (var dark in new[] { true, false })
        {
            var image = NativeMathRenderer.Render(latex, display: true, dark: dark);
            Assert.True(image.Error is null, image.Error);
            Assert.NotEmpty(image.Png);
            Assert.InRange(image.Width, 50, 1500);
            Assert.InRange(image.Height, 15, 300);
        }
    }

    [Theory]
    [InlineData(@"\bigl(x\bigr)", "(x)")]
    [InlineData(@"\Bigl[x\Bigr]", "[x]")]
    [InlineData(@"\biggl\{x\biggr\}", @"\{x\}")]
    [InlineData(@"\Biggl\langle x\Biggr\rangle", @"\langle x\rangle")]
    [InlineData(@"a\Bigm|b", "a|b")]
    public void UnsupportedVisualDelimiterPrefixesPreserveAllMathematicalTokens(string sized, string plain)
    {
        var result = NativeMathRenderer.Render(sized, display: true);
        var equivalent = NativeMathRenderer.Render(plain, display: true);
        Assert.True(result.Error is null, result.Error);
        Assert.Null(equivalent.Error);
        Assert.Equal(equivalent.Png, result.Png);
    }

    [Theory]
    [InlineData(@"\Bigunknown[x]")]
    [InlineData(@"\Big x")]
    public void InvalidCommandsAreNotSilentlyRemovedAsDelimiterSizing(string latex)
    {
        Assert.NotNull(NativeMathRenderer.Render(latex, display: true).Error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RepeatedJsonEscapingKeepsMatrixRowsAndTheirFollowingControlWords(int layers)
    {
        const string regular = @"\begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}";
        var escaped = regular;
        for (var layer = 0; layer < layers; layer++) escaped = escaped.Replace("\\", "\\\\", StringComparison.Ordinal);
        var expected = NativeMathRenderer.Render(regular, display: true);
        var actual = NativeMathRenderer.Render(escaped, display: true);
        Assert.Null(expected.Error);
        Assert.True(actual.Error is null, actual.Error);
        Assert.Same(expected, actual);
    }

    [Fact]
    public void ExistingMatrixRowsAndMixedEscapesAreNotReinterpretedAsJson()
    {
        const string regular = @"\begin{matrix}a&b\\c&d\end{matrix}";
        Assert.Null(NativeMathRenderer.Render(regular, display: true).Error);
        const string mixed = @"\frac{1}{2}+\\alpha";
        Assert.Equal(regular, NativeMathRenderer.NormalizeEscapedLatex(regular));
        Assert.Equal(mixed, NativeMathRenderer.NormalizeEscapedLatex(mixed));
    }

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
    [InlineData(@"\begin{pmatrix}B_p\\B_\phi\end{pmatrix}", @"\\begin{pmatrix}B_p\\\\B_\\phi\\end{pmatrix}")]
    [InlineData(@"\begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}",
        @"\\begin{pmatrix}-\\eta_p & \\alpha\\\\\\omega_\\Omega & -\\eta_\\phi\\end{pmatrix}")]
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

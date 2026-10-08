using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeLooseMathTests
{
    [Theory]
    [InlineData("dA/dt = A(μ - c A²) + ξ(t)", "dA/dt = A (μ-c A^{2})+ξ (t)")]
    [InlineData("τ_K ∝ exp(ΔU/D_eff)", @"τ_{K} \propto \exp (ΔU/D_{eff})")]
    [InlineData("ΔU = μ²/(4c)", "ΔU = μ^{2}/(4 c)")]
    [InlineData("A·m²", @"A\cdot m^{2}")]
    public void CurrentGeomagneticEquationsRenderAsOneCompleteLosslessFormula(string source, string latex)
    {
        var segments = NativeMathSyntax.SplitForRendering(source);
        var formula = Assert.Single(segments, segment => segment.IsMath);
        Assert.Equal(source, formula.Text);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(latex, formula.RenderLatex);
        var image = NativeMathRenderer.Render(formula.RenderLatex!, display: false);
        Assert.True(image.Error is null, image.Error);
        Assert.NotEmpty(image.Png);
    }

    [Theory]
    [InlineData("T_ECT = T_HH/ln 2")]
    [InlineData("P_ECT = A·T_ECT⁴ = A·(T_HH/ln 2)⁴ = P_std/(ln 2)⁴")]
    [InlineData("dM/dt = -P/c²")]
    [InlineData("t_ECT = M·c²/P_ECT = t_std/(0.2308)")]
    [InlineData("P ~ A·T⁴")]
    [InlineData("t = M·c²/P")]
    [InlineData("t_ECT = M·c²/P_ECT = M·c²/(P_std/(ln 2)⁴) = t_std·(ln 2)⁴")]
    [InlineData("T_ECT = T_HH/ln 2 = T_HH·0.6931")]
    [InlineData("T_ECT⁴ = T_HH⁴·(0.6931)⁴ = T_HH⁴·0.2308")]
    [InlineData("t_ECT = M·c²/P_ECT = M·c²/(P_std·0.2308) = t_std/0.2308 = t_std·4.333")]
    public void ScreenshotEquationsRenderWithoutChangingTheirOriginalSource(string source)
    {
        var segments = NativeMathSyntax.SplitForRendering(source);
        var formula = Assert.Single(segments, segment => segment.IsMath);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(source, formula.Text);
        Assert.NotNull(formula.RenderLatex);
        var image = NativeMathRenderer.Render(formula.RenderLatex!, formula.Display, NativeStreamingMarkdown.BodyFontSize);
        Assert.True(image.Error is null, image.Error);
        Assert.NotEmpty(image.Png);
    }

    [Fact]
    public void InlineScreenshotEquationLeavesSurroundingGermanProseUntouched()
    {
        const string formulaSource = "t_ECT = t_std·(ln 2)⁴";
        const string source = "Also " + formulaSource + ". Das ist korrekt: niedrigere Temperatur → geringere Leistung.";
        var segments = NativeMathSyntax.SplitForRendering(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        var formula = Assert.Single(segments, segment => segment.IsMath);
        Assert.Equal(formulaSource, formula.Text);
        Assert.False(formula.Display);
        Assert.Equal("Also ", segments[0].Text);
        Assert.Equal(". Das ist korrekt: niedrigere Temperatur → geringere Leistung.", segments[^1].Text);
        Assert.Null(NativeMathRenderer.Render(formula.RenderLatex!, display: false).Error);
    }

    [Theory]
    [InlineData("T_ECT = T_HH/ln 2", @"T_{ECT} = T_{HH}/\ln 2")]
    [InlineData("P_ECT = A·T_ECT⁴ = A·(T_HH/ln 2)⁴ = P_std/(ln 2)⁴", @"P_{ECT} = A\cdot T_{ECT}^{4} = A\cdot (T_{HH}/\ln 2)^{4} = P_{std}/(\ln 2)^{4}")]
    [InlineData("dM/dt = -P/c²", @"dM/dt = -P/c^{2}")]
    [InlineData("t_ECT = M·c²/P_ECT = t_std/(0.2308)", @"t_{ECT} = M\cdot c^{2}/P_{ECT} = t_{std}/(0.2308)")]
    public void RenderingNormalizesNotationWithoutRearrangingEquationOrDivision(string source, string expectedLatex)
    {
        var formula = Assert.Single(NativeMathSyntax.SplitForRendering(source), segment => segment.IsMath);
        Assert.Equal(expectedLatex, formula.RenderLatex);
    }

    [Theory]
    [InlineData("`token_id=123`")]
    [InlineData("```python\ntoken_id=123\nT_ECT=1\n```")]
    [InlineData("```python\ntoken_id=123\nT_ECT=1")]
    [InlineData("~~~text\nT_ECT = T_HH/ln 2\n~~~")]
    [InlineData("T_ECT.py")]
    [InlineData(@"C:\research\T_ECT.py")]
    [InlineData("https://example.org/?T_ECT=1")]
    [InlineData("[T_ECT.py](https://example.org/?T_ECT=1)")]
    [InlineData("token_id=123")]
    [InlineData("a=b")]
    [InlineData("a/T_ECT")]
    [InlineData("a/T_ECT⁴")]
    [InlineData("research_data/T_ECT")]
    [InlineData("example.org?q=T_ECT")]
    [InlineData("tool_name=T_ECT")]
    [InlineData("path=x_i")]
    [InlineData("cache_key=T_ECT")]
    [InlineData("ΔU.py")]
    [InlineData("samples/ΔU")]
    [InlineData("ΔUnknown=1")]
    public void CodeFilesPathsUrlsAndSimpleAssignmentsStayLiteral(string source)
    {
        var segments = NativeMathSyntax.SplitForRendering(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.DoesNotContain(segments, segment => segment.IsMath);
    }

    [Fact]
    public void PublicDelimiterParserKeepsItsExplicitOnlyLosslessContract()
    {
        const string source = "T_ECT = T_HH/ln 2";
        var segment = Assert.Single(NativeMathSyntax.Split(source));
        Assert.Equal(source, segment.Text);
        Assert.False(segment.IsMath);
        Assert.False(NativeMathSyntax.ContainsMath(source));
        Assert.Single(NativeMathSyntax.SplitForRendering(source), piece => piece.IsMath);
    }

    [Theory]
    [InlineData("Ein Zwischenschritt: T_ECT =")]
    [InlineData("Ein Zwischenschritt: P_ECT = A·T_ECT^")]
    [InlineData("Ein Zwischenschritt: P_ECT = A·T_ECT^{")]
    [InlineData("$5^2")]
    [InlineData("$5x²")]
    [InlineData("τ_K ∝ exp(ΔU/")]
    public void IncompleteStreamingOperatorsStayLosslessAndNeverBecomeBrokenFormulas(string source)
    {
        var segments = NativeMathSyntax.SplitForRendering(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        foreach (var formula in segments.Where(segment => segment.IsMath))
        {
            Assert.NotNull(formula.RenderLatex);
            Assert.False(formula.Text.TrimEnd().EndsWith('='));
            Assert.False(formula.Text.TrimEnd().EndsWith('^'));
            Assert.False(formula.Text.TrimEnd().EndsWith('{'));
            Assert.Null(NativeMathRenderer.Render(formula.RenderLatex!, formula.Display).Error);
        }
        var complete = NativeMathSyntax.SplitForRendering("Ein Zwischenschritt: P_ECT = A·T_ECT⁴.");
        Assert.Single(complete, segment => segment.IsMath);
    }

    [Fact]
    public void SafeMarkdownTransportCarriesOriginalCopyTextAndRenderLatexForBothFormulaKinds()
    {
        const string source = @"Also t_ECT = t_std·(ln 2)⁴. Zum Vergleich $T_{\mathrm{ECT}}=\frac{T_{\mathrm{HH}}}{\ln 2}$.";
        var tokenized = NativeStreamingMarkdown.TokenizeMath(source);
        var pieces = NativeStreamingMarkdown.ParseMathInlines(tokenized.Text, tokenized.Prefix);
        var formulas = pieces.Where(piece => piece.IsMath).ToArray();
        Assert.Equal(2, formulas.Length);
        Assert.Equal("t_ECT = t_std·(ln 2)⁴", formulas[0].Text);
        Assert.Equal(@"$T_{\mathrm{ECT}}=\frac{T_{\mathrm{HH}}}{\ln 2}$", formulas[1].Text);
        Assert.Equal(source, string.Concat(pieces.Select(piece => piece.Text)));
        Assert.NotNull(formulas[0].RenderLatex);
        Assert.Null(NativeMathRenderer.Render(formulas[0].RenderLatex!, formulas[0].Display).Error);
    }

    [Theory]
    [InlineData("x^2y", "x^{2} y")]
    [InlineData("x^2=2e-3", @"x^{2} = 2\cdot 10^{-3}")]
    [InlineData("A·2e-3", @"A\cdot 2\cdot 10^{-3}")]
    [InlineData("1.6×10^-35m", @"1.6\cdot 10^{-35} m")]
    public void ScientificNumbersAndAdjacentSymbolsKeepTheirMathematicalAssociation(string source, string latex)
    {
        var formula = Assert.Single(NativeMathSyntax.SplitForRendering(source), segment => segment.IsMath);
        Assert.Equal(source, formula.Text);
        Assert.Equal(latex, formula.RenderLatex);
        Assert.Null(NativeMathRenderer.Render(latex, false).Error);
    }

    [Fact]
    public void MalformedLegacyMathDoesNotHideLaterExplicitMath()
    {
        const string source = "T_ECT = unbekannt; korrekt $x^2$";
        var segments = NativeMathSyntax.SplitForRendering(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal("$x^2$", Assert.Single(segments, segment => segment.IsMath).Text);
    }

    [Theory]
    [InlineData("x^2^3")]
    [InlineData("T_ECT_i = 3")]
    public void AmbiguousRepeatedScriptsRemainLiteral(string source)
    {
        Assert.DoesNotContain(NativeMathSyntax.SplitForRendering(source), segment => segment.IsMath);
    }

    [Fact]
    public void LongPunctuationAndIncompleteInputRemainBoundedAndLossless()
    {
        var source = new string('(', 20_000) + new string('.', 20_000) + " T_ECT =";
        var segments = NativeMathSyntax.SplitForRendering(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.DoesNotContain(segments, segment => segment.IsMath);
    }
}

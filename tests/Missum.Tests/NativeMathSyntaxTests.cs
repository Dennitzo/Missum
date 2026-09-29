using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeMathSyntaxTests
{
    private static readonly string[] MathAfterCode = ["$x$", @"\[y\]"];

    [Theory]
    [InlineData("$x^2$", false)]
    [InlineData(@"\(\frac{1}{2}\)", false)]
    [InlineData("$$x^2 + y^2 = z^2$$", true)]
    [InlineData(@"\[\int_0^1 x\,dx\]", true)]
    [InlineData("$$\r\n\\begin{aligned}\r\na &= b \\\\\r\nc &= d\r\n\\end{aligned}\r\n$$", true)]
    [InlineData("$5$", false)]
    public void SupportedDelimitersPreserveOriginalSource(string source, bool display)
    {
        var segment = Assert.Single(NativeMathSyntax.Split(source));
        Assert.True(segment.IsMath);
        Assert.Equal(display, segment.Display);
        Assert.Equal(source, segment.Text);
        Assert.True(NativeMathSyntax.ContainsMath(source));
    }

    [Fact]
    public void ProseWhitespaceAndMultipleFormulaKindsRemainLossless()
    {
        const string source = "  Für $x$ gilt:\r\n\\[x + 1 = 2\\]\r\nDann \\(x=1\\).  ";
        var segments = NativeMathSyntax.Split(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(7, segments.Count);
        Assert.Equal("  Für ", segments[0].Text);
        Assert.Equal(" gilt:\r\n", segments[2].Text);
        Assert.Equal(".  ", segments[6].Text);
        Assert.Equal(3, segments.Count(segment => segment.IsMath));
        Assert.True(segments[3].Display);
    }

    [Theory]
    [InlineData(@"Preis: \$5 und \$10.")]
    [InlineData("Preis: $5 und $10.")]
    [InlineData("Preis: $5–$10.")]
    [InlineData("Kosten $100, $200 und $300.")]
    [InlineData("$ x $")]
    [InlineData("$x $")]
    [InlineData("$x\ny$")]
    [InlineData(@"\(x" )]
    [InlineData(@"\[\frac{1}{2}" )]
    [InlineData("$$x + 1")]
    [InlineData("$$unfinished $inner$\nformula")]
    [InlineData("$x + 1")]
    [InlineData(@"\\(kein TeX\\)")]
    public void PricesEscapesAndIncompleteStreamingInputStayLiteral(string source)
    {
        var segment = Assert.Single(NativeMathSyntax.Split(source));
        Assert.False(segment.IsMath);
        Assert.Equal(source, segment.Text);
        Assert.False(NativeMathSyntax.ContainsMath(source));
    }

    [Theory]
    [InlineData("`$x$`")]
    [InlineData("``text `$x$` \\(y\\)``")]
    [InlineData("```csharp\nvar source = \"$x$\";\n```")]
    [InlineData("~~~latex\n\\[x\\]\n~~~")]
    [InlineData("   ```latex\r\n$$x$$\r\n   ```")]
    [InlineData("````text\n```\n$x$\n```\n````")]
    [InlineData("```latex\n$x$")]
    [InlineData("`pending $x$")]
    public void CodeRemainsLiteralIncludingUnfinishedCode(string source)
    {
        var segment = Assert.Single(NativeMathSyntax.Split(source));
        Assert.False(segment.IsMath);
        Assert.Equal(source, segment.Text);
    }

    [Fact]
    public void MathAfterClosedCodeIsStillRecognized()
    {
        const string source = "`$ignored$` and $x$\n~~~text\n$ignored$\n~~~\n\\[y\\]";
        var segments = NativeMathSyntax.Split(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(MathAfterCode, segments.Where(segment => segment.IsMath).Select(segment => segment.Text));
    }

    [Fact]
    public void EscapedDollarInsideFormulaDoesNotCloseIt()
    {
        const string source = @"$\text{Kosten: }\$5$";
        var segment = Assert.Single(NativeMathSyntax.Split(source));
        Assert.True(segment.IsMath);
        Assert.Equal(source, segment.Text);
    }

    [Fact]
    public void APriceCannotCaptureDollarDelimitersInsideCode()
    {
        const string source = "Kosten $5 `var a = \"$x$\";` und \\(y\\).";
        var segments = NativeMathSyntax.Split(source);
        Assert.Equal(source, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(@"\(y\)", Assert.Single(segments, segment => segment.IsMath).Text);
    }

    [Fact]
    public void StreamingPromotesOnlyTheCompletedFormula()
    {
        const string complete = @"Ergebnis: \[\frac{a}{b}\]";
        for (var length = 0; length < complete.Length; length++)
        {
            var prefix = complete[..length];
            var segments = NativeMathSyntax.Split(prefix);
            Assert.Equal(prefix, string.Concat(segments.Select(segment => segment.Text)));
            Assert.DoesNotContain(segments, segment => segment.IsMath);
        }
        Assert.True(NativeMathSyntax.ContainsMath(complete));
    }

    [Fact]
    public void EmptyInputProducesNoBlocks()
    {
        Assert.Empty(NativeMathSyntax.Split(string.Empty));
        Assert.False(NativeMathSyntax.ContainsMath(string.Empty));
    }
}

using System.Globalization;
using Missum.App.Services;

namespace Missum.Tests;

public sealed class NativeContextSummaryTests
{
    [Theory]
    [InlineData(154900, 258000, "154.9k / 258k")]
    [InlineData(0, 258000, "0k / 258k")]
    [InlineData(500, 262144, "0.5k / 262.1k")]
    [InlineData(1048576, 1048576, "1048.6k / 1048.6k")]
    [InlineData(270000, 258000, "270k / 258k")]
    [InlineData(0, 0, "0k / —")]
    [InlineData(1000, -1, "1k / —")]
    [InlineData(-1, 258000, "0k / 258k")]
    public void UsesThousandsWithoutRedundantDecimalsOrInventedLimits(double used, double limit, string expected) =>
        Assert.Equal(expected, NativeContextSummary.Format(used, limit));

    [Fact]
    public void DecimalPointDoesNotDependOnTheWindowsCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("154.9k / 258k", NativeContextSummary.Format(154900, 258000));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingOrNonFiniteProgressDoesNotLeakIntoTheCaption()
    {
        Assert.Equal("0k / —", NativeContextSummary.Format(double.NaN, double.PositiveInfinity));
        Assert.Equal("0k / 258k", NativeContextSummary.Format(double.NegativeInfinity, 258000));
    }
}

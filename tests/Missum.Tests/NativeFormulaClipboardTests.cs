using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeFormulaClipboardTests
{
    private static readonly NativeConversationSelection.ClipboardSpan[] FormulaSentence =
    [
        new(2, 8, "Davor "),
        new(10, 11, @"$\frac{x}{y}=z^2$", true),
        new(13, 21, " danach."),
    ];

    [Fact]
    public void SelectedSentenceRetainsOriginalLatexAndDelimiters()
        => Assert.Equal(@"Davor $\frac{x}{y}=z^2$ danach.", Copy(0, 23));

    [Fact]
    public void FormulaOnlySelectionDoesNotDependOnWinUiSelectedText()
        => Assert.Equal(@"$\frac{x}{y}=z^2$", Copy(10, 11));

    [Theory]
    [InlineData(4, 16, @"vor $\frac{x}{y}=z^2$ da")]
    [InlineData(16, 4, @"vor $\frac{x}{y}=z^2$ da")]
    [InlineData(3, 5, "av")]
    [InlineData(14, 17, "dan")]
    [InlineData(8, 10, "")]
    [InlineData(11, 13, "")]
    [InlineData(10, 10, "")]
    public void PartialAndReverseSelectionsUseNativeOffsets(int start, int end, string expected)
        => Assert.Equal(expected, Copy(start, end));

    [Fact]
    public void MultipleFormulasKeepTheOriginalSourcesWithoutDuplicatingMarkup()
    {
        NativeConversationSelection.ClipboardSpan[] spans =
        [
            new(2, 3, "$x$", true),
            new(5, 10, " und "),
            new(12, 13, @"\(y^2\)", true),
            new(15, 16, "\n", true),
            new(18, 21, "A·m"),
        ];
        Assert.Equal("$x$ und \\(y^2\\)\nA·m", NativeConversationSelection.CopySourceRange(spans, 0, 22));
    }

    [Fact]
    public void AtomicFormulaIsNeverCutIntoInvalidLatex()
    {
        NativeConversationSelection.ClipboardSpan[] spans = [new(2, 5, @"$$\boxed{a+b}$$", true)];
        Assert.Equal(@"$$\boxed{a+b}$$", NativeConversationSelection.CopySourceRange(spans, 3, 4));
    }

    private static string Copy(int start, int end) => NativeConversationSelection.CopySourceRange(FormulaSentence, start, end);
}

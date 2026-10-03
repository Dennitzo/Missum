using Missum.App.Controls;
using Microsoft.UI.Xaml;

namespace Missum.Tests;

public sealed class NativeMarkdownTableTests
{
    [Fact]
    public void ScreenshotTableWithBlankLinesIsOneTableBetweenProse()
    {
        const string source = "Kandidaten\n\n| Theorie | Stärken | Offene Lücken |\n\n|---|---|---|\n\n"
            + "| **Stringtheorie** | Vereinheitlicht alle Kräfte | Keine eindeutige Vorhersage |\n\n"
            + "| Loop-Quantengravitation | Diskrete Geometrie | Klassischer Grenzfall |\n\nWeiterführende Erklärung.";
        var sections = NativeStreamingMarkdown.ParseSections(source);
        Assert.Equal([NativeStreamingMarkdown.SectionKind.Paragraph, NativeStreamingMarkdown.SectionKind.Table,
            NativeStreamingMarkdown.SectionKind.Paragraph], sections.Select(section => section.Kind));
        var table = ReadTable(sections[1].Text);
        Assert.Equal(["Theorie", "Stärken", "Offene Lücken"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("**Stringtheorie**", table.Rows[0][0]);
        Assert.Equal("Weiterführende Erklärung.", sections[2].Text);
    }

    [Fact]
    public void AlignmentAndRowsWithoutOuterPipesArePreserved()
    {
        var table = ReadTable("Begriff | Messwert | Einheit\n:--- | :---: | ---:\nv | 3 | m/s\nE | 9 | J");
        Assert.Equal([TextAlignment.Left, TextAlignment.Center, TextAlignment.Right], table.Alignments);
        Assert.Equal(["E", "9", "J"], table.Rows[1]);
    }

    [Theory]
    [InlineData("links | rechts\nkeine | Tabelle")]
    [InlineData("| a | b |\n| --- | --- | --- |")]
    [InlineData("| a | b |\n| --- | Text |")]
    [InlineData("| a | b |\n| -- | -- |")]
    [InlineData("| a | b |")]
    public void PipeProseAndIncompleteOrMismatchedDelimitersRemainText(string source)
    {
        Assert.DoesNotContain(NativeStreamingMarkdown.ParseSections(source), section => section.Kind == NativeStreamingMarkdown.SectionKind.Table);
    }

    [Fact]
    public void PipesWithinInlineCodeAndEscapesDoNotSplitCells()
    {
        var table = ReadTable("| Ausdruck | Ergebnis |\n| --- | --- |\n| `a | b` | a\\|b |\n| ``a `|` b`` | c | ");
        Assert.Equal(["`a | b`", "a|b"], table.Rows[0]);
        Assert.Equal(["``a `|` b``", "c"], table.Rows[1]);
    }

    [Theory]
    [InlineData("```markdown\n| a | b |\n| --- | --- |\n| 1 | 2 |\n```")]
    [InlineData("~~~markdown\n| a | b |\n\n| --- | --- |\n| 1 | 2 |")]
    public void TablesInsideClosedOrStreamingCodeRemainCode(string source)
    {
        var section = Assert.Single(NativeStreamingMarkdown.ParseSections(source));
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Code, section.Kind);
        Assert.Contains("| --- | --- |", section.Text);
    }

    [Fact]
    public void StreamingHeaderPromotesOnlyWhenEveryDelimiterCellIsComplete()
    {
        const string prefix = "| Größe | Wert | Einheit |\n| --- | --- | ";
        foreach (var suffix in new[] { "", "-", "--" })
            Assert.DoesNotContain(NativeStreamingMarkdown.ParseSections(prefix + suffix), section => section.Kind == NativeStreamingMarkdown.SectionKind.Table);
        var section = Assert.Single(NativeStreamingMarkdown.ParseSections(prefix + "---"));
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Table, section.Kind);
        Assert.Empty(ReadTable(section.Text).Rows);
    }

    [Fact]
    public void LastExplicitPartialRowIsVisibleAndGrowsWithoutLosingCells()
    {
        const string header = "| Größe | Wert | Einheit |\n| --- | --- | --- |\n";
        var partial = ReadTable(header + "| Energie | 9");
        Assert.Equal(["Energie", "9", string.Empty], Assert.Single(partial.Rows));
        var complete = ReadTable(header + "| Energie | 9 | J |");
        Assert.Equal(["Energie", "9", "J"], Assert.Single(complete.Rows));
    }

    [Fact]
    public void MalformedRowsAreNotTruncatedOrAbsorbed()
    {
        const string source = "| A | B |\n| --- | --- |\n| 1 | 2 |\n\n| 3 | 4 | 5 |\nWeitere Erklärung.";
        var sections = NativeStreamingMarkdown.ParseSections(source);
        Assert.Equal(3, sections.Count);
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Table, sections[0].Kind);
        Assert.Single(ReadTable(sections[0].Text).Rows);
        Assert.Equal("| 3 | 4 | 5 |", sections[1].Text);
        Assert.Equal("Weitere Erklärung.", sections[2].Text);
    }

    [Fact]
    public void InlineMathAndMarkdownRemainTokenizedInsideTableCells()
    {
        const string source = "| Symbol | Erklärung |\n| --- | --- |\n| **$v$** | Geschwindigkeit in m/s |\n| $E=mc^2$ | Energie |";
        var section = Assert.Single(NativeStreamingMarkdown.ParseSections(source));
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Table, section.Kind);
        Assert.NotEmpty(section.MathTokenPrefix);
        var table = ReadTable(section.Text);
        var symbol = Assert.Single(NativeStreamingMarkdown.ParseMathInlines(table.Rows[0][0], section.MathTokenPrefix));
        Assert.True(symbol.IsMath);
        Assert.Equal("$v$", symbol.Text);
        Assert.Equal(NativeStreamingMarkdown.InlineKind.Bold, symbol.Kind);
        Assert.Equal("Geschwindigkeit in m/s", table.Rows[0][1]);
    }

    [Fact]
    public void EmptyCellAtEitherEdgeKeepsTheExpectedColumnCount()
    {
        var table = ReadTable("| A | B | C |\n| --- | --- | --- |\n| | 2 | | ");
        Assert.Equal([string.Empty, "2", string.Empty], Assert.Single(table.Rows));
    }

    [Fact]
    public void ExplicitSingleColumnTablesWorkWithoutPromotingSetextHeadings()
    {
        var table = ReadTable("| Begriff |\n| --- |\n| Energie |");
        Assert.Equal(["Begriff"], table.Headers);
        Assert.Equal(["Energie"], Assert.Single(table.Rows));
        Assert.DoesNotContain(NativeStreamingMarkdown.ParseSections("Begriff\n---"), section => section.Kind == NativeStreamingMarkdown.SectionKind.Table);
    }

    private static NativeStreamingMarkdown.TableSpec ReadTable(string source)
    {
        var lines = source.ReplaceLineEndings("\n").Split('\n');
        Assert.True(NativeStreamingMarkdown.TryParseTable(lines, 0, out var table, out _));
        return table;
    }
}

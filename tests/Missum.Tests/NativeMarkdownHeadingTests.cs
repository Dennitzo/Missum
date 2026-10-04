using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativeMarkdownHeadingTests
{
    private static readonly string[] HeadingIndents = ["", "   ", "    ", "\t"];
    [Fact]
    public void IndentedScientificDraftFromLiveReasoningRendersHeadingsWithoutHashMarkers()
    {
        const string source = "Ich formuliere jetzt den Abschnitt.\n\n    ## Entropische Planck-Gravitation (EPT): Eine überprüfbare Theorie\n\n"
            + "    ### Ausgangspunkt\n    Die vorhandenen Ansätze werden verglichen.";
        var sections = NativeStreamingMarkdown.ParseSections(source);
        Assert.Equal(4, sections.Count);
        Assert.Equal(2, sections[1].HeadingLevel);
        Assert.Equal("Entropische Planck-Gravitation (EPT): Eine überprüfbare Theorie", sections[1].Text);
        Assert.Equal(3, sections[2].HeadingLevel);
        Assert.Equal("Ausgangspunkt", sections[2].Text);
        Assert.Equal(0, sections[3].HeadingLevel);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AllAtxLevelsAllowModelIndentationAndHaveBodyRelativeFontScale(int level)
    {
        foreach (var indent in HeadingIndents)
        {
            var section = Assert.Single(NativeStreamingMarkdown.ParseSections(indent + new string('#', level) + " Überschrift"));
            Assert.Equal(level, section.HeadingLevel);
            Assert.Equal("Überschrift", section.Text);
            Assert.InRange(NativeStreamingMarkdown.SectionFontSize(level), NativeStreamingMarkdown.BodyFontSize, NativeStreamingMarkdown.BodyFontSize + 8);
            if (level < 6)
                Assert.True(NativeStreamingMarkdown.SectionFontSize(level) > NativeStreamingMarkdown.SectionFontSize(level + 1));
        }
    }

    [Fact]
    public void StreamingHeadingCanStartAfterProseWithoutBlankLinesOrAbsorbingTheNextLine()
    {
        const string before = "Einleitung.\n    ## ";
        const string title = "Entropische Planck-Gravitation";
        for (var length = 0; length <= title.Length; length++)
        {
            var sections = NativeStreamingMarkdown.ParseSections(before + title[..length]);
            Assert.Equal(2, sections.Count);
            Assert.Equal("Einleitung.", sections[0].Text);
            Assert.Equal(2, sections[1].HeadingLevel);
            Assert.Equal(title[..length].TrimEnd(' ', '\t'), sections[1].Text);
        }
        var complete = NativeStreamingMarkdown.ParseSections(before + title + "\n### Ausgangspunkt\nDer Text folgt sofort.");
        Assert.Equal(4, complete.Count);
        Assert.Equal(title, complete[1].Text);
        Assert.Equal(3, complete[2].HeadingLevel);
        Assert.Equal("Ausgangspunkt", complete[2].Text);
        Assert.Equal(0, complete[3].HeadingLevel);
        Assert.Equal("Der Text folgt sofort.", complete[3].Text);
        var spaced = Assert.Single(NativeStreamingMarkdown.ParseSections("## Entropische  Planck-Gravitation \t"));
        Assert.Equal("Entropische  Planck-Gravitation", spaced.Text);
    }

    [Theory]
    [InlineData("## Titel ##", "Titel")]
    [InlineData("\t### Titel\t###\t", "Titel")]
    [InlineData("## Titel#", "Titel#")]
    [InlineData("## Titel \\#", "Titel \\#")]
    [InlineData("## ###", "")]
    public void OnlyAnUnescapedSpacedClosingHashSequenceIsRemoved(string source, string expected)
    {
        Assert.True(NativeStreamingMarkdown.TryParseHeading(source, out _, out var content));
        Assert.Equal(expected, content);
    }

    [Theory]
    [InlineData("#hashtag")]
    [InlineData("####### Kein Heading")]
    [InlineData("\\## Wörtlicher Titel")]
    [InlineData("Text mit ## Hash im Satz")]
    [InlineData("   \\# Wörtlicher Hash")]
    public void HashtagsEscapesAndHashTextRemainOrdinaryProse(string source)
    {
        var section = Assert.Single(NativeStreamingMarkdown.ParseSections(source));
        Assert.Equal(0, section.HeadingLevel);
        Assert.Equal(source, section.Text);
    }

    [Theory]
    [InlineData("```markdown\n## Beispiel\n### Noch Code\n```")]
    [InlineData("~~~markdown\n## Beispiel\n### Noch Code\n~~~")]
    [InlineData("```markdown\n## Beispiel\n### Noch Code")]
    public void ClosedAndStreamingCodeFencesKeepHeadingMarkersLiteral(string source)
    {
        var section = Assert.Single(NativeStreamingMarkdown.ParseSections(source));
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Code, section.Kind);
        Assert.Equal(0, section.HeadingLevel);
        Assert.Contains("## Beispiel", section.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void HeadingMathTablesAndFormulaCodeRemainSeparateDuringStreaming()
    {
        const string source = "    ## Energie $E=mc^2$\n| Symbol | Bedeutung |\n| --- | --- |\n| $E$ | Energie |\n"
            + "### Rechnung\n$$E=mc^2$$\n```latex\n# $E$ bleibt Quelltext\n```";
        var sections = NativeStreamingMarkdown.ParseSections(source);
        Assert.Equal(2, sections[0].HeadingLevel);
        Assert.NotEmpty(sections[0].MathTokenPrefix);
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Table, sections[1].Kind);
        Assert.Equal(3, sections[2].HeadingLevel);
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Math, sections[3].Kind);
        Assert.Equal(NativeStreamingMarkdown.SectionKind.Code, sections[4].Kind);
        Assert.Equal("# $E$ bleibt Quelltext", sections[4].Text);
    }
}

using Missum.App.Services;

namespace Missum.Tests;

public sealed class SpeechSourcePunctuationTests
{
    [Fact]
    public void SavedArithmeticPromptPreservesLastValueAndMapsBothSentencesToTheirSpeechAndSource()
    {
        const string first = "Berechne mit dem Rechenwerkzeug die Summe und den Mittelwert von 12, 18 und 30.";
        const string second = "Antworte auf Deutsch in höchstens zwei Sätzen.";
        const string prompt = first + " " + second;
        var units = SpeechSourceSegmentation.CreateUnits(prompt);
        Assert.Equal(2, units.Count);
        Assert.Equal(first, units[0].Text);
        Assert.Equal(second, units[1].Text);
        Assert.Equal(MicrophoneTranscriptionService.PrepareSpeechText(first), units[0].SpeechText);
        Assert.Equal(MicrophoneTranscriptionService.PrepareSpeechText(second), units[1].SpeechText);
        Assert.All(units, unit =>
        {
            Assert.Equal("paragraph", unit.Kind);
            Assert.Equal(prompt, prompt.Substring(unit.Start, unit.Length));
        });
        var segments = SpeechSourceSegmentation.CreateDirectSegments(units);
        Assert.Equal(units.Count, segments.Count);
        for (var index = 0; index < units.Count; index++)
        {
            Assert.Equal(units[index].Id, Assert.Single(segments[index].SourceUnitIds));
            Assert.Equal(units[index].SpeechText, segments[index].Text);
        }
    }

    [Fact]
    public void IndentedOrderedListMarkersAreRemovedWithoutRemovingTrailingValues()
    {
        string[] lines = ["  1. Erster Wert: 30.", "\t2) Zweiter Wert: 18.", "   3. Dritter Wert: 12."];
        string[] expected = ["Erster Wert: 30.", "Zweiter Wert: 18.", "Dritter Wert: 12."];
        var markdown = string.Join('\n', lines);
        var units = SpeechSourceSegmentation.CreateUnits(markdown);
        Assert.Equal(expected.Length, units.Count);
        for (var index = 0; index < units.Count; index++)
        {
            Assert.Equal("listItem", units[index].Kind);
            Assert.Equal(index, units[index].BlockIndex);
            Assert.Equal(expected[index], units[index].Text);
            Assert.Equal(MicrophoneTranscriptionService.PrepareSpeechText(expected[index]), units[index].SpeechText);
            Assert.Equal(lines[index], markdown.Substring(units[index].Start, units[index].Length));
        }
    }

    [Theory]
    [InlineData("Der Dezimalwert beträgt 3.14. Danach folgt 30.")]
    [InlineData("Der Dezimalwert beträgt 3,14 und der Bereich ist 12–30. Danach folgt 18.")]
    [InlineData("Das Jahr ist 2026. Danach beginnt 2027.")]
    [InlineData("Die Rechnung endet bei 30. Das entspricht der Summe.")]
    public void NumbersAndSentenceBoundariesRemainInVisibleSource(string text)
    {
        var units = SpeechSourceSegmentation.CreateUnits(text);
        Assert.Equal(text, string.Join(' ', units.Select(unit => unit.Text)));
        Assert.All(units, unit => Assert.Equal(text, text.Substring(unit.Start, unit.Length)));
    }

    [Fact]
    public void InlineMarkdownAndLinksKeepTheirExistingVisibleTextBehavior()
    {
        const string markdown = "Ein **fetter**, _kursiver_ und ~~alter~~ Text mit `Code` und [Link](https://example.com).";
        const string expected = "Ein fetter, kursiver und alter Text mit Code und Link.";
        var units = SpeechSourceSegmentation.CreateUnits(markdown);
        Assert.Equal(expected, Assert.Single(units).Text);
    }

    [Fact]
    public void WrappedParagraphDoesNotTreatMidSentenceNumbersAsAnOrderedList()
    {
        const string markdown = "Die Werte sind 12, 18 und 30.\nDanach bleibt die Satznummer 2026. erhalten.";
        var units = SpeechSourceSegmentation.CreateUnits(markdown);
        Assert.Equal("Die Werte sind 12, 18 und 30. Danach bleibt die Satznummer 2026. erhalten.",
            string.Join(' ', units.Select(unit => unit.Text)));
        Assert.All(units, unit => Assert.Equal(markdown, markdown.Substring(unit.Start, unit.Length)));
    }

    [Fact]
    public void SentenceRangesIdentifyTheOriginalMarkdownAndLineEndingsForRepeatedText()
    {
        const string first = "So steht es z. B. im **Handbuch**.";
        const string second = "Der [zweite Link](https://example.com/a.b) erklärt&#33;";
        const string third = "Die Zeile wird\r\nweitergeführt.";
        const string source = "# Einleitung\r\n\r\n" + first + " " + second + " " + second + " " + third;
        var units = SpeechSourceSegmentation.CreateUnits(source);
        Assert.Equal([first, second, second, third], units.Skip(1).Select(unit => unit.SentenceSource));
        Assert.Equal(source.IndexOf(first, StringComparison.Ordinal), units[1].SentenceStart);
        Assert.Equal(source.IndexOf(second, StringComparison.Ordinal), units[2].SentenceStart);
        Assert.Equal(source.LastIndexOf(second, StringComparison.Ordinal), units[3].SentenceStart);
        Assert.All(units, unit => Assert.Equal(unit.SentenceSource,
            source.Substring(unit.SentenceStart!.Value, unit.SentenceLength!.Value)));
    }

    [Fact]
    public void AnExcerptWithNormalizedLineEndingsKeepsItsOriginalSourceOffset()
    {
        const string prefix = "Erster Satz.\r\n\r\n";
        const string suffix = "Zweiter Satz.\r\nDritter Satz.";
        Assert.Equal(prefix.Length, MissumAiAssistantService.SpeechExcerptOffset(prefix + suffix, suffix.ReplaceLineEndings("\n")));
        Assert.Null(MissumAiAssistantService.SpeechExcerptOffset(prefix + suffix, "Anderer Satz."));
    }
}

using Missum.App.Services;

namespace Missum.Tests;

public sealed class ScientificSessionTitleTests
{
    [Fact]
    public void PublicationRequestUsesTheScientificSubjectInsteadOfItsInstructionPrefix()
    {
        const string prompt = """
            Erstelle eine wissenschaftliche Publikation:
            Einsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik kombinieren und eine allgemeine Gleichung finden. Stelle mehrere verschiedene Hypothesen auf und prüfe ihre Annahmen.
            """;

        Assert.Equal("Einsteinsche Feldgleichungen, Allgemeine Relativitätstheorie und Quantenmechanik",
            ScientificSessionTitle.FromPrompt(prompt));
    }

    [Fact]
    public void ScientificSubjectIsNotLimitedToSixWordsOrSixtyFourCharacters()
    {
        const string topic = "Gravitation, Quantenphysik und thermodynamische Grenzfälle in gekrümmten Raumzeiten";

        var title = ScientificSessionTitle.FromPrompt(topic);

        Assert.Equal(topic, title);
        Assert.True(title!.Length > 64);
        Assert.True(title.Split(' ').Length > 6);
    }

    [Fact]
    public void MarkdownTopicHeadingDoesNotIncludeLaterResearchInstructions()
    {
        const string prompt = """
            Bitte erstelle eine wissenschaftliche Publikation:
            # **Quantenfelder in gekrümmter Raumzeit**

            Untersuche mehrere Modellansätze. Berechne außerdem alle Einheiten und Grenzfälle.
            """;

        Assert.Equal("Quantenfelder in gekrümmter Raumzeit", ScientificSessionTitle.FromPrompt(prompt));
    }

    [Fact]
    public void EnglishPublicationRequestUsesItsTopic()
    {
        const string prompt = "Create a scientific publication: Quantum fields in curved spacetime. Explain the assumptions and derive the equations.";

        Assert.Equal("Quantum fields in curved spacetime", ScientificSessionTitle.FromPrompt(prompt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("Weitermachen")]
    [InlineData("Weiter.")]
    [InlineData("Fortsetzen!")]
    [InlineData("Continue")]
    [InlineData("Resume")]
    public void EmptyAndContinuationPromptsDoNotReplaceTheExistingTitle(string? prompt)
    {
        Assert.Null(ScientificSessionTitle.FromPrompt(prompt));
    }

    [Fact]
    public void LongScientificTitleEndsAtACompleteWordWithinTheStorageLimit()
    {
        var expected = string.Join(' ', Enumerable.Repeat("Quantenfeld", 11)) + " Raumzeitkrümmungen";
        var prompt = expected + " Renormierungseigenschaften";
        Assert.True(expected.Length <= ScientificSessionTitle.MaximumLength);
        Assert.True(prompt.Length > ScientificSessionTitle.MaximumLength);

        var title = ScientificSessionTitle.FromPrompt(prompt);

        Assert.Equal(expected, title);
        Assert.DoesNotContain("Renormierung", title);
    }

    [Fact]
    public void SingleOverlongWordUsesANeutralScientificTitleInsteadOfACutFragment()
    {
        var prompt = new string('x', ScientificSessionTitle.MaximumLength + 1);

        Assert.Equal("Wissenschaftliches Forschungsprojekt", ScientificSessionTitle.FromPrompt(prompt));
    }

    [Fact]
    public void MathematicalSymbolsAndUnicodeArePreserved()
    {
        const string topic = "Quantenfelder in gekrümmter Raumzeit: Gμν + Λgμν = κTμν, ħ und Energie–Impuls-Kopplung 🔬";

        Assert.Equal(topic, ScientificSessionTitle.FromPrompt(topic));
    }

    [Fact]
    public void ScienceIntroductionKeepsTheCompleteStoredTitleBeyondOneHundredTwentyCharacters()
    {
        const string title = "Konsistenz der Einsteinschen Feldgleichungen und quantisierten Materiefelder in gekrümmten Raumzeiten mit nachvollziehbaren Grenzfällen";
        Assert.True(title.Length > 120);
        Assert.True(title.Length <= ScientificSessionTitle.MaximumLength);

        var parts = MissumAiAssistantService.ScienceIntroductionParts(title);

        Assert.StartsWith("**" + title + "**\n\n", parts[0]);
    }
}

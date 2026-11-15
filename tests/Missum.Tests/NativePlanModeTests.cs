using System.Text.Json;
using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativePlanModeTests
{
    private const string Marker = "```assistant-plan\n{\"kind\":\"plan\",\"title\":\"Pi-Berechnung\"}\n```";

    [Theory]
    [InlineData("", "\n\n## Umsetzung\nPython-Skript schreiben.", "# Pi-Berechnung\n\n## Umsetzung\nPython-Skript schreiben.")]
    [InlineData("## Umsetzung\nPython-Skript schreiben.\n\n", "", "# Pi-Berechnung\n\n## Umsetzung\nPython-Skript schreiben.")]
    [InlineData("## Umsetzung\n\n", "\n\n## Prüfung", "# Pi-Berechnung\n\n## Umsetzung\n\n## Prüfung")]
    [InlineData("", "", "")]
    public void HidesOnlyTheMarkerAndApprovesTheVisiblePlan(string before, string after, string expectedPlan)
    {
        var content = before + Marker + after;
        var visible = NativeAssistantPage.HidePlanPayload(content);

        Assert.Equal(before + new string(' ', Marker.Length) + after, visible);
        Assert.Equal(expectedPlan, NativeAssistantPage.PlanApprovalText(content));
    }

    [Fact]
    public void KeepsTitleOnlyOnceWhenThePlanAlreadyStartsWithIt()
    {
        const string content = "# Pi-Berechnung\n\n## Umsetzung\nBerechnen.\n\n" + Marker;
        Assert.Equal("# Pi-Berechnung\n\n## Umsetzung\nBerechnen.", NativeAssistantPage.PlanApprovalText(content));
    }

    [Fact]
    public void KeepsMarkdownTitleOnlyOnceAndIncludesAllContent()
    {
        const string content = "```assistant-plan\n{\"kind\":\"plan\",\"title\":\"Mein Plan\",\"markdown\":\"# Mein Plan\\n\\n## Ziel\\nPlantext\"}\n```";
        Assert.Equal("# Mein Plan\n\n## Ziel\nPlantext", NativeAssistantPage.PlanApprovalText(content));
    }

    [Fact]
    public void PreviewIsBoundedAndOmitsRepeatedHeading()
    {
        var preview = NativeAssistantPage.PlanPreview("# Mein Plan\n\n" + new string('a', 500), "Mein Plan");
        Assert.True(preview.Length <= 260);
        Assert.DoesNotContain("Mein Plan", preview);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void ApprovesMarkdownWithCodeFenceInsideTheJsonField()
    {
        const string content = "```assistant-plan\n{\"kind\":\"plan\",\"markdown\":\"Prüfen mit:\\n```sh\\npython main.py\\n```\"}\n```";
        Assert.Equal("Prüfen mit:\n```sh\npython main.py\n```", NativeAssistantPage.PlanApprovalText(content));
    }

    [Theory]
    [InlineData("## Offene Fragen", true)]
    [InlineData("**Offene Fragen:**", false)]
    [InlineData("Offene Fragen", false)]
    public void OpenDecisionsBecomeQuestionsAndBlockApproval(string heading, bool insidePlan)
    {
        var questions = heading + "\n\n1. Welche Lesarten umfasst Brennleuchten.\n"
            + "   Sollen alle drei vorkommen.\n"
            + "2. Wo stehen die Musterlösungen.\n"
            + "3. Werden Fotografien bereitgestellt.";
        var markdown = insidePlan ? "## Umsetzung\nGliederung erstellen.\n\n" + questions
            : "## Umsetzung\nGliederung erstellen.";
        var payload = JsonSerializer.Serialize(new { kind = "plan", title = "Publikation", markdown });
        var content = "```assistant-plan\n" + payload + "\n```" + (insidePlan ? "" : "\n\n" + questions);

        Assert.Empty(NativeAssistantPage.PlanApprovalText(content));
        Assert.Equal([
            "Welche Lesarten umfasst Brennleuchten. Sollen alle drei vorkommen.",
            "Wo stehen die Musterlösungen.",
            "Werden Fotografien bereitgestellt."
        ], NativeAssistantPage.ReadOpenPlanQuestions(questions));
    }

    [Fact]
    public void NoOpenDecisionsKeepsPlanWithResearchQuestionsAndExamplesApprovable()
    {
        const string markdown = "## Offene Forschungsfragen\n- Welche Eigenschaften hat das Metall?\n\n"
            + "```text\n## Offene Fragen\n1. Beispiel im Codeblock?\n```\n\n"
            + "## Offene Fragen\nKeine.\n\n## Prüfung\nBefunde mit Quellen vergleichen.";
        var payload = JsonSerializer.Serialize(new { kind = "plan", title = "Publikation", markdown });

        Assert.Equal("# Publikation\n\n" + markdown,
            NativeAssistantPage.PlanApprovalText("```assistant-plan\n" + payload + "\n```"));
    }

    [Fact]
    public void ExplicitQuestionsOnPlanPayloadBlockApproval()
    {
        const string content = "```assistant-plan\n{\"kind\":\"plan\",\"markdown\":\"## Umsetzung\","
            + "\"questions\":[{\"text\":\"Welche Lesart ist gemeint?\"}]}\n```";

        Assert.Empty(NativeAssistantPage.PlanApprovalText(content));
    }

    [Fact]
    public void RecognizesEscapedMarkdownListNumbersAndHardLineBreaks()
    {
        const string markdown = "Offene Fragen\\\n\n1\\. Welche Lesart.\\\n\n2\\. Welcher Anhang.";

        Assert.Equal(["Welche Lesart.", "Welcher Anhang."], NativeAssistantPage.ReadOpenPlanQuestions(markdown));
    }
}

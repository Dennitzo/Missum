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
}

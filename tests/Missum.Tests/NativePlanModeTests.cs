using Missum.App.Pages;

namespace Missum.Tests;

public sealed class NativePlanModeTests
{
    private const string Marker = "```assistant-plan\n{\"kind\":\"plan\",\"title\":\"Pi-Berechnung\"}\n```";

    [Theory]
    [InlineData("", "\n\n## Umsetzung\nPython-Skript schreiben.", "## Umsetzung\nPython-Skript schreiben.")]
    [InlineData("## Umsetzung\nPython-Skript schreiben.\n\n", "", "## Umsetzung\nPython-Skript schreiben.")]
    [InlineData("## Umsetzung\n\n", "\n\n## Prüfung", "## Umsetzung\n\n## Prüfung")]
    [InlineData("", "", "")]
    public void HidesOnlyTheMarkerAndApprovesTheVisiblePlan(string before, string after, string expectedPlan)
    {
        var content = before + Marker + after;
        var visible = NativeAssistantPage.HidePlanPayload(content);

        Assert.Equal(before + new string(' ', Marker.Length) + after, visible);
        Assert.Equal(expectedPlan, NativeAssistantPage.PlanApprovalText(content));
    }

    [Fact]
    public void ApprovesMarkdownWithCodeFenceInsideTheJsonField()
    {
        const string content = "```assistant-plan\n{\"kind\":\"plan\",\"markdown\":\"Prüfen mit:\\n```sh\\npython main.py\\n```\"}\n```";
        Assert.Equal("Prüfen mit:\n```sh\npython main.py\n```", NativeAssistantPage.PlanApprovalText(content));
    }
}

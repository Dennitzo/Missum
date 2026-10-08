using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ScienceMediaToolSelectionTests
{
    [Fact]
    public void ScientificWebResearchCanInspectAndAnalyzeItsFigures()
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(PromptTriggerAction.WebSearch,
            "Prüfe die Diagramme der Simulation.", ChatMode.ClaudeScience);

        Assert.Equal(["web.search", "web.fetch", "media.inspect", "media.analyze"], tools);
        Assert.DoesNotContain("image.generate", tools);
        Assert.DoesNotContain("speech.synthesize", tools);
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    public void OtherWebSearchModesKeepTheirExistingToolSelection(ChatMode chatMode)
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(PromptTriggerAction.WebSearch,
            "Vergleiche die Quellen.", chatMode);

        Assert.Equal(["web.search", "web.fetch"], tools);
    }

    [Fact]
    public void AutomaticScientificResearchAlreadyIncludesVisualAnalysis()
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(null,
            "Prüfe die Diagramme der Simulation.", ChatMode.ClaudeScience);

        Assert.Contains("media.inspect", tools);
        Assert.Contains("media.analyze", tools);
    }

    [Fact]
    public void AudiobookRemainsWithoutServerToolsEvenInScience()
    {
        var tools = MissumAiAssistantService.GetAllowedServerTools(PromptTriggerAction.Audiobook,
            "Lies den Bericht vor.", ChatMode.ClaudeScience);

        Assert.Empty(tools);
    }
}

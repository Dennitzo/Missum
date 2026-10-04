using Missum.App.Services;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ScienceResearchPromptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScienceWebSearchKeepsTheFullTaskWithoutLegacySdkPreparation(bool deepResearch)
    {
        const string task = "Setze die Theorie fort.\nBewahre Annahmen, Einheiten und offene Gegenbeispiele.";
        var trigger = Trigger(PromptTriggerAction.WebSearch, task, deepResearch);
        var transformed = MissumAiAssistantService.BuildWebResearchPrompt(task);

        var prompt = MissumAiAssistantService.ResolveScientificResearchPrompt(ChatMode.ClaudeScience,
            "Websuche: " + task, trigger, transformed);

        Assert.Equal(task, prompt);
        Assert.DoesNotContain("MISSUM_WEB_RESEARCH_REQUEST", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("isolierten SDK-Schritten", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Evidenzdossier", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ScienceEmptyTriggerRemainderPreservesTheOriginalUserInput()
    {
        const string original = "Weitermachen";
        var trigger = Trigger(PromptTriggerAction.WebSearch, " ", false);

        Assert.Equal(original, MissumAiAssistantService.ResolveScientificResearchPrompt(
            ChatMode.ClaudeScience, original, trigger, "legacy preparation"));
    }

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    public void OtherModesPreserveStagedWebResearch(ChatMode mode)
    {
        const string task = "Vergleiche zwei aktuelle Originalquellen.";
        var trigger = Trigger(PromptTriggerAction.WebSearch, task, false);
        var transformed = MissumAiAssistantService.BuildWebResearchPrompt(task);

        var prompt = MissumAiAssistantService.ResolveScientificResearchPrompt(mode, task, trigger, transformed);

        Assert.Equal(transformed, prompt);
        Assert.Contains("MISSUM_WEB_RESEARCH_REQUEST", prompt, StringComparison.Ordinal);
        Assert.Contains("isolierten SDK-Schritten", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PromptTriggerAction.Translation)]
    [InlineData(PromptTriggerAction.DocumentCreate)]
    public void ScienceKeepsUnrelatedToolInstructions(PromptTriggerAction action)
    {
        const string transformed = "Nutze das ausgewählte Werkzeug für diesen Auftrag.";
        var trigger = Trigger(action, "Auftrag", false);

        Assert.Equal(transformed, MissumAiAssistantService.ResolveScientificResearchPrompt(
            ChatMode.ClaudeScience, "Auftrag", trigger, transformed));
    }

    [Fact]
    public void ScienceWithoutToolTriggerDoesNotRewriteUserText()
    {
        const string userText = "Erkläre den Marker [MISSUM_WEB_RESEARCH_REQUEST] und das Wort Rechercheauftrag:.";

        Assert.Equal(userText, MissumAiAssistantService.ResolveScientificResearchPrompt(
            ChatMode.ClaudeScience, userText, null, userText));
    }

    private static PromptTriggerMatch Trigger(PromptTriggerAction action, string task, bool deepResearch) =>
        new(new PromptTrigger(Guid.Empty, action, "Websuche", "Test", PromptTriggerMatchMode.Prefix,
            true, 1, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), task, task, deepResearch);
}

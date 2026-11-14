using Missum.App.Services;
using Missum.Core.Extensions;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ComputerRoutingSelectionTests
{
    [Fact]
    public void AutomaticSelectionIsLimitedToTheDefaultCodingRun()
    {
        var coding = AssistantCoordinator.CreateToolMatch("coding", "Bediene das Fenster");
        Assert.True(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, coding, ChatMode.Coding));
        Assert.False(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, coding, ChatMode.ClaudeScience));
        Assert.False(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, coding with { DeepResearch = true }, ChatMode.Coding));
        Assert.False(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding,
            coding with { Trigger = coding.Trigger with { Id = Guid.NewGuid() } }, ChatMode.Coding));
        var blender = AssistantCoordinator.CreateExtensionMatch("com.missum.blender/edit", "Öffne Blender");
        Assert.False(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, blender, ChatMode.Coding));
        var computer = AssistantCoordinator.CreateExtensionMatch(BuiltInActionIds.ComputerUse, "Öffne ein Fenster");
        Assert.False(MissumAiAssistantService.ShouldAutoRouteComputer(PromptTriggerAction.Coding, computer, ChatMode.Coding));
    }
}

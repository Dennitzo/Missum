using System.Text.Json;
using System.Text.Json.Serialization;
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

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static ExtensionActionCatalog InstalledIntegrations(params string[] names)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var catalog = new ExtensionActionCatalog();
        foreach (var name in names)
            catalog.Register(JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(
                Path.Combine(directory.FullName, "extensions", name, "manifest", "extension.json")), ManifestOptions)!);
        return catalog;
    }

    [Fact]
    public void NamedProgramIntegrationWinsOverDesktopControl()
    {
        var catalog = InstalledIntegrations("unreal", "blender");
        // The exported MissumDivided prompt went to desktop control instead of the Unreal tools.
        Assert.Equal("com.missum.unreal/edit", MissumAiAssistantService.MatchIntegrationAction(
            "Erstelle ein neues Unreal Engine Projekt namens \"Missum Divided\".", catalog));
        Assert.Equal("com.missum.unreal/edit", MissumAiAssistantService.MatchIntegrationAction("Öffne MissumDivided.uproject in UE5.8", catalog));
        Assert.Equal("com.missum.blender/edit", MissumAiAssistantService.MatchIntegrationAction("Modelliere in Blender eine Kiste", catalog));
        Assert.Null(MissumAiAssistantService.MatchIntegrationAction("Klicke im Explorer auf Dokumente", catalog));
        Assert.Null(MissumAiAssistantService.MatchIntegrationAction("Ein unrealistischer Plan", catalog));
        // Without the installed integration the classifier decides as before.
        Assert.Null(MissumAiAssistantService.MatchIntegrationAction("Öffne Unreal", InstalledIntegrations()));
    }

    [Fact]
    public void ObservationOutlivesASlowLocalModel()
    {
        // GLM needed about 2.5 min between computer.observe and computer.act; 60 s failed every action.
        Assert.True(ComputerDesktopBroker.ObservationLifetime >= TimeSpan.FromMinutes(10));
    }
}

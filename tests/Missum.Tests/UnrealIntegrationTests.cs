using System.Text.Json;
using System.Text.Json.Serialization;
using Missum.Core.Extensions;

namespace Missum.Tests;

public sealed class UnrealIntegrationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void PackageDeclaresSelectableCodingActionAndReadOnlyStatus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(
            File.ReadAllText(Path.Combine(directory.FullName, "extensions", "unreal", "manifest", "extension.json")), Options);
        var validation = ExtensionManifestValidator.Validate(manifest);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        var action = Assert.Single(manifest!.Actions);
        Assert.Equal("com.missum.unreal/edit", action.ActionId);
        Assert.Equal("integrations", action.GroupId);
        Assert.Equal(ExtensionActionKind.SelectableTool, action.ActionKind);
        Assert.Equal(new[] { ExtensionChatMode.Coding }, action.SupportedChatModes);
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.status" && tool.RiskClass == ExtensionToolRiskClass.ReadOnly);
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.connect" && tool.RiskClass != ExtensionToolRiskClass.ReadOnly);
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.camera.create");
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.view.capture");
    }
}

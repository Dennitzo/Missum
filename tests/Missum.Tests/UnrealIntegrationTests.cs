using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Missum.App.Services.Extensions;
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
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.camera");
        Assert.Contains(manifest.Tools, tool => tool.ModelToolName == "unreal.view.capture");
    }

    [Fact]
    public void InjectedLauncherFactsPassTheInvocationPolicy()
    {
        // Live run in MissumDivided: every unreal.* call failed with "Argument 'executable' benötigt die
        // deklarierte Prozessberechtigung", because the launcher path was injected as "executable".
        var workspace = Directory.CreateTempSubdirectory("missum-unreal-policy").FullName;
        try
        {
            var permissions = new[] { ExtensionPermissionKind.FileWrite, ExtensionPermissionKind.Network };
            var arguments = new JsonObject
            {
                ["_missumUnrealLauncher"] = ExtensionRuntimeService.UnrealLauncherNode(
                    @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", true, "process", 4242),
                ["folder"] = "/Game/Maps",
                ["_missumUnrealEngines"] = new JsonArray(new JsonObject { ["association"] = "5.8", ["version"] = "5.8.4", ["root"] = @"E:\UnrealEngine\UE_5.8" }),
            };
            ExtensionInvocationPolicy.Validate(new ExtensionActionInvocation("com.missum.unreal/edit",
                JsonSerializer.SerializeToElement(arguments), new(permissions, workspace), "unreal.status"), permissions, workspace);
        }
        finally { Directory.Delete(workspace, true); }
    }

    [Theory]
    [InlineData("/Game/Maps", true)]
    [InlineData("/Engine/BasicShapes", true)]
    [InlineData("/Game", true)]
    [InlineData("/Windows/System32", false)]
    [InlineData("/Game/../Windows", false)]
    [InlineData("//server/share", false)]
    [InlineData("Import/Blender", false)]
    public void OnlyEngineMountPointsCountAsPackagePaths(string value, bool expected) =>
        Assert.Equal(expected, ExtensionInvocationPolicy.IsEnginePackagePath(value));

    [Fact]
    public void FolderOutsideTheEngineMountsIsStillRejected()
    {
        var workspace = Directory.CreateTempSubdirectory("missum-unreal-policy").FullName;
        try
        {
            var permissions = new[] { ExtensionPermissionKind.FileWrite };
            Assert.Throws<InvalidDataException>(() => ExtensionInvocationPolicy.Validate(new ExtensionActionInvocation(
                "com.missum.unreal/edit", JsonSerializer.SerializeToElement(new { folder = "/Windows/System32" }),
                new(permissions, workspace), "unreal.level"), permissions, workspace));
        }
        finally { Directory.Delete(workspace, true); }
    }
}

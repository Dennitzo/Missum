using System.Text.Json;
using System.Text.Json.Serialization;
using Missum.App.Services;
using Missum.Core.Extensions;
using Missum.Core.Models;

namespace Missum.Tests;

/// <summary>
/// The Blender package is the reference case for two catalogue features: a package that declares
/// its own menu group and one action that unlocks several model tools for the same coding run.
/// Both are checked against the manifest that actually ships.
/// </summary>
public sealed class BlenderIntegrationTests
{
    private const string BlenderActionId = "com.missum.blender/edit";

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static readonly string[] ExpectedTools =
    [
        "blender.status",
        "blender.scene.read",
        "blender.scene.edit",
        "blender.view.capture",
        "blender.custom.run",
    ];

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void BlenderReviewRequiresARealAnalysisOfTheCurrentCapture(
        bool hasAnalysis, bool exactImage, bool currentOperation, bool accepted)
    {
        const string imageHash = "7871b8af2781b51837e046f95b05a2a14fdd1341f481d6389c28c9d1849482a5";
        var now = DateTimeOffset.UtcNow;
        var arguments = JsonSerializer.SerializeToElement(new
        {
            visionReview = new { verdict = "verified", operationId = "move-sphere", analyzedSha256 = imageHash },
        });
        var steps = new List<AssistantToolStep>
        {
            new("capture", "blender.view.capture", "completed", StartedAt: now,
                OutputJson: JsonSerializer.Serialize(new { operationId = currentOperation ? "move-sphere" : "older-edit", sha256 = imageHash })),
        };
        if (hasAnalysis)
        {
            steps.Add(new("vision", "media.analyze", "completed", StartedAt: now.AddSeconds(1),
                OutputJson: JsonSerializer.Serialize(new
                {
                    analysis = "Die blaue Kugel ist sichtbar.",
                    primaryUpload = new { sha256 = imageHash, exactRequestedUpload = exactImage },
                })));
        }
        Assert.Equal(accepted, MissumAiAssistantService.BlenderReviewHasVisionReceipt(arguments, steps, now));
    }

    [Fact]
    public void BlenderManifestIsValidAndPlacesTheActionInTheIntegrationGroup()
    {
        var manifest = ReadBlenderManifest();

        var validation = ExtensionManifestValidator.Validate(manifest);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));

        var action = Assert.Single(manifest.Actions);
        Assert.Equal(BlenderActionId, action.ActionId);
        Assert.Equal("integrations", action.GroupId);
        Assert.Equal(ExtensionActionKind.SelectableTool, action.ActionKind);
        Assert.Equal(new[] { ExtensionChatMode.Coding }, action.SupportedChatModes);

        var group = Assert.Single(manifest.Groups ?? []);
        Assert.Equal("integrations", group.GroupId);
        Assert.Equal("Programmintegrationen", group.Label);
        Assert.Equal(60, group.GroupOrder);

        Assert.Equal(ExpectedTools, manifest.Tools.Select(tool => tool.ModelToolName).ToArray());
        Assert.All(manifest.Tools, tool => Assert.Equal(BlenderActionId, tool.ActionId));

        var entrypoint = Assert.Single(manifest.Entrypoints, candidate => candidate.Kind == ExtensionEntrypointKind.Desktop);
        Assert.Equal("adapter/Missum.BlenderAdapter.exe", entrypoint.Path);
    }

    [Fact]
    public void BlenderRuntimeSavesWithoutLeavingBackupFilesBehind()
    {
        var runtime = Path.Combine(FindRepositoryRoot(), "extensions", "blender", "runtime");
        var source = File.ReadAllText(Path.Combine(runtime, "scene_ops.py"));

        // Saving must switch Blender's backup off and restore the previous value afterwards,
        // otherwise every save leaves another .blend1 file next to the project.
        Assert.Contains("save_version", source, StringComparison.Ordinal);
        Assert.Contains("preferences.filepaths", source, StringComparison.Ordinal);
        Assert.Contains("previous_versions", source, StringComparison.Ordinal);
        Assert.Contains("project.saveAs", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogUnlocksEveryBlenderToolForTheSelectedAction()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        catalog.Register(ReadBlenderManifest());

        Assert.True(catalog.TryGetTools(BlenderActionId, out var tools));
        Assert.Equal(ExpectedTools, tools!.Select(tool => tool.ModelToolName).ToArray());

        // Integrations written against one tool per action keep working: the legacy lookup
        // answers with the first tool of the selected action.
        Assert.True(catalog.TryGetTool(BlenderActionId, out var first));
        Assert.Equal(ExpectedTools[0], first!.ModelToolName);

        Assert.True(catalog.TryGetToolByModelName("blender.view.capture", out var capture));
        Assert.Equal(BlenderActionId, capture!.ActionId);
    }

    [Fact]
    public void BlenderActionAppearsOnlyInCodingModeUnderTheIntegrationGroup()
    {
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        catalog.Register(ReadBlenderManifest());

        var codingAction = Assert.Single(
            catalog.GetActions(ExtensionChatMode.Coding), action => action.ActionId == BlenderActionId);
        Assert.Equal("integrations", codingAction.GroupId);

        Assert.DoesNotContain(catalog.GetActions(ExtensionChatMode.General), action => action.ActionId == BlenderActionId);
        Assert.DoesNotContain(
            catalog.GetActions(ExtensionChatMode.ClaudeScience), action => action.ActionId == BlenderActionId);

        var groups = catalog.GetGroups();
        var integration = Assert.Single(groups, group => group.GroupId == "integrations");
        Assert.Equal("Programmintegrationen", integration.Label);
        Assert.Equal(groups.Max(static group => group.GroupOrder), integration.GroupOrder);
        Assert.Equal(
            groups.OrderBy(static group => group.GroupOrder)
                .ThenBy(static group => group.GroupId, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.GroupId),
            groups.Select(static group => group.GroupId));
    }

    [Fact]
    public void ManifestValidatorAcceptsSeveralToolsPerActionAndRefusesTooMany()
    {
        var manifest = ReadBlenderManifest();

        var three = WithToolCount(manifest, 3);
        var threeResult = ExtensionManifestValidator.Validate(three);
        Assert.True(threeResult.IsValid, string.Join(" ", threeResult.Errors));

        var tooMany = WithToolCount(manifest, ExtensionManifestValidator.MaximumToolsPerAction + 1);
        var result = ExtensionManifestValidator.Validate(tooMany);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("mehr als", StringComparison.Ordinal));
    }

    private static ExtensionManifest WithToolCount(ExtensionManifest manifest, int count)
    {
        var tools = new ExtensionToolDescriptor[count];
        for (var index = 0; index < count; index++)
        {
            var source = manifest.Tools[index % manifest.Tools.Length];
            tools[index] = index < manifest.Tools.Length
                ? source
                : source with { ModelToolName = $"blender.additional.{index}" };
        }

        return manifest with { Tools = tools };
    }

    private static ExtensionManifest ReadBlenderManifest()
    {
        var path = Path.Combine(FindRepositoryRoot(), "extensions", "blender", "manifest", "extension.json");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), ManifestJson);
        return manifest ?? throw new InvalidDataException($"Das Blender-Manifest wurde nicht gelesen: {path}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Missum.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Das Missum-Repository wurde aus dem Testausgabeverzeichnis nicht gefunden.");
    }
}

using System.Text.Json;
using Missum.App.Services;
using Missum.App.Services.Extensions;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Extensions;
using Missum.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class ExtensionActionDescriptorIntegrationTests
{
    private const string ActionId = "com.missum.fixture/show-result";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true, null, null, null)]
    [InlineData(false, null, null, "Der ExtensionHost ist für diese Aktion nicht verfügbar.")]
    [InlineData(true, "Das Paket ist deaktiviert.", null, "Das Paket ist deaktiviert.")]
    [InlineData(true, null, "Die Aktion ist im Katalog gesperrt.", "Die Aktion ist im Katalog gesperrt.")]
    public async Task SnapshotPreservesTheActualAvailabilityOfInstalledActions(
        bool runtimePresent, string? runtimeReason, string? catalogReason, string? expectedReason)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Extension-Aktionen", ChatMode.General);
        var workspace = Path.Combine(environment.Directory, "workspace");
        Directory.CreateDirectory(workspace);
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, activateCoding: false);
        await environment.Get<ISettingsStore>().SaveAsync(new AppSettings
        {
            ActiveSessionId = session.Id,
        });
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var catalog = ExtensionActionCatalog.CreateWithBuiltIns();
        catalog.Register(new ExtensionManifest(
            ExtensionPackageFormat.CurrentSchemaVersion, "com.missum.fixture", "1.0.0", "Test-Erweiterung", "Missum Tests",
            [new(ExtensionEntrypointKind.Desktop, "fixture.dll")], [],
            [new(ActionId, "Ergebnis anzeigen", "Ergebnis der Erweiterung anzeigen", "extension", "extensions", 70, 10,
                [ExtensionChatMode.General], ExtensionActionKind.Immediate, ExtensionSelectionBehavior.None, catalogReason)], []));
        var runtime = new AvailabilityRuntime(runtimeReason);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), null, settings, recent,
            extensionActions: catalog, extensionRuntime: runtimePresent ? runtime : null);

        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), Json);
        var descriptor = Assert.Single(snapshot.GetProperty("actionDescriptors").EnumerateArray(),
            item => item.GetProperty("actionId").GetString() == ActionId);

        Assert.Equal("immediate", descriptor.GetProperty("actionKind").GetString());
        Assert.Equal(expectedReason, descriptor.GetProperty("disabledReason").GetString());
        if (runtimePresent && catalogReason is null)
        {
            Assert.Equal(ActionId, runtime.CheckedActionId);
            Assert.Equal(workspace, runtime.CheckedWorkspace);
        }
        else Assert.Null(runtime.CheckedActionId);
    }

    private sealed class AvailabilityRuntime(string? disabledReason) : IExtensionRuntimeService
    {
        public string? CheckedActionId { get; private set; }
        public string? CheckedWorkspace { get; private set; }

        public string? GetDisabledReason(string actionId, string? workspaceRoot)
        {
            CheckedActionId = actionId;
            CheckedWorkspace = workspaceRoot;
            return disabledReason;
        }

        public Task<JsonElement> InvokeAsync(string actionId, JsonElement arguments, string? workspaceRoot,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Building a menu snapshot must never invoke an extension.");
    }
}

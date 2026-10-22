using System.Text.Json;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class CodingToolStepSettingsTests
{
    [Fact]
    public void AutomaticDisclosurePreferenceIsNotPartOfTheSettingsModelOrEditor()
    {
        Assert.Null(typeof(AppSettings).GetProperty("CodingToolStepsExpanded"));
        Assert.Null(typeof(SettingsViewModel).GetProperty("CodingToolStepsExpanded"));
        Assert.Null(typeof(AssistantSettingsPatch).GetProperty("CodingToolStepsExpanded"));
        var json = JsonSerializer.SerializeToElement(new AppSettings(), JsonSerializerOptions.Web);
        Assert.False(json.TryGetProperty("codingToolStepsExpanded", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDisclosurePreferenceIsDroppedOnLoadAndPreservesOtherPreferences(bool expanded)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new
        {
            version = AppSettings.CurrentVersion,
            preferencesRevision = 12,
            selectedModel = "coding/existing-general~1234",
            codingToolStepsExpanded = expanded,
            isAutomaticSpeechEnabled = false,
            theme = "light",
            accentColor = "#A970FF",
        }));

        var restored = await store.LoadAsync();

        Assert.Equal(AppSettings.CurrentVersion, restored.Version);
        Assert.Equal("coding/existing-general~1234", restored.SelectedModel);
        Assert.Equal(12, restored.PreferencesRevision);
        Assert.False(restored.IsAutomaticSpeechEnabled);
        Assert.Equal(AppTheme.Light, restored.Theme);
        Assert.Equal("#A970FF", restored.AccentColor);
        using var savedJson = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.False(savedJson.RootElement.TryGetProperty("codingToolStepsExpanded", out _));
        Assert.Equal("coding/existing-general~1234", savedJson.RootElement.GetProperty("selectedModel").GetString());
    }

    [Fact]
    public async Task SharedSettingsAndConversationSnapshotsDoNotPublishRemovedDisclosurePreference()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = CreateService(environment, settings, connection);
        var settingsSnapshot = JsonSerializer.SerializeToElement(await service.GetSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.False(settingsSnapshot.GetProperty("values").TryGetProperty("codingToolStepsExpanded", out _));

        var coordinator = CreateCoordinator(environment, settings);
        var snapshot = JsonSerializer.SerializeToElement(await coordinator.BuildSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.False(snapshot.TryGetProperty("codingToolStepsExpanded", out _));
        JsonElement? refreshed = null;
        await coordinator.HandleAsync(new WebBridgeEnvelope(
            AssistantProtocol.ProtocolVersion, "conversation.refresh", "compact-refresh",
            JsonSerializer.SerializeToElement(new { sessionId = snapshot.GetProperty("activeSessionId").GetGuid() })),
            (type, payload, requestId) =>
            {
                Assert.Equal("conversation.snapshot", type);
                Assert.Equal("compact-refresh", requestId);
                refreshed = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            });
        var refreshedSnapshot = Assert.IsType<JsonElement>(refreshed);
        Assert.False(refreshedSnapshot.TryGetProperty("codingToolStepsExpanded", out _));
    }

    [Fact]
    public async Task RemovedPreferenceCannotBeReenabledByAnOldSettingsPatch()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = CreateService(environment, settings, connection);
        var events = new List<(string Type, JsonElement Payload)>();
        var revision = settings.Current.PreferencesRevision;
        await service.HandleAsync(new WebBridgeEnvelope(AssistantProtocol.ProtocolVersion, "settings.update", "old-editor",
            JsonSerializer.SerializeToElement(new { expectedRevision = revision, values = new { codingToolStepsExpanded = true } })),
            (type, payload, _) =>
            {
                events.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
                return Task.CompletedTask;
            });

        var error = Assert.Single(events);
        Assert.Equal("settings.error", error.Type);
        Assert.Contains("codingToolStepsExpanded", error.Payload.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(revision, settings.Current.PreferencesRevision);
    }

    private static AssistantSettingsService CreateService(TestEnvironment environment, SettingsCoordinator settings,
        MissumAiConnectionService connection) => new(settings, connection,
            environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>());

    private static AssistantCoordinator CreateCoordinator(TestEnvironment environment, SettingsCoordinator settings)
    {
        var recentActivity = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        recentActivity.Restore();
        return new AssistantCoordinator(
            environment.Get<IChatRepository>(), environment.Get<IDocumentIngestor>(), environment.Get<IContextAssembler>(),
            environment.Get<IPromptTriggerRepository>(), environment.Get<IAssistantAttachmentRepository>(),
            environment.Get<IChatArtifactRepository>(), environment.Get<IConversationSnapshotRepository>(),
            null, settings, recentActivity);
    }
}

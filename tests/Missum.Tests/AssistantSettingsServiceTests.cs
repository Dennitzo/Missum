using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class AssistantSettingsServiceTests
{
    [Fact]
    public void NativeUnsavedThemeAccentAndBackgroundCannotReplaceTheSavedBrowserPalette()
    {
        var saved = new AppSettings { Theme = AppTheme.Dark, AccentColor = "#A970FF", BackgroundColor = "#6B6872" };
        Assert.True(Missum.App.App.MatchesSavedAppearance(saved, AppTheme.Dark, "#a970ff", "#6b6872"));
        Assert.False(Missum.App.App.MatchesSavedAppearance(saved, AppTheme.Light, saved.AccentColor, saved.BackgroundColor));
        Assert.False(Missum.App.App.MatchesSavedAppearance(saved, saved.Theme, "#FFFFFF", saved.BackgroundColor));
        Assert.False(Missum.App.App.MatchesSavedAppearance(saved, saved.Theme, saved.AccentColor, "#000000"));
    }

    [Theory]
    [InlineData(false, "dark", "#1F1F1F", "#FFFFFF")]
    [InlineData(true, "light", "#E4E1E6", "#000000E4")]
    public void SystemChangesResolveSavedColorsIndependentlyOfNativePreview(bool systemLight, string theme, string window, string foreground)
    {
        var saved = new AppSettings { Theme = AppTheme.System, AccentColor = "#A970FF", BackgroundColor = "#181818" };
        var appearance = Missum.App.App.CreateSavedAppearance(saved, systemLight, null);
        Assert.Equal(theme, appearance.Theme);
        Assert.Equal(window, appearance.Colors["window"]);
        Assert.Equal(foreground, appearance.Colors["text"]);
        Assert.Equal("#A970FF", appearance.Colors["accent"]);
        Assert.Equal("#A970FF24", appearance.Colors["accentSubtle"]);
        Assert.Equal(AppTheme.System, saved.Theme);
    }

    [Fact]
    public void AccessibilityChangesUseSavedPaletteWithoutNativePreviewColors()
    {
        var saved = new AppSettings { Theme = AppTheme.Dark, AccentColor = "#A970FF", BackgroundColor = "#181818" };
        var contrast = Missum.App.App.CreateHighContrastPalette(Windows.UI.Color.FromArgb(255, 24, 40, 56),
            Windows.UI.Color.FromArgb(255, 240, 224, 208), Windows.UI.Color.FromArgb(255, 170, 187, 204));
        var appearance = Missum.App.App.CreateSavedAppearance(saved, false, contrast);
        Assert.True(appearance.HighContrast);
        Assert.Equal("high-contrast", appearance.Theme);
        Assert.Equal("#182838", appearance.Colors["window"]);
        Assert.Equal("#F0E0D0", appearance.Colors["text"]);
        Assert.Equal("#AABBCC", appearance.Colors["accent"]);
        Assert.Equal("#AABBCC24", appearance.Colors["accentSubtle"]);
        Assert.Equal("#A970FF", saved.AccentColor);
    }

    [Fact]
    public async Task AppearanceSnapshotUsesUiCapturedBrushesWithoutChangingStoredPreferences()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var original = settings.Current;
        var colors = new Dictionary<string, string> { ["window"] = "#252526", ["accent"] = "#A970FF", ["accentSubtle"] = "#A970FF24", ["layer"] = "#292929E6" };
        service.ResolvedAppearanceProvider = () => new("high-contrast", true, colors);
        service.ResolvedThemeProvider = () => "light";
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.get", new { }), Capture(events));

        var snapshot = Assert.Single(events).Payload;
        Assert.Equal("high-contrast", snapshot.GetProperty("resolvedTheme").GetString());
        var appearance = snapshot.GetProperty("resolvedAppearance");
        Assert.True(appearance.GetProperty("highContrast").GetBoolean());
        Assert.Equal("#A970FF24", appearance.GetProperty("colors").GetProperty("accentSubtle").GetString());
        Assert.Equal("#292929E6", appearance.GetProperty("colors").GetProperty("layer").GetString());
        Assert.Equal(original, settings.Current);
        Assert.Equal(original.AccentColor, snapshot.GetProperty("values").GetProperty("accentColor").GetString());
    }

    [Fact]
    public async Task AppearanceSnapshotWithoutNativeWindowKeepsServerThemeFallback()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { Theme = AppTheme.System });
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        service.ResolvedThemeProvider = () => "light";
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.get", new { }), Capture(events));

        var snapshot = Assert.Single(events).Payload;
        Assert.Equal("light", snapshot.GetProperty("resolvedTheme").GetString());
        Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("resolvedAppearance").ValueKind);
    }

    [Fact]
    public async Task FailedSettingsWriteDoesNotChangeMemoryRevisionOrPublishAnEvent()
    {
        var store = new FailingSettingsStore();
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        var changes = 0;
        settings.Changed += (_, _) => changes++;

        await Assert.ThrowsAsync<IOException>(() => settings.UpdatePreferencesAsync(
            current => current with { Language = "en-US" }, 0));

        Assert.Equal("de-DE", settings.Current.Language);
        Assert.Equal(0, settings.Current.PreferencesRevision);
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task BrowserPatchPersistsPreferencesAndKeepsNavigationModelsAndWindow()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var sessionId = Guid.NewGuid();
        await settings.UpdateAsync(current => current with { ActiveSessionId = sessionId,
            SelectedModel = "chosen-model", NavigationPaneWidth = 401, Window = new(12, 34, 1000, 800) });
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.update", new { expectedRevision = 0, values = new
        {
            missumAiServerUrl = "http://127.0.0.1:65001/", isAutomaticSpeechEnabled = true,
            theme = "light", accentColor = "#a970ff", codingToolStepsExpanded = true,
        } }), Capture(events));

        Assert.Equal("settings.changed", Assert.Single(events).Type);
        Assert.Equal(1, settings.Current.PreferencesRevision);
        Assert.Equal(sessionId, settings.Current.ActiveSessionId);
        Assert.Equal("chosen-model", settings.Current.SelectedModel);
        Assert.Equal(401, settings.Current.NavigationPaneWidth);
        Assert.Equal(new WindowPlacement(12, 34, 1000, 800), settings.Current.Window);
        Assert.Equal("http://127.0.0.1:65001", settings.Current.MissumAiServerUrl);
        using var reopenedStore = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        using var reopened = new SettingsCoordinator(reopenedStore);
        await reopened.InitializeAsync();
        Assert.True(reopened.Current.IsAutomaticSpeechEnabled);
        Assert.True(reopened.Current.CodingToolStepsExpanded);
        Assert.Equal("#A970FF", reopened.Current.AccentColor);
        Assert.Equal(1, reopened.Current.PreferencesRevision);
    }

    [Fact]
    public async Task StalePreferenceEditorReceivesCurrentSnapshotWithoutOverwritingIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        await service.UpdatePreferencesAsync(new() { Language = "en-US" }, 0);
        // Navigation changes do not invalidate a preferences editor.
        await settings.UpdateAsync(current => current with { NavigationPaneWidth = 350 });
        Assert.Equal(1, settings.Current.PreferencesRevision);
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.update", new { expectedRevision = 0,
            values = new { language = "de-DE" } }), Capture(events));

        var result = Assert.Single(events);
        Assert.Equal("settings.conflict", result.Type);
        Assert.Equal("en-US", settings.Current.Language);
        Assert.Equal(1, result.Payload.GetProperty("snapshot").GetProperty("revision").GetInt64());
        Assert.Equal("en-US", result.Payload.GetProperty("snapshot").GetProperty("values").GetProperty("language").GetString());
    }

    [Fact]
    public async Task NativePreferenceEditorUsesSameRevisionCheckAndKeepsItsDraft()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var viewModel = new SettingsViewModel(settings, connection, environment.Get<IPromptTriggerRepository>(),
            environment.Get<IBackupService>(), new ShellViewModel(), new ModelCapabilityRegistry(), service);
        viewModel.Initialize();
        viewModel.AccentColor = "#A970FF";
        await service.UpdatePreferencesAsync(new() { Language = "en-US" }, 0);

        await Assert.ThrowsAsync<AssistantSettingsConflictException>(() => viewModel.SavePreferencesAsync());

        Assert.Equal("#A970FF", viewModel.AccentColor);
        Assert.Equal("en-US", settings.Current.Language);
        Assert.Equal(AppSettings.DefaultAccentColor, settings.Current.AccentColor);
    }

    [Theory]
    [InlineData("{\"window\":{\"width\":500}}")]
    [InlineData("{\"accentColor\":\"red\"}")]
    [InlineData("{\"theme\":\"unknown\"}")]
    [InlineData("{\"missumAiServerUrl\":\"file:///C:/private\"}")]
    [InlineData("{\"isAutomaticSpeechEnabled\":\"true\"}")]
    public async Task InvalidOrInternalPreferenceFieldsAreRejected(string values)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        using var document = JsonDocument.Parse("{\"expectedRevision\":0,\"values\":" + values + "}");
        var events = new List<(string Type, JsonElement Payload)>();
        await service.HandleAsync(new(2, "settings.update", "invalid", document.RootElement.Clone()), Capture(events));
        Assert.Equal("settings.error", Assert.Single(events).Type);
        Assert.Equal(0, settings.Current.PreferencesRevision);
    }

    [Fact]
    public async Task ConnectionTestUsesUnsavedEditorAddressWithoutChangingSettings()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var original = settings.Current.MissumAiServerUrl;
        var probe = new GatewayProbe();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance, () => probe);
        using var service = Service(environment, settings, connection);
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.connectionTest", new { missumAiServerUrl = "http://192.168.1.99:3210" }), Capture(events));

        Assert.Equal(3, probe.Addresses.Count);
        Assert.All(probe.Addresses, address => Assert.Equal("192.168.1.99:3210", address.Authority));
        Assert.Equal(original, settings.Current.MissumAiServerUrl);
        Assert.Equal(0, settings.Current.PreferencesRevision);
        var result = Assert.Single(events);
        Assert.Equal("settings.connectionResult", result.Type);
        Assert.True(result.Payload.GetProperty("isReachable").GetBoolean());
        Assert.True(result.Payload.GetProperty("isReady").GetBoolean());
    }

    [Fact]
    public async Task TriggerConflictPreflightDoesNotPartiallyDeleteOtherRowsOrSavePreferences()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var repository = environment.Get<IPromptTriggerRepository>();
        var first = await repository.CreateAsync(Trigger("first-test"));
        var stale = await repository.CreateAsync(Trigger("stale-test"));
        var updated = await repository.UpdateAsync(stale with { Phrase = "new-test" }, stale.Revision);
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("settings.update", new
        {
            expectedRevision = 0, values = new { language = "en-US" },
            deletedTriggers = new[] { new { id = first.Id, revision = first.Revision }, new { id = stale.Id, revision = stale.Revision } },
        }), Capture(events));

        Assert.Equal("settings.conflict", Assert.Single(events).Type);
        Assert.NotNull(await repository.GetAsync(first.Id));
        Assert.Equal(updated.Revision, (await repository.GetAsync(stale.Id))?.Revision);
        Assert.Equal("de-DE", settings.Current.Language);
    }

    [Fact]
    public async Task BrowserTriggerUpdatePreservesExistingMatchingPolicy()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var repository = environment.Get<IPromptTriggerRepository>();
        var existing = await repository.CreateAsync(Trigger("exact-test") with { MatchMode = PromptTriggerMatchMode.Exact, Priority = 987 });
        var events = new List<(string Type, JsonElement Payload)>();

        await service.HandleAsync(Envelope("promptTriggers.apply", new { triggers = new[]
        {
            new { id = existing.Id, revision = existing.Revision, action = "webSearch", phrase = "changed-test", description = "browser edit", isEnabled = false },
        } }), Capture(events));

        Assert.Equal("settings.changed", Assert.Single(events).Type);
        var saved = Assert.IsType<PromptTrigger>(await repository.GetAsync(existing.Id));
        Assert.Equal(PromptTriggerMatchMode.Exact, saved.MatchMode);
        Assert.Equal(987, saved.Priority);
        Assert.False(saved.IsEnabled);
        Assert.Equal(existing.Revision + 1, saved.Revision);
    }

    [Fact]
    public async Task BackupUploadIsValidatedThenDeferredUntilHostAllowsRestore()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        var backups = new RecordingBackupService();
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IPromptTriggerRepository>(), backups);
        var events = new List<(string Type, JsonElement Payload)>();
        await service.HandleAsync(Envelope("backup.restore", new { fileName = "uploaded.missumbackup", base64 = Convert.ToBase64String([1, 2, 3]) }), Capture(events));
        var ready = Assert.Single(events);
        Assert.Equal("backup.restoreReady", ready.Type);
        Assert.Equal(1, backups.ValidationCount);
        Assert.Equal(0, backups.RestoreCount);
        var id = ready.Payload.GetProperty("restoreId").GetGuid();
        events.Clear();
        service.RestoreHandler = (_, _) => Task.FromResult(false);
        await service.HandleAsync(Envelope("backup.restoreCommit", new { restoreId = id }), Capture(events));
        Assert.Equal("backup.restoreDeferred", Assert.Single(events).Type);
        Assert.Equal(0, backups.RestoreCount);

        events.Clear();
        service.RestoreHandler = async (prepared, token) => { await service.CompleteRestoreAsync(prepared.Id, token); return true; };
        await service.HandleAsync(Envelope("backup.restoreCommit", new { restoreId = id }), Capture(events));
        Assert.Equal("backup.restored", Assert.Single(events).Type);
        Assert.Equal(1, backups.RestoreCount);
        Assert.Equal(2, backups.ValidationCount);
    }

    [Fact]
    public async Task BrowserBackupReturnsOnlyAnAllocatedDownloadResource()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        var backups = new RecordingBackupService();
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IPromptTriggerRepository>(), backups);
        var events = new List<(string Type, JsonElement Payload)>();
        await service.HandleAsync(Envelope("backup.create", new { }), Capture(events));
        var ready = Assert.Single(events);
        Assert.Equal("backup.ready", ready.Type);
        Assert.False(ready.Payload.TryGetProperty("path", out _));
        var id = ready.Payload.GetProperty("id").GetGuid();
        Assert.True(service.TryGetResource(id, out var resource));
        Assert.True(File.Exists(resource!.Path));
        Assert.Equal("/assistant/settings-resources/" + id.ToString("D"), ready.Payload.GetProperty("url").GetString());
        Assert.False(service.TryGetResource(Guid.NewGuid(), out _));
    }

    private static MissumAiConnectionService Connection(SettingsCoordinator settings) =>
        new(settings, NullLogger<MissumAiConnectionService>.Instance);
    private static AssistantSettingsService Service(TestEnvironment environment, SettingsCoordinator settings, MissumAiConnectionService connection) =>
        new(settings, connection, environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>());
    private static WebBridgeEnvelope Envelope(string type, object payload) =>
        new(2, type, Guid.NewGuid().ToString("N"), JsonSerializer.SerializeToElement(payload));
    private static Func<string, object, string?, Task> Capture(List<(string Type, JsonElement Payload)> events) =>
        (type, payload, _) => { events.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web))); return Task.CompletedTask; };
    private static PromptTrigger Trigger(string phrase) => new(Guid.NewGuid(), PromptTriggerAction.WebSearch, phrase, "test",
        PromptTriggerMatchMode.Prefix, true, 100, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private sealed class GatewayProbe : HttpMessageHandler
    {
        public List<Uri> Addresses { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Addresses.Add(uri);
            var body = uri.AbsolutePath switch
            {
                "/v1/health/live" => """{"status":"live","protocolVersion":"1.0","timestamp":"2026-10-05T00:00:00Z"}""",
                "/v1/health/ready" => """{"status":"modelNotLoaded","protocolVersion":"1.0","timestamp":"2026-10-05T00:00:00Z"}""",
                "/v1/capabilities" => """{"protocolVersion":"1.0","serverVersion":"1.0","models":[],"serverTools":[],"clientTools":[],"uploadLimits":{},"mediaTypes":[],"supportsSseResume":true,"uploadChunkSize":8388608}""",
                _ => throw new InvalidOperationException("Unexpected path: " + uri.AbsolutePath),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RecordingBackupService : IBackupService
    {
        public int ValidationCount { get; private set; }
        public int RestoreCount { get; private set; }
        public async Task<BackupResult> CreateAsync(string destinationPath, CancellationToken cancellationToken = default)
        {
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3], cancellationToken);
            return new(destinationPath, new string('a', 64), DateTimeOffset.UtcNow);
        }
        public Task ValidateAsync(string backupPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(backupPath));
            ValidationCount++;
            return Task.CompletedTask;
        }
        public Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(File.Exists(backupPath));
            RestoreCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSettingsStore : ISettingsStore
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "unused-Missum-settings.json");
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AppSettings());
        }
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            _ = settings;
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("Injected write failure.");
        }
    }
}

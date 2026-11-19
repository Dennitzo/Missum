using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class NativeModelRootSettingsTests
{
    [Fact]
    public async Task NativeFolderSelectionSurvivesSettingsReload()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IBackupService>());

        await service.UpdatePreferencesAsync(new() { NativeModelRoot = environment.Directory }, 0);
        await settings.InitializeAsync();

        Assert.Equal(environment.Directory, settings.Current.LocalAiModelDirectory);
        Assert.Equal(1, settings.Current.PreferencesRevision);
    }
    [Theory]
    [InlineData("nativeModelRoot")]
    [InlineData("NativeModelRoot")]
    public async Task LegacyModelFolderIsRemovedWithoutLosingRemoteGateway(string legacyName)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath,
            JsonSerializer.Serialize(new Dictionary<string, object> {
                [legacyName] = @"D:\OldModels", ["missumAiServerUrl"] = "http://192.168.0.67:8080",
                ["language"] = "en-US", ["version"] = 13 }));

        var settings = await store.LoadAsync();

        Assert.Equal("http://192.168.0.67:8080", settings.MissumAiServerUrl);
        Assert.Equal("en-US", settings.Language);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.DoesNotContain(saved.RootElement.EnumerateObject(),
            item => item.Name.Equals("nativeModelRoot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreferencesExposeRemoteGatewayButNotLegacyModelFolder()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = new AssistantSettingsService(settings, connection,
            environment.Get<IBackupService>());

        await service.UpdatePreferencesAsync(new() { MissumAiServerUrl = "http://192.168.0.42:8080" }, 0);
        var values = JsonSerializer.SerializeToElement(await service.GetSnapshotAsync(), JsonSerializerOptions.Web).GetProperty("values");

        Assert.Equal("http://192.168.0.42:8080", values.GetProperty("missumAiServerUrl").GetString());
        Assert.False(values.TryGetProperty("nativeModelRoot", out _));
    }
}

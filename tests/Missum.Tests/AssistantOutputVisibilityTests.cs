using Missum.App.Services;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class AssistantOutputVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlayPreferenceSurvivesRestartAndOtherSettingsUpdates(bool visible)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with { AssistantOutputsVisible = visible });
        await settings.UpdateAsync(current => current with { AccentColor = "#9470DC" });
        using var reopened = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        using var restarted = new SettingsCoordinator(reopened);
        await restarted.InitializeAsync();
        Assert.Equal(visible, restarted.Current.AssistantOutputsVisible);
        Assert.Equal("#9470DC", restarted.Current.AccentColor);
    }

    [Fact]
    public async Task ExistingSettingsRetainThePreviousVisibleDefault()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath, "{\"version\":21}");
        Assert.True((await store.LoadAsync()).AssistantOutputsVisible);
    }
}

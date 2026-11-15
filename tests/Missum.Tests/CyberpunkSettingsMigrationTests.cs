using System.Text.Json;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class CyberpunkSettingsMigrationTests
{
    [Theory]
    [InlineData("accentColor", "#00FF9C")]
    [InlineData("AccentColor", "  #00ff9c  ")]
    public async Task ExistingCyberpunkPresetMigratesWithoutChangingOtherPreferences(string key, string legacyAccent)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = AppSettings.CurrentVersion,
            ["preferencesRevision"] = 7,
            [key] = legacyAccent,
            ["backgroundColor"] = "#181818",
            ["language"] = "en-US",
            ["selectedModel"] = "coding/kept-model",
        }));

        var settings = await store.LoadAsync();
        Assert.Equal(AppSettings.CyberpunkAccentColor, settings.AccentColor);
        Assert.Equal("#181818", settings.BackgroundColor);
        Assert.Equal("en-US", settings.Language);
        Assert.Equal("coding/kept-model", settings.SelectedModel);
        Assert.Equal(7, settings.PreferencesRevision);

        using var written = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.Equal("#FCEE0A", written.RootElement.GetProperty("accentColor").GetString());
        Assert.Equal(AppSettings.CyberpunkAccentColor, (await store.LoadAsync()).AccentColor);
    }

    [Fact]
    public async Task OtherAccentValuesRemainUnchanged()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath, """{"accentColor":"#A970FF","backgroundColor":"#261D45","version":21}""");
        var settings = await store.LoadAsync();
        Assert.Equal("#A970FF", settings.AccentColor);
        Assert.Equal("#261D45", settings.BackgroundColor);
    }
}

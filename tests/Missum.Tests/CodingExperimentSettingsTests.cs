using System.Text.Json;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class CodingExperimentSettingsTests
{
    [Theory]
    [InlineData("adaptive", false)]
    [InlineData("maximum", true)]
    public async Task RemovedPreferencesAreIgnoredAndDroppedOnSave(string reasoning, bool workingState)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var store = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        await File.WriteAllTextAsync(store.SettingsPath, JsonSerializer.Serialize(new {
            version = AppSettings.CurrentVersion, codingReasoningPolicy = reasoning,
            codingWorkingStateEnabled = workingState, codingSpecialistMode = "optional",
            selectedCodingModel = "coding/test", codingToolStepsExpanded = true }));
        using var settings = new SettingsCoordinator(store);
        await settings.InitializeAsync();
        await settings.UpdateAsync(static current => current);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(store.SettingsPath));
        Assert.False(json.RootElement.TryGetProperty("codingReasoningPolicy", out _));
        Assert.False(json.RootElement.TryGetProperty("codingWorkingStateEnabled", out _));
        Assert.False(json.RootElement.TryGetProperty("codingSpecialistMode", out _));
        Assert.Equal("coding/test", settings.Current.SelectedCodingModel);
        Assert.True(settings.Current.CodingToolStepsExpanded);
        Assert.Null(typeof(SettingsViewModel).GetProperty("CodingWorkingStateEnabled"));
        Assert.Null(typeof(SettingsViewModel).GetProperty("SelectedCodingReasoningOption"));
    }
}

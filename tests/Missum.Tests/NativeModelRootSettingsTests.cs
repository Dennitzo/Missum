using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class NativeModelRootSettingsTests
{
    private const string Nas = @"\\umbrel.local\Melow's Umbrel\Huggingface";

    [Theory]
    [InlineData(@"\\umbrel.local\Melow's Umbrel\Huggingface", @"\\umbrel.local\Melow's Umbrel\Huggingface")]
    [InlineData(@"  C:\Models\Huggingface\  ", @"C:\Models\Huggingface")]
    [InlineData(@"\\server\Daten\Modelle ü\..\Huggingface", @"\\server\Daten\Huggingface")]
    [InlineData("", null)]
    public void PathNormalizationIsSyntacticAndPreservesUncNames(string input, string? expected) =>
        Assert.Equal(expected, NativeModelRootPath.Normalize(input));

    [Theory]
    [InlineData(@"Models\Huggingface")]
    [InlineData(@"C:Models")]
    [InlineData(@"\server\share")]
    [InlineData(@"\\server")]
    [InlineData("https://server/models")]
    [InlineData("C:\\Models\nother")]
    [InlineData(@"C:\Models|other")]
    public void RelativeMalformedAndControlCharacterPathsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => NativeModelRootPath.Normalize(path));

    [Fact]
    public async Task NativeEditorSavesUncPathAndReloadsItWithoutChangingSelectionOrNavigation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        var sessionId = Guid.NewGuid();
        await settings.UpdateAsync(current => current with { ActiveSessionId = sessionId, SelectedModel = "coding/local-choice" });
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var editor = Editor(environment, settings, connection, service);
        editor.Initialize();
        Assert.False(editor.HasUnsavedChanges);
        editor.NativeModelRoot = Nas + "\\";
        Assert.True(editor.HasUnsavedChanges);

        await editor.SavePreferencesAsync();

        Assert.Equal(Nas, settings.Current.NativeModelRoot);
        Assert.Equal(Nas, editor.NativeModelRoot);
        Assert.False(editor.HasUnsavedChanges);
        Assert.Equal(1, settings.Current.PreferencesRevision);
        Assert.Equal(sessionId, settings.Current.ActiveSessionId);
        Assert.Equal("coding/local-choice", settings.Current.SelectedModel);
        using var reopened = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        var restored = await reopened.LoadAsync();
        Assert.Equal(Nas, restored.NativeModelRoot);
        Assert.Equal(1, restored.PreferencesRevision);
        var snapshot = JsonSerializer.SerializeToElement(await service.GetSnapshotAsync(), JsonSerializerOptions.Web);
        Assert.Equal(Nas, snapshot.GetProperty("values").GetProperty("nativeModelRoot").GetString());
    }

    [Fact]
    public async Task UnchangedEffectiveDefaultDoesNotCreateAnOverrideWhenOtherPreferencesAreSaved()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var editor = Editor(environment, settings, connection, service);
        editor.Initialize();
        Assert.False(string.IsNullOrWhiteSpace(editor.NativeModelRoot));
        editor.Language = "en-US";

        await editor.SavePreferencesAsync();

        Assert.Null(settings.Current.NativeModelRoot);
        Assert.Equal("en-US", settings.Current.Language);
        Assert.False(editor.HasUnsavedChanges);
    }

    [Fact]
    public async Task RootPreferenceParticipatesInRevisionConflictsAndPreservesTheNativeDraft()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);
        var editor = Editor(environment, settings, connection, service);
        editor.Initialize();
        var draft = Path.Combine(editor.NativeModelRoot, "Edited Models");
        editor.NativeModelRoot = draft;
        await service.UpdatePreferencesAsync(new() { NativeModelRoot = @"D:\Other Models" }, 0);

        await Assert.ThrowsAsync<AssistantSettingsConflictException>(() => editor.SavePreferencesAsync());

        Assert.Equal(draft, editor.NativeModelRoot);
        Assert.True(editor.HasUnsavedChanges);
        Assert.Equal(@"D:\Other Models", settings.Current.NativeModelRoot);
        Assert.Equal(1, settings.Current.PreferencesRevision);
    }

    [Fact]
    public async Task InvalidRootCannotPartiallySaveOtherPreferencesAndAnOverrideCanBeCleared()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = Connection(settings);
        using var service = Service(environment, settings, connection);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdatePreferencesAsync(
            new() { NativeModelRoot = "relative-models", Language = "en-US" }, 0));
        Assert.Equal("de-DE", settings.Current.Language);
        Assert.Equal(0, settings.Current.PreferencesRevision);
        await service.UpdatePreferencesAsync(new() { NativeModelRoot = Nas }, 0);
        await service.UpdatePreferencesAsync(new() { NativeModelRoot = "" }, 1);
        Assert.Null(settings.Current.NativeModelRoot);
        Assert.Equal(2, settings.Current.PreferencesRevision);
    }

    private static MissumAiConnectionService Connection(SettingsCoordinator settings) =>
        new(settings, NullLogger<MissumAiConnectionService>.Instance);

    private static AssistantSettingsService Service(TestEnvironment environment, SettingsCoordinator settings, MissumAiConnectionService connection) =>
        new(settings, connection, environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>());

    private static SettingsViewModel Editor(TestEnvironment environment, SettingsCoordinator settings,
        MissumAiConnectionService connection, AssistantSettingsService service) =>
        new(settings, connection, environment.Get<IPromptTriggerRepository>(), environment.Get<IBackupService>(),
            new ShellViewModel(), new ModelCapabilityRegistry(), service);
}

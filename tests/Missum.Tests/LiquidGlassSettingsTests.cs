using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Infrastructure;
using Missum.Infrastructure.Settings;

namespace Missum.Tests;

public sealed class LiquidGlassSettingsTests
{
    [Fact]
    public void LiquidGlassIsOffByDefaultSoTheClassicLookStays()
    {
        Assert.False(new AppSettings().IsLiquidGlassEnabled);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void HighContrastAlwaysDisablesTheGlass(bool enabled, bool highContrast, bool active) =>
        Assert.Equal(active, LiquidGlassAppearance.IsActive(enabled, highContrast));

    [Theory]
    [InlineData(LiquidGlassSurface.Window, false)]
    [InlineData(LiquidGlassSurface.Layer, true)]
    [InlineData(LiquidGlassSurface.LayerStrong, false)]
    [InlineData(LiquidGlassSurface.Input, true)]
    [InlineData(LiquidGlassSurface.Hover, false)]
    [InlineData(LiquidGlassSurface.Pressed, true)]
    [InlineData(LiquidGlassSurface.Selection, false)]
    public void InactiveGlassLeavesEveryOpaqueSurfaceUnchanged(LiquidGlassSurface surface, bool light)
    {
        Assert.Equal(0xF2202428u, LiquidGlassAppearance.Surface(surface, light, 0xFF1F1F1F, 0xF2202428, active: false));
        Assert.Equal(0xFFFAF8FCu, LiquidGlassAppearance.Surface(surface, light, 0xFFE4E1E6, 0xFFFAF8FC, active: false));
    }

    [Theory]
    [InlineData(true, 0xFFE4E1E6u)]
    [InlineData(false, 0xFF1F1F1Fu)]
    public void ActiveGlassTurnsSidebarsButtonsAndInputsIntoTranslucentPanes(bool light, uint window)
    {
        static uint Alpha(uint argb) => argb >> 24;
        Assert.Equal(0u, Alpha(LiquidGlassAppearance.Surface(LiquidGlassSurface.Window, light, window, 0xFF000000 | window, active: true)));
        var layer = Alpha(LiquidGlassAppearance.Surface(LiquidGlassSurface.Layer, light, window, 0xFF000000, active: true));
        var button = Alpha(LiquidGlassAppearance.Surface(LiquidGlassSurface.LayerStrong, light, window, 0xFF000000, active: true));
        var input = Alpha(LiquidGlassAppearance.Surface(LiquidGlassSurface.Input, light, window, 0xFF000000, active: true));
        var hover = Alpha(LiquidGlassAppearance.Surface(LiquidGlassSurface.Hover, light, window, 0xFF000000, active: true));
        // Every pane lets the blurred backdrop through, yet stacked controls stay
        // progressively more solid so text on buttons and inputs keeps its contrast.
        Assert.InRange(layer, 0x40u, 0xD0u);
        Assert.True(button > layer);
        Assert.True(input > layer);
        Assert.True(hover >= button);
        Assert.True(input < 0xFFu);
    }

    [Fact]
    public void GlassPanesAreFrostedFromTheWindowColorSoTheBackgroundShowsThrough()
    {
        // Cyberpunk background mixed into the dark window: panes keep its violet hue.
        const uint window = 0xFF2A2342;
        var pane = LiquidGlassAppearance.Surface(LiquidGlassSurface.Layer, light: false, window, 0xFF000000, active: true);
        var r = (pane >> 16) & 0xFF;
        var g = (pane >> 8) & 0xFF;
        var b = pane & 0xFF;
        Assert.True(b > r && b > g, $"pane {pane:X8} lost the background hue");
        // Frosting brightens towards white instead of darkening the glass.
        Assert.True(b > (window & 0xFF));
    }

    [Theory]
    [InlineData(true, 0xFFE4E1E6u)]
    [InlineData(false, 0xFF1F1F1Fu)]
    public void GlassEdgesAreBrightTranslucentRims(bool light, uint window)
    {
        var edge = LiquidGlassAppearance.Edge(light, window);
        Assert.InRange(edge >> 24, 0x20u, 0xD0u);
        Assert.True(((edge >> 16) & 0xFF) > ((window >> 16) & 0xFF));
    }

    [Theory]
    [InlineData(LiquidGlassMaterialKind.Pane, false)]
    [InlineData(LiquidGlassMaterialKind.Control, false)]
    [InlineData(LiquidGlassMaterialKind.Input, false)]
    [InlineData(LiquidGlassMaterialKind.Pane, true)]
    [InlineData(LiquidGlassMaterialKind.Control, true)]
    [InlineData(LiquidGlassMaterialKind.Input, true)]
    public void GlassMaterialsAreTranslucentAcrylicWithTheOpaqueClassicFallback(LiquidGlassMaterialKind kind, bool light)
    {
        var material = LiquidGlassAppearance.Material(kind, light, 0xFF2A2342, 0xE6302A40);
        // Translucent enough to frost the scene, tinted enough to read text on it.
        Assert.InRange(material.TintOpacity, 0.15, 0.6);
        Assert.InRange(material.LuminosityOpacity, 0.3, 0.9);
        Assert.Equal(0xFFu, material.TintColor >> 24);
        // Battery saver / transparency off show the classic opaque surface.
        Assert.Equal(0xFF302A40u, material.FallbackColor);
    }

    [Fact]
    public void GlassMaterialsCarryTheBackgroundHue()
    {
        var material = LiquidGlassAppearance.Material(LiquidGlassMaterialKind.Pane, light: false, 0xFF2A2342, 0xFF2A2342);
        var r = (material.TintColor >> 16) & 0xFF;
        var b = material.TintColor & 0xFF;
        Assert.True(b > r, $"tint {material.TintColor:X8} lost the background hue");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RimIsBrightWhereTheLightHitsAndFadesAcrossThePane(bool light)
    {
        var rim = LiquidGlassAppearance.Rim(light);
        Assert.Equal(0d, rim[0].Offset);
        Assert.Equal(1d, rim[^1].Offset);
        Assert.Equal(0xFFFFFFu, rim[0].Color & 0x00FFFFFF);
        Assert.True(rim[0].Color >> 24 > rim[1].Color >> 24, "specular edge must be the brightest part");
        Assert.All(rim, stop => Assert.True(stop.Color >> 24 > 0));
    }

    [Theory]
    [InlineData(0xA970FFu, true)]
    [InlineData(0xFCEE0Au, false)]
    public void ComposerRimReflectsTheAccentAtTheSendButtonCorner(uint accent, bool light)
    {
        var rim = LiquidGlassAppearance.Rim(light, accent);
        Assert.Equal(0xFFFFFFu, rim[0].Color & 0x00FFFFFF);
        Assert.Equal(accent, rim[^1].Color & 0x00FFFFFF);
        Assert.True(rim[^1].Color >> 24 >= 0xC0, "the accent reflection must be clearly visible");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SceneIsOpaqueAndLitByAccentAndBackground(bool light)
    {
        Assert.Equal(0xFF2A2342u, LiquidGlassAppearance.SceneBase(0x002A2342));
        var lights = LiquidGlassAppearance.SceneLights(light, 0xA970FF, 0x261D45, 0x1A1A1A);
        Assert.Contains(lights, item => (item.Color & 0x00FFFFFF) != 0xA970FF && (item.Color & 0x00FFFFFF) != 0xFFFFFF);
        Assert.Contains(lights, item => (item.Color & 0x00FFFFFF) == 0xA970FF);
        Assert.All(lights, item =>
        {
            Assert.InRange(item.Color >> 24, 0x10u, 0xB0u);
            Assert.InRange(item.RadiusX, 0.1, 1.0);
        });
        // The chat center stays calm: no light field is centered in the middle of the window.
        Assert.DoesNotContain(lights, item => Math.Abs(item.CenterX - 0.5) < 0.15 && Math.Abs(item.CenterY - 0.5) < 0.15);
        // The accent lights stay behind the sidebars and are subtle, so chat text stays readable:
        // one at the top left and its exact mirror at the top right.
        var accentLights = lights.Where(item => (item.Color & 0x00FFFFFF) == 0xA970FF).OrderBy(item => item.CenterX).ToArray();
        Assert.Equal(2, accentLights.Length);
        var (left, right) = (accentLights[0], accentLights[1]);
        Assert.True(left.CenterX + left.RadiusX <= 0.4);
        Assert.True(left.Color >> 24 <= 0x50);
        Assert.Equal(1 - left.CenterX, right.CenterX, 6);
        Assert.Equal((left.CenterY, left.RadiusX, left.RadiusY, left.Color), (right.CenterY, right.RadiusX, right.RadiusY, right.Color));
    }

    [Fact]
    public void ADarkBackgroundNeverDarkensTheSceneIntoStreaks()
    {
        // Pink accent on #181818 in dark mode: the window base (#1F1F1F) is lighter than the
        // background, so the background light would only draw dark rounded bands. It is left out.
        const uint window = 0x1F1F1F;
        var lights = LiquidGlassAppearance.SceneLights(false, 0xD95BA8, 0x181818, window);
        Assert.DoesNotContain(lights, item => (item.Color & 0x00FFFFFF) == 0x181818);
        Assert.Equal(2, lights.Count(item => (item.Color & 0x00FFFFFF) == 0xD95BA8));
        Assert.InRange(lights.Count, 1, LiquidGlassAppearance.MaximumSceneLights);
        // A lighter background still tints the scene.
        Assert.Contains(LiquidGlassAppearance.SceneLights(false, 0xD95BA8, 0x3A2A55, window),
            item => (item.Color & 0x00FFFFFF) == 0x3A2A55);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComposerLightsLiftTheGlassAndReflectTheAccent(bool light)
    {
        var lights = LiquidGlassAppearance.ComposerLights(light, 0xFF4C8DFF);
        Assert.Equal(0x4C8DFFu, lights.AccentGlowColor & 0x00FFFFFF);
        Assert.Equal(0x4C8DFFu, lights.AccentReflectionColor & 0x00FFFFFF);
        // The glow tints the glass without overpainting it; the reflection is the strongest accent light.
        Assert.InRange(lights.AccentGlowColor >> 24, 0x20u, 0x50u);
        // The glow stays close to the composer vertically, so it does not reach far into the chat.
        Assert.True(lights.AccentGlowRise > 0 && lights.AccentGlowRise <= lights.AccentGlowSpread / 2);
        Assert.True(lights.AccentReflectionColor >> 24 > lights.AccentGlowColor >> 24);
        Assert.True(lights.AccentGlowSpread >= 24 && lights.LiftSpread > 0 && lights.LiftOffsetY > 0);
        Assert.True(lights.LiftShadowColor >> 24 > 0);
        // Light glass gets a softer lift, so it does not turn grey.
        Assert.True(light ? lights.LiftShadowColor >> 24 < 0x60 : lights.LiftShadowColor >> 24 >= 0x60);
    }

    [Fact]
    public void SavedLiquidGlassDoesNotChangeThePublishedPaletteMatch()
    {
        // The browser palette is opaque either way; the glass flag applies after a restart.
        var saved = new AppSettings { Theme = AppTheme.Dark, AccentColor = "#A970FF", BackgroundColor = "#181818", IsLiquidGlassEnabled = true };
        Assert.True(Missum.App.App.MatchesSavedAppearance(saved, AppTheme.Dark, "#A970FF", "#181818"));
    }

    [Fact]
    public async Task TogglingLiquidGlassIsAPreferenceChangeThatPersists()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        SettingsChangedEventArgs? change = null;
        settings.Changed += (_, args) => change = args;

        await settings.UpdatePreferencesAsync(current => current with { IsLiquidGlassEnabled = true }, 0);

        Assert.NotNull(change);
        Assert.True(change.PreferencesChanged);
        Assert.Equal(1, settings.Current.PreferencesRevision);
        using var reopenedStore = new JsonSettingsStore(new MissumInfrastructureOptions { DataDirectory = environment.Directory });
        var reloaded = await reopenedStore.LoadAsync();
        Assert.True(reloaded.IsLiquidGlassEnabled);
        Assert.Equal(AppSettings.DefaultAccentColor, reloaded.AccentColor);
        Assert.Equal(AppSettings.DefaultBackgroundColor, reloaded.BackgroundColor);
    }

    [Fact]
    public async Task NativeEditorSavesLiquidGlassAlongsideAccentAndBackground()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IBackupService>());
        var viewModel = new SettingsViewModel(settings, connection,
            environment.Get<IBackupService>(), new ShellViewModel(), new ModelCapabilityRegistry(), service);
        viewModel.Initialize();
        Assert.False(viewModel.IsLiquidGlassEnabled);
        Assert.False(viewModel.HasUnsavedChanges);

        viewModel.IsLiquidGlassEnabled = true;
        viewModel.AccentColor = "#4C8DFF";
        viewModel.BackgroundColor = "#261D45";
        Assert.True(viewModel.HasUnsavedChanges);
        await viewModel.SavePreferencesAsync();

        Assert.False(viewModel.HasUnsavedChanges);
        Assert.True(settings.Current.IsLiquidGlassEnabled);
        Assert.Equal("#4C8DFF", settings.Current.AccentColor);
        Assert.Equal("#261D45", settings.Current.BackgroundColor);

        viewModel.IsLiquidGlassEnabled = false;
        await viewModel.SavePreferencesAsync();
        Assert.False(settings.Current.IsLiquidGlassEnabled);
        Assert.Equal("#4C8DFF", settings.Current.AccentColor);
    }

    [Fact]
    public async Task BrowserEditorCanReadAndToggleLiquidGlass()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        using var connection = new MissumAiConnectionService(settings, NullLogger<MissumAiConnectionService>.Instance);
        using var service = new AssistantSettingsService(settings, connection, environment.Get<IBackupService>());
        var events = new List<(string Type, JsonElement Payload)>();
        Func<string, object, string?, Task> capture = (type, payload, _) =>
        {
            events.Add((type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web)));
            return Task.CompletedTask;
        };

        await service.HandleAsync(new(2, "settings.update", "glass",
            JsonSerializer.SerializeToElement(new { expectedRevision = 0, values = new { isLiquidGlassEnabled = true } })), capture);

        var changed = Assert.Single(events);
        Assert.Equal("settings.changed", changed.Type);
        Assert.True(changed.Payload.GetProperty("values").GetProperty("isLiquidGlassEnabled").GetBoolean());
        Assert.True(settings.Current.IsLiquidGlassEnabled);

        events.Clear();
        using var invalid = JsonDocument.Parse("{\"expectedRevision\":1,\"values\":{\"isLiquidGlassEnabled\":\"yes\"}}");
        await service.HandleAsync(new(2, "settings.update", "invalid", invalid.RootElement.Clone()), capture);
        Assert.NotEqual("settings.changed", Assert.Single(events).Type);
        Assert.True(settings.Current.IsLiquidGlassEnabled);
    }
}

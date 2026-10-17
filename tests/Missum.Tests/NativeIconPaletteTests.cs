using Missum.Core.Models;
using Missum.App.Controls;
using Windows.UI;

namespace Missum.Tests;

public sealed class NativeIconPaletteTests
{
    private static readonly string[] IconRoles =
    [
        "Web", "Research", "Image", "Audio", "Speech", "Pdf", "Document", "Plan", "Code", "Folder",
        "Navigation", "Link", "Add", "Danger", "Settings", "Subagent",
    ];

    private static readonly string[] SurfaceRoles = ["window", "layer", "layerStrong", "input", "hover", "pressed"];

    private static readonly string[][] ThemeGroups =
    [
        ["Navigation", "Settings", "Add", "Link", "Folder"],
        ["Web"],
        ["Research", "Subagent"],
        ["Code"],
        ["Speech", "Audio"],
        ["Image"],
        ["Document", "Pdf", "Plan"],
    ];

    public static IEnumerable<object[]> ContrastCases()
    {
        foreach (var theme in new[] { AppTheme.Light, AppTheme.Dark })
        foreach (var accent in new[] { "#000000", "#FFFFFF", "#808080", "#A970FF", "#FF0000", "#00FF00", "#0000FF", "#FFFF00", NativeIconPalette.CyberpunkAccent })
        foreach (var background in new[] { "#000000", "#FFFFFF", "#181818", "#FF0000", "#0000FF" })
            yield return [theme, accent, background];
    }

    [Theory]
    [MemberData(nameof(ContrastCases))]
    public void IconsStayReadableOnNativeSurfacesAndSelectedStates(AppTheme theme, string accent, string background)
    {
        var settings = new AppSettings { Theme = theme, AccentColor = accent, BackgroundColor = background };
        var appearance = Missum.App.App.CreateSavedAppearance(settings, systemLight: false, highContrast: null);
        var window = Parse(appearance.Colors["window"]);
        var selection = Parse(appearance.Colors["accentSubtle"]);

        foreach (var iconRole in IconRoles)
        {
            var icon = Parse(appearance.Colors["icon" + iconRole]);
            Assert.Equal(255, icon.A);
            foreach (var surfaceRole in SurfaceRoles)
            {
                var surface = Composite(Parse(appearance.Colors[surfaceRole]), window);
                AssertContrast(icon, surface, $"{theme}, accent {accent}, background {background}, {iconRole} on {surfaceRole}");
                AssertContrast(icon, Composite(selection, surface),
                    $"{theme}, accent {accent}, background {background}, selected {iconRole} on {surfaceRole}");
            }
        }

        Assert.Equal(accent, settings.AccentColor);
        Assert.Equal(background, settings.BackgroundColor);
        Assert.Equal(accent, appearance.Colors["accent"]);
    }

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void AccentChangesEveryIconRoleWhilePreservingDistinctTopicGroups(AppTheme theme)
    {
        var purple = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = theme, AccentColor = "#A970FF", BackgroundColor = "#181818" }, false, null);
        var turquoise = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = theme, AccentColor = "#008C9E", BackgroundColor = "#181818" }, false, null);

        foreach (var role in IconRoles)
            Assert.NotEqual(purple.Colors["icon" + role], turquoise.Colors["icon" + role]);

        foreach (var appearance in new[] { purple, turquoise })
        {
            var ordinaryColors = ThemeGroups.Select(group => appearance.Colors["icon" + group[0]]).ToArray();
            Assert.Equal(ThemeGroups.Length, ordinaryColors.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain(appearance.Colors["iconDanger"], ordinaryColors);
            foreach (var group in ThemeGroups)
                foreach (var role in group)
                    Assert.Equal(appearance.Colors["icon" + group[0]], appearance.Colors["icon" + role]);
        }
    }

    [Theory]
    [InlineData(AppTheme.Light, "#000000")]
    [InlineData(AppTheme.Light, "#FFFFFF")]
    [InlineData(AppTheme.Light, "#808080")]
    [InlineData(AppTheme.Dark, "#000000")]
    [InlineData(AppTheme.Dark, "#FFFFFF")]
    [InlineData(AppTheme.Dark, "#808080")]
    public void NeutralAccentsRetainRecognizableTopicGroups(AppTheme theme, string accent)
    {
        var appearance = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = theme, AccentColor = accent, BackgroundColor = "#181818" }, false, null);
        var groupColors = ThemeGroups.Select(group => appearance.Colors["icon" + group[0]]).ToArray();
        Assert.Equal(ThemeGroups.Length, groupColors.Distinct(StringComparer.Ordinal).Count());
        // A neutral user accent may keep navigation neutral, but must not turn
        // the tool groups into a row of almost-identical grays.
        Assert.True(groupColors.Select(Parse).Count(color =>
            Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) >= 24) >= ThemeGroups.Length - 1);
    }

    [Fact]
    public void RelatedActionsShareTheirTopicColorAcrossAliases()
    {
        var accent = Parse("#A970FF");
        var surfaces = new[] { Parse("#FFFFFF") };
        string[][] aliases =
        [
            ["navigation", "settings", "add", "link", "folder", "project", "changes"], ["web", "source"],
            ["research", "subagent"], ["code", "lean"],
            ["speech", "audio", "captions", "translate", "audiobook"], ["image", "video"],
            ["document", "pdf", "attachment", "plan"], ["danger", "delete", "stop"],
        ];
        foreach (var group in aliases)
            foreach (var role in group)
                Assert.Equal(NativeIconPalette.ResolveColor(group[0], accent, surfaces), NativeIconPalette.ResolveColor(role, accent, surfaces));
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000", "#E6E6E6")]
    [InlineData("#000000", "#FFFFFF", "#FFFF00")]
    public void HighContrastUsesSystemForegroundForEveryIcon(string background, string foreground, string accent)
    {
        var highContrast = Missum.App.App.CreateHighContrastPalette(Parse(background), Parse(foreground), Parse(accent));
        var appearance = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = AppTheme.Light, AccentColor = "#A970FF", BackgroundColor = "#FF0000" }, true, highContrast);

        foreach (var role in IconRoles)
        {
            Assert.Equal(foreground, appearance.Colors["icon" + role]);
            foreach (var surfaceRole in SurfaceRoles)
                AssertContrast(Parse(appearance.Colors["icon" + role]), Parse(appearance.Colors[surfaceRole]),
                    $"High contrast {role} on {surfaceRole}");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SystemThemeUsesTheSameIconColorsAsItsResolvedTheme(bool systemLight)
    {
        var automatic = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = AppTheme.System, AccentColor = "#008C9E", BackgroundColor = "#0000FF" }, systemLight, null);
        var explicitTheme = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = systemLight ? AppTheme.Light : AppTheme.Dark, AccentColor = "#008C9E", BackgroundColor = "#0000FF" }, !systemLight, null);

        foreach (var role in IconRoles)
            Assert.Equal(explicitTheme.Colors["icon" + role], automatic.Colors["icon" + role]);
    }

    [Fact]
    public void SavedAppearanceDoesNotDependOnAnInterveningPreviewPalette()
    {
        var saved = new AppSettings { Theme = AppTheme.Light, AccentColor = "#A970FF", BackgroundColor = "#181818" };
        var before = Missum.App.App.CreateSavedAppearance(saved, false, null);
        var preview = Missum.App.App.CreateSavedAppearance(
            new AppSettings { Theme = AppTheme.Dark, AccentColor = "#00FF00", BackgroundColor = "#FFFFFF" }, false, null);
        var after = Missum.App.App.CreateSavedAppearance(saved, false, null);

        Assert.NotEqual(before.Colors["iconNavigation"], preview.Colors["iconNavigation"]);
        foreach (var entry in before.Colors)
            Assert.Equal(entry.Value, after.Colors[entry.Key]);
    }

    private static void AssertContrast(Color icon, Color surface, string description)
    {
        var contrast = Missum.App.App.TextContrastRatio(icon, surface);
        Assert.True(contrast >= 4.5, $"{description}: contrast {contrast:F4} is below 4.5.");
    }

    private static Color Parse(string value) => Color.FromArgb(value.Length == 9 ? Convert.ToByte(value[7..9], 16) : (byte)255,
        Convert.ToByte(value[1..3], 16), Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16));

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromArgb(255, (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)), (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }
}

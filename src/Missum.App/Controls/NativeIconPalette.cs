using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>Accent-derived colors shared by XAML and retained native controls. Planet artwork is independent.</summary>
public static class NativeIconPalette
{
    internal const string CyberpunkAccent = "#FCEE0A";
    internal static readonly Color CyberpunkAccentColor = Color.FromArgb(255, 252, 238, 10);
    private static IReadOnlyDictionary<string, Color> _colors = ResolveColors(
        Color.FromArgb(255, 169, 112, 255), [Color.FromArgb(255, 48, 48, 48)]);
    // WinUI brushes belong to their dispatcher. Color resolution itself is pure
    // and can also run for immutable saved-appearance snapshots off the UI thread.
    [ThreadStatic] private static Dictionary<string, SolidColorBrush>? _brushes;

    private static string RoleFor(string key, string actionId) => key.ToLowerInvariant() switch
    {
        _ when actionId.Contains("audiobook", StringComparison.OrdinalIgnoreCase) => "speech",
        "danger" or "delete" or "stop" or "warning" => "caution",
        "folder" or "project" or "changes" => "controls",
        "web" or "source" => "web",
        "research" or "subagent" => "research",
        "code" or "lean" => "code",
        "speech" or "audio" or "captions" or "translate" or "audiobook" => "speech",
        "image" or "video" => "visual",
        "pdf" or "document" or "attachment" or "plan" => "documents",
        _ => "controls",
    };

    public static Color ColorFor(string key, string actionId = "") => Volatile.Read(ref _colors)[RoleFor(key, actionId)];

    public static SolidColorBrush BrushFor(string key, string actionId = "")
    {
        var role = RoleFor(key, actionId);
        return BrushForRole(role);
    }

    internal static SolidColorBrush ForegroundBrushFor(string key) => BrushForRole(RoleFor(key, "") + "Foreground");

    private static SolidColorBrush BrushForRole(string role)
    {
        var brushes = _brushes ??= new(StringComparer.Ordinal);
        if (!brushes.TryGetValue(role, out var brush)) brushes[role] = brush = new SolidColorBrush(Volatile.Read(ref _colors)[role]);
        return brush;
    }

    internal static void Apply(Color accent, Color[] surfaces, Color? highContrastForeground = null)
    {
        var colors = ResolveColors(accent, surfaces, highContrastForeground);
        Volatile.Write(ref _colors, colors);
        if (_brushes is null) return;
        foreach (var (role, brush) in _brushes)
            if (brush.Color != colors[role]) brush.Color = colors[role];
    }

    internal static Color ResolveColor(string key, Color accent, Color[] surfaces, Color? highContrastForeground = null) =>
        ResolveColors(accent, surfaces, highContrastForeground)[RoleFor(key, "")];

    private static Dictionary<string, Color> ResolveColors(Color accent, Color[] surfaces, Color? highContrastForeground = null)
    {
        accent.A = 255;
        var (hue, saturation) = AccentHue(accent);
        // Navigation and output links use the accent; tool themes use distinct,
        // harmonized neighbouring and split-complementary colors.
        // Equal starting lightness and restrained saturation keep the families
        // coherent. Contrast correction then adapts them to the actual surfaces.
        Color Family(double offset) => FromHsl(hue + offset, saturation, .57);
        // Destructive actions remain recognizable, but follow the chosen accent.
        var caution = Color.FromArgb(255, (byte)Math.Round(accent.R * .3 + 218 * .7),
            (byte)Math.Round(accent.G * .3 + 65 * .7), (byte)Math.Round(accent.B * .3 + 72 * .7));
        Color Readable(Color color) => highContrastForeground ?? App.ReadableAccentColor(color, surfaces);
        // The named yellow preset has its own coordinated topic colors. All
        // families still pass through the same light/dark contrast correction.
        var cyberpunk = accent == CyberpunkAccentColor;
        Color Topic(double offset, uint cyberpunkRgb) => Readable(cyberpunk
            ? Color.FromArgb(255, (byte)(cyberpunkRgb >> 16), (byte)(cyberpunkRgb >> 8), (byte)cyberpunkRgb)
            : Family(offset));
        var colors = new Dictionary<string, Color>(StringComparer.Ordinal)
        {
            ["controls"] = Readable(accent),
            ["web"] = Topic(300, 0x00F0FF),
            ["research"] = Topic(30, 0xB388FF),
            ["code"] = Topic(240, 0x5B9DFF),
            ["speech"] = Topic(270, 0x51E9BD),
            ["visual"] = Topic(60, 0xFF4BA7),
            ["documents"] = Topic(150, 0xB9D5E8),
            ["caution"] = Readable(cyberpunk ? Color.FromArgb(255, 255, 74, 89) : caution),
        };
        foreach (var role in colors.Keys.ToArray())
            colors[role + "Foreground"] = App.ContrastForeground(colors[role]);
        return colors;
    }

    private static (double Hue, double Saturation) AccentHue(Color accent)
    {
        var red = accent.R / 255d; var green = accent.G / 255d; var blue = accent.B / 255d;
        var high = Math.Max(red, Math.Max(green, blue));
        var low = Math.Min(red, Math.Min(green, blue));
        var chroma = high - low;
        // Achromatic accents have no meaningful hue. Keep controls neutral but
        // retain a subdued Missum harmony so tool categories stay distinguishable.
        if (chroma < .06) return (264, .46);
        var lightness = (high + low) / 2;
        var saturation = chroma / (1 - Math.Abs(2 * lightness - 1));
        var hue = high == red ? (green - blue) / chroma
            : high == green ? (blue - red) / chroma + 2 : (red - green) / chroma + 4;
        return ((hue * 60 + 360) % 360, Math.Clamp(saturation * .65 + .25, .46, .78));
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360 / 60;
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var second = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (red, green, blue) = hue switch
        {
            < 1 => (chroma, second, 0d), < 2 => (second, chroma, 0d),
            < 3 => (0d, chroma, second), < 4 => (0d, second, chroma),
            < 5 => (second, 0d, chroma), _ => (chroma, 0d, second),
        };
        var offset = lightness - chroma / 2;
        byte Channel(double value) => (byte)Math.Clamp(Math.Round((value + offset) * 255), 0, 255);
        return Color.FromArgb(255, Channel(red), Channel(green), Channel(blue));
    }

    /// <summary>Classifies a file consistently for attachments and generated output links.</summary>
    public static string FileKey(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".avif" or ".bmp" or ".gif" or ".ico" or ".jpeg" or ".jpg" or ".png" or ".svg"
            or ".tif" or ".tiff" or ".webp" => "image",
        ".aac" or ".aiff" or ".flac" or ".m4a" or ".mp3" or ".ogg" or ".opus" or ".wav" or ".wma" => "audio",
        ".avi" or ".m4v" or ".mkv" or ".mov" or ".mp4" or ".mpeg" or ".mpg" or ".webm" => "video",
        ".c" or ".cpp" or ".cs" or ".css" or ".go" or ".h" or ".hpp" or ".html" or ".ipynb" or ".java"
            or ".js" or ".json" or ".jsx" or ".lean" or ".ps1" or ".psm1" or ".py" or ".r" or ".rs"
            or ".sh" or ".sql" or ".tex" or ".toml" or ".ts" or ".tsx" or ".xaml" or ".xml" or ".yaml"
            or ".yml" => "code",
        _ => "document",
    };
}

/// <summary>Exposes the same native icon palette to XAML without duplicating its color values.</summary>
public sealed class NativeIconResources : ResourceDictionary
{
    public NativeIconResources()
    {
        foreach (var key in new[]
        {
            "Web", "Research", "Image", "Audio", "Speech", "Pdf", "Document", "Plan", "Code", "Folder",
            "Navigation", "Link", "Add", "Danger", "Settings", "Subagent", "Success", "Warning",
        })
        {
            this["MissumIcon" + key + "Brush"] = NativeIconPalette.BrushFor(key);
            this["MissumIcon" + key + "ForegroundBrush"] = NativeIconPalette.ForegroundBrushFor(key);
        }
    }
}

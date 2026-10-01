using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>The shared, opaque colors used by native navigation, tools and output files.</summary>
public static class NativeIconPalette
{
    public static Color ColorFor(string key, string actionId = "") => key.ToLowerInvariant() switch
    {
        "attachment" or "navigation" or "link" => Color.FromArgb(255, 91, 156, 246),
        "web" => Color.FromArgb(255, 76, 148, 242),
        "research" => Color.FromArgb(255, 160, 124, 246),
        "image" or "video" => Color.FromArgb(255, 231, 104, 171),
        "audio" or "add" or "success" => Color.FromArgb(255, 70, 188, 133),
        "speech" or "captions" or "translate" => Color.FromArgb(255, 51, 184, 207),
        "pdf" or "danger" or "delete" or "stop" => Color.FromArgb(255, 218, 80, 82),
        "document" => Color.FromArgb(255, 55, 143, 234),
        "plan" or "audiobook" or "folder" or "project" => Color.FromArgb(255, 241, 157, 56),
        _ when actionId.Contains("audiobook", StringComparison.OrdinalIgnoreCase) => Color.FromArgb(255, 241, 157, 56),
        "code" or "changes" or "settings" => Color.FromArgb(255, 151, 116, 225),
        _ => Color.FromArgb(255, 151, 116, 225),
    };

    public static SolidColorBrush BrushFor(string key, string actionId = "") => new(ColorFor(key, actionId));

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
            "Navigation", "Link", "Add", "Danger", "Settings",
        })
            this["MissumIcon" + key + "Brush"] = NativeIconPalette.BrushFor(key);
    }
}

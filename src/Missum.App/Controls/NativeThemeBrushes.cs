using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Missum.App.Controls;

/// <summary>References mutable application brushes, so retained native controls follow the PC palette.</summary>
internal static class NativeThemeBrushes
{
    internal static SolidColorBrush Resource(string key, byte fallback) =>
        Resource(key, Color.FromArgb(255, fallback, fallback, fallback));

    internal static SolidColorBrush Resource(string key, Color fallback)
    {
        if (Application.Current is { } application && application.Resources.TryGetValue(key, out var value) && value is SolidColorBrush brush) return brush;
        return new SolidColorBrush(fallback);
    }

    internal static SolidColorBrush Text => Resource("MissumTextBrush", 248);
    internal static SolidColorBrush MutedText => Resource("MissumMutedTextBrush", 160);
    internal static SolidColorBrush ReadableAccent => Resource("MissumAccentReadableBrush", Microsoft.UI.Colors.MediumPurple);
}

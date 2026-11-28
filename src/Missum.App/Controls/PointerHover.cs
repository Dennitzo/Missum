using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Missum.App.Controls;

/// <summary>
/// Stable hover state for an element with children. PointerEntered and PointerExited are routed
/// events that bubble up from every child (icon, text, buttons), so a plain handler sees "exit"
/// whenever the pointer moves from one child to another inside the element and the hover effect
/// flickers off and on within milliseconds. This tracker reports a change only when the pointer
/// really enters or leaves the element's own bounds.
/// </summary>
internal static class PointerHover
{
    public static void Track(FrameworkElement element, Action<bool> changed)
    {
        var hovered = false;
        void Set(bool value)
        {
            if (hovered == value) return;
            hovered = value;
            changed(value);
        }
        element.PointerEntered += (_, _) => Set(true);
        element.PointerExited += (_, e) => { if (!IsInside(element, e)) Set(false); };
        element.PointerCanceled += (_, _) => Set(false);
        element.PointerCaptureLost += (_, e) => { if (!IsInside(element, e)) Set(false); };
        element.Unloaded += (_, _) => Set(false);
    }

    /// <summary>True while the pointer is still within the element (a bubbled child exit).</summary>
    public static bool IsInside(FrameworkElement element, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(element).Position;
        return point.X >= 0 && point.Y >= 0 && point.X < element.ActualWidth && point.Y < element.ActualHeight;
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

/// <summary>Uses a right-pointing closed chevron and a downward open chevron in native controls.</summary>
public sealed class NativeDropdownChevron : DependencyObject
{
    public const string ClosedGlyph = "\uE76C";
    public const string OpenGlyph = "\uE70D";

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NativeDropdownChevron), new PropertyMetadata(false, OnIsEnabledChanged));
    private static readonly DependencyProperty StateCallbackTokenProperty = DependencyProperty.RegisterAttached(
        "StateCallbackToken", typeof(long), typeof(NativeDropdownChevron), new PropertyMetadata(0L));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>Bind before ShowAt. The button content must contain a FontIcon chevron.</summary>
    public static void Bind(FlyoutBase flyout, Button button)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(button);
        SetExpanded(button, false);
        flyout.Opened += (_, _) => SetExpanded(button, true);
        flyout.Closed += (_, _) => SetExpanded(button, false);
    }

    public static void SetExpanded(Button button, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (button.Content is not DependencyObject content) return;
        var icon = FindDescendant(content, element => element is FontIcon { Glyph: ClosedGlyph or OpenGlyph or "\uE70E" }) as FontIcon;
        if (icon is not null) icon.Glyph = expanded ? OpenGlyph : ClosedGlyph;
    }

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Control control) return;
        var stateProperty = control switch
        {
            Expander => Expander.IsExpandedProperty,
            ComboBox => ComboBox.IsDropDownOpenProperty,
            _ => null,
        };
        if (stateProperty is null) return;
        if ((bool)args.OldValue)
        {
            control.Loaded -= OnControlLoaded;
            control.UnregisterPropertyChangedCallback(stateProperty, (long)control.GetValue(StateCallbackTokenProperty));
        }
        if (!(bool)args.NewValue) return;
        control.Loaded += OnControlLoaded;
        var token = control.RegisterPropertyChangedCallback(stateProperty, (element, _) => UpdateControl((Control)element));
        control.SetValue(StateCallbackTokenProperty, token);
        if (control.IsLoaded) UpdateControl(control);
    }

    private static void OnControlLoaded(object sender, RoutedEventArgs args) => UpdateControl((Control)sender);

    private static void UpdateControl(Control control)
    {
        if (!GetIsEnabled(control)) return;
        control.ApplyTemplate();
        var expanded = control switch { Expander expander => expander.IsExpanded, ComboBox combo => combo.IsDropDownOpen, _ => false };
        var partName = control is Expander ? "ExpandCollapseChevron" : "DropDownGlyph";
        var part = FindDescendant(control, element => element is FrameworkElement framework && framework.Name == partName);
        var glyph = expanded ? OpenGlyph : ClosedGlyph;
        if (part is AnimatedIcon animated)
        {
            // WinUI uses AnimatedChevronUpDownSmallVisualSource. Its glyph resources only affect
            // fallback rendering, so use that supported fallback instead of replacing the template.
            animated.Source = null;
            if (animated.FallbackIconSource is FontIconSource fallback) fallback.Glyph = glyph;
            else animated.FallbackIconSource = new FontIconSource
            {
                Glyph = glyph, FontSize = 12, FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Foreground = animated.Foreground, IsTextScaleFactorEnabled = false,
            };
        }
        else if (part is FontIcon icon) icon.Glyph = glyph;
    }

    private static DependencyObject? FindDescendant(DependencyObject root, Func<DependencyObject, bool> predicate)
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var element))
        {
            if (predicate(element)) return element;
            // Include content before templates are loaded (e.g. a freshly constructed flyout button).
            if (element is Panel panel)
            {
                foreach (var child in panel.Children) pending.Enqueue(child);
            }
            else
            {
                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                    pending.Enqueue(VisualTreeHelper.GetChild(element, index));
            }
        }
        return null;
    }
}

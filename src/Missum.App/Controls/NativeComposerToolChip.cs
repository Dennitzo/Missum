using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Missum.App.Controls;

public sealed class NativeComposerToolChip : Button
{
    private readonly FontIcon _icon = new() { FontSize = 13 };
    private readonly Border _close = new()
    {
        Width = 14, Height = 14, CornerRadius = new(7), Opacity = 0,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Gray),
        Child = new FontIcon { Glyph = "\uE711", FontSize = 8, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) },
    };
    private readonly TextBlock _label = new() { FontSize = 13, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private bool _hovered;
    private bool _keyboardFocused;
    private Storyboard? _transition;
    internal bool IsRemoveAffordanceVisible { get; private set; }

    public NativeComposerToolChip()
    {
        Height = MinHeight = 28; MinWidth = 28; MaxWidth = 220;
        Padding = new(7, 3, 7, 3); BorderThickness = new(0); CornerRadius = new(14);
        VerticalAlignment = VerticalAlignment.Center;
        ApplyBackground();
        ActualThemeChanged += (_, _) => ApplyBackground();
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 190, 190));
        var slot = new Grid { Width = 16, Height = 16 };
        slot.Children.Add(_icon); slot.Children.Add(_close);
        var row = new Grid { ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new() { Width = new(16) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(slot); Grid.SetColumn(_label, 1); row.Children.Add(_label);
        Content = row;
        PointerEntered += (_, _) => SetPointerState(true);
        PointerExited += (_, _) => SetPointerState(false);
        GettingFocus += (_, args) => SetKeyboardFocusState(args.FocusState == FocusState.Keyboard);
        GotFocus += (_, _) => SetKeyboardFocusState(FocusState == FocusState.Keyboard);
        LostFocus += (_, _) => SetKeyboardFocusState(false);
        Unloaded += (_, _) => { _hovered = false; _keyboardFocused = false; _transition?.Stop(); };
    }

    public void SetTool(string label, string glyph)
    {
        _label.Text = label; _icon.Glyph = glyph;
        ToolTipService.SetToolTip(this, label + " entfernen");
        AutomationProperties.SetName(this, label + " entfernen");
        UpdateAffordance();
    }

    internal void SetPointerState(bool hovered) { _hovered = hovered; UpdateAffordance(); }

    internal void SetKeyboardFocusState(bool focused) { _keyboardFocused = focused; UpdateAffordance(); }

    private void ApplyBackground()
    {
        Background = Application.Current.Resources.TryGetValue("MissumLayerStrongBrush", out var brush) && brush is Brush background
            ? background : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 40, 40, 40));
    }

    private void UpdateAffordance()
    {
        var show = _hovered || _keyboardFocused;
        IsRemoveAffordanceVisible = show;

        _transition?.Stop();
        var storyboard = new Storyboard();
        foreach (var (target, opacity) in new (FrameworkElement, double)[] { (_icon, show ? 0 : 1), (_close, show ? 1 : 0) })
        {
            var animation = new DoubleAnimation { To = opacity, Duration = new Duration(TimeSpan.FromMilliseconds(100)) };
            Storyboard.SetTarget(animation, target); Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
        }
        _transition = storyboard; storyboard.Begin();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

public sealed class NativeComposerToolChip : Button
{
    private readonly FontIcon _icon = new() { FontSize = 13 };
    private readonly Border _close = new()
    {
        Width = 14, Height = 14, CornerRadius = new(7), Opacity = 0,
        Background = NativeThemeBrushes.Resource("MissumAccentSubtleBrush", 40),
        Child = new FontIcon { Glyph = "\uE711", FontSize = 8, Foreground = NativeIconPalette.BrushFor("danger") },
    };
    private readonly TextBlock _label = new() { FontSize = 13, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private bool _hovered;
    private bool _keyboardFocused;
    internal bool IsRemoveAffordanceVisible { get; private set; }

    public NativeComposerToolChip()
    {
        Height = MinHeight = 28; MinWidth = 28; MaxWidth = 220;
        Padding = new(7, 3, 7, 3); BorderThickness = new(0); CornerRadius = new(14);
        VerticalAlignment = VerticalAlignment.Center;
        _icon.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(100) };
        _close.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(100) };
        ApplyBackground();
        ActualThemeChanged += (_, _) => ApplyBackground();
        Foreground = NativeThemeBrushes.MutedText;
        var slot = new Grid { Width = 16, Height = 16 };
        slot.Children.Add(_icon); slot.Children.Add(_close);
        var row = new Grid { ColumnSpacing = 5 };
        row.ColumnDefinitions.Add(new() { Width = new(16) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.Children.Add(slot); Grid.SetColumn(_label, 1); row.Children.Add(_label);
        Content = row;
        PointerHover.Track(this, SetPointerState);
        GettingFocus += (_, args) => SetKeyboardFocusState(args.FocusState == FocusState.Keyboard);
        GotFocus += (_, _) => SetKeyboardFocusState(FocusState == FocusState.Keyboard);
        LostFocus += (_, _) => SetKeyboardFocusState(false);
        Unloaded += (_, _) => { _hovered = _keyboardFocused = false; UpdateAffordance(); };
    }

    public void SetTool(string label, string glyph, Brush? iconBrush = null)
    {
        if (_label.Text != label)
        {
            _label.Text = label;
            ToolTipService.SetToolTip(this, label + " entfernen");
            AutomationProperties.SetName(this, label + " entfernen");
        }
        _icon.Glyph = glyph;
        _icon.Foreground = iconBrush ?? NativeIconPalette.BrushFor("tool");
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
        if (IsRemoveAffordanceVisible == show) return;
        IsRemoveAffordanceVisible = show;
        _icon.Opacity = show ? 0 : 1;
        _close.Opacity = show ? 1 : 0;
    }
}

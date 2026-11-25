using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Missum.App.Controls;

/// <summary>
/// Floating tool pill as in the Webdesign tab: styled like the Codex prompt window, translucent so
/// the content stays visible behind it, draggable by its grip (double-click resets), collapsible
/// to a small label. The control is a transparent canvas laid over its host; only the pill takes
/// pointer input.
/// </summary>
public sealed partial class FloatingToolbar : Canvas
{
    private readonly Border _pill = new();
    private readonly FrameworkElement _body;
    private readonly StackPanel _mini = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly Button _collapse = new() { Width = 32, Height = 30, Padding = new Thickness(0), CornerRadius = new CornerRadius(15), VerticalAlignment = VerticalAlignment.Center };
    private (double X, double Y)? _anchor;
    private Windows.Foundation.Point? _dragStart;
    private (double Left, double Top) _dragOrigin;
    private bool _hovered;

    public FloatingToolbar(string name, FrameworkElement body, string miniGlyph, string miniText)
    {
        _body = body;
        Canvas.SetZIndex(this, 9);
        SizeChanged += (_, _) => Place();
        _pill.CornerRadius = new CornerRadius(26);
        _pill.BorderThickness = new Thickness(1);
        _pill.Padding = new Thickness(6, 8, 10, 8);
        _pill.Translation = new System.Numerics.Vector3(0, 0, 32);
        _pill.Shadow = new ThemeShadow();
        _pill.SizeChanged += (_, _) => Place();
        _pill.PointerEntered += (_, _) => { _hovered = true; UpdateSurface(); };
        _pill.PointerExited += (_, _) => { _hovered = false; UpdateSurface(); };
        AutomationProperties.SetName(_pill, name);
        ActualThemeChanged += (_, _) => UpdateSurface();
        UpdateSurface();

        var layout = new Grid { ColumnSpacing = 6 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var grip = new Border
        {
            Width = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new FontIcon { Glyph = "", FontSize = 14, Foreground = NativeThemeBrushes.MutedText, VerticalAlignment = VerticalAlignment.Center },
        };
        AutomationProperties.SetName(grip, name + " verschieben");
        ToolTipService.SetToolTip(grip, "Ziehen zum Verschieben · Doppelklick setzt die Position zurück");
        grip.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        grip.PointerExited += (_, _) => { if (_dragStart is null) ProtectedCursor = null; };
        grip.PointerPressed += (_, args) =>
        {
            _dragStart = args.GetCurrentPoint(this).Position;
            _dragOrigin = (GetLeft(_pill), GetTop(_pill));
            grip.CapturePointer(args.Pointer);
            args.Handled = true;
        };
        grip.PointerMoved += (_, args) =>
        {
            if (_dragStart is not { } start) return;
            var point = args.GetCurrentPoint(this).Position;
            Move(_dragOrigin.Left + point.X - start.X, _dragOrigin.Top + point.Y - start.Y);
            args.Handled = true;
        };
        grip.PointerReleased += (_, args) => { grip.ReleasePointerCapture(args.Pointer); _dragStart = null; ProtectedCursor = null; };
        grip.PointerCaptureLost += (_, _) => { _dragStart = null; ProtectedCursor = null; };
        grip.DoubleTapped += (_, _) => { _anchor = null; Place(); };
        layout.Children.Add(grip);
        Grid.SetColumn(_body, 1);
        layout.Children.Add(_body);
        _mini.Children.Add(new FontIcon { Glyph = miniGlyph, FontSize = 14 });
        _mini.Children.Add(new TextBlock { Text = miniText, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_mini, 1);
        layout.Children.Add(_mini);
        _collapse.Click += (_, _) => SetCollapsed(!Collapsed);
        Grid.SetColumn(_collapse, 2);
        layout.Children.Add(_collapse);
        _pill.Child = layout;
        Children.Add(_pill);
        SetCollapsed(false);
    }

    public bool Collapsed { get; private set; }
    public (double Left, double Top) Position => (GetLeft(_pill), GetTop(_pill));
    public FrameworkElement Pill => _pill;

    public void SetCollapsed(bool collapsed)
    {
        Collapsed = collapsed;
        _body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _mini.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        _collapse.Content = new FontIcon { Glyph = collapsed ? "" : "", FontSize = 14 };
        var label = collapsed ? "Werkzeuge ausklappen" : "Werkzeuge einklappen";
        AutomationProperties.SetName(_collapse, label);
        ToolTipService.SetToolTip(_collapse, label);
    }

    public void Move(double left, double top)
    {
        var free = (Width: Math.Max(0, ActualWidth - _pill.ActualWidth), Height: Math.Max(0, ActualHeight - _pill.ActualHeight));
        left = Math.Clamp(left, 0, free.Width);
        top = Math.Clamp(top, 0, free.Height);
        SetLeft(_pill, left);
        SetTop(_pill, top);
        _anchor = (free.Width <= 0 ? 0.5 : left / free.Width, free.Height <= 0 ? 1 : top / free.Height);
    }

    private void Place()
    {
        if (ActualWidth <= 0 || _pill.ActualWidth <= 0) return;
        var free = (Width: Math.Max(0, ActualWidth - _pill.ActualWidth), Height: Math.Max(0, ActualHeight - _pill.ActualHeight));
        var (x, y) = _anchor ?? (0.5, 1);
        SetLeft(_pill, Math.Round(free.Width * x));
        SetTop(_pill, _anchor is null ? Math.Max(0, free.Height - 18) : Math.Round(free.Height * y));
    }

    /// <summary>Translucent like a pane of glass; a little more solid while the pointer is on it.</summary>
    private void UpdateSurface()
    {
        var input = NativeThemeBrushes.Resource("MissumInputBrush", 40).Color;
        _pill.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(_hovered ? (byte)0xE6 : (byte)0xB8, input.R, input.G, input.B));
        _pill.BorderBrush = NativeThemeBrushes.Resource("MissumStrokeBrush", 70);
    }

    /// <summary>A round tool button in the pill's style.</summary>
    public static Button Tool(string glyph, string text, string help, Action onClick, UIElement? icon = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(icon ?? new FontIcon { Glyph = glyph, FontSize = 13 });
        if (text.Length > 0) content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Height = 30, Padding = new Thickness(10, 0, 11, 0), CornerRadius = new CornerRadius(15) };
        AutomationProperties.SetName(button, text.Length > 0 ? text : help);
        ToolTipService.SetToolTip(button, help);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Separator() => new()
    {
        Width = 1, Margin = new Thickness(4, 4, 4, 4), Background = NativeThemeBrushes.Resource("MissumStrokeBrush", 70),
    };
}

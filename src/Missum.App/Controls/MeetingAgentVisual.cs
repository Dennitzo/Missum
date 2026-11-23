using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Missum.App.Controls;

/// <summary>
/// Missum's "camera": an animated mark instead of a video. Listening pulses a
/// soft ring, thinking spins short rays, writing draws the signature loop and
/// done shows a check with rays — the states people see in the meeting tile.
/// </summary>
internal sealed class MeetingAgentVisual : UserControl
{
    private static readonly Color FallbackAccent = Color.FromArgb(255, 0xA9, 0x70, 0xFF);

    /// <summary>The app's live accent brush; it follows theme and accent color changes.</summary>
    public static SolidColorBrush AccentBrush => NativeThemeBrushes.Resource("MissumAccentBrush", FallbackAccent);

    public static Color AccentColor => AccentBrush.Color;
    private readonly Canvas _canvas = new() { Width = 120, Height = 120 };
    private readonly Ellipse _pulse;
    private readonly Ellipse _disc;
    private readonly Path _loop;
    private readonly Path _check;
    private readonly Path _alert;
    private readonly Canvas _rays;
    private Storyboard? _storyboard;
    private string _state = "";

    public MeetingAgentVisual()
    {
        IsTabStop = false;
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = _canvas };
        var orange = AccentBrush;
        _pulse = new Ellipse
        {
            Width = 76, Height = 76, Stroke = orange, StrokeThickness = 2, Opacity = 0,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5), RenderTransform = new CompositeTransform(),
        };
        Place(_pulse, 22, 22);
        _disc = new Ellipse
        {
            Width = 64, Height = 64, Fill = orange,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5), RenderTransform = new CompositeTransform(),
        };
        Place(_disc, 28, 28);
        _rays = new Canvas
        {
            Width = 120, Height = 120,
            RenderTransformOrigin = new Windows.Foundation.Point(.5, .5), RenderTransform = new CompositeTransform(),
            Opacity = 0,
        };
        for (var index = 0; index < 10; index++)
        {
            var angle = index * Math.PI * 2 / 10;
            var inner = index % 2 == 0 ? 42 : 44;
            var outer = index % 2 == 0 ? 54 : 51;
            _rays.Children.Add(new Line
            {
                X1 = 60 + Math.Cos(angle) * inner, Y1 = 60 + Math.Sin(angle) * inner,
                X2 = 60 + Math.Cos(angle) * outer, Y2 = 60 + Math.Sin(angle) * outer,
                Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 3.2,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
        }
        _canvas.Children.Add(_rays);
        _loop = Stroke("M8,66 C30,66 44,66 54,58 C64,50 66,36 57,33 C48,30 44,44 52,54 C60,64 84,66 112,66", 4);
        _check = Stroke("M45,61 L56,72 L77,48", 6);
        _check.Opacity = 0;
        _alert = Stroke("M60,44 L60,64 M60,74 L60,75", 6);
        _alert.Opacity = 0;
        _canvas.Children.Add(_loop);
        _canvas.Children.Add(_check);
        _canvas.Children.Add(_alert);
        Loaded += (_, _) => Restart();
        Unloaded += (_, _) => { _storyboard?.Stop(); _storyboard = null; };
        SetState("idle");
    }

    public string State => _state;

    private Path Stroke(string data, double thickness)
    {
        var path = (Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            $"<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"{data}\"/>");
        path.Stroke = new SolidColorBrush(Colors.White);
        path.StrokeThickness = thickness;
        path.StrokeStartLineCap = PenLineCap.Round;
        path.StrokeEndLineCap = PenLineCap.Round;
        path.StrokeLineJoin = PenLineJoin.Round;
        Place(path, 0, 0);
        return path;
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        if (!_canvas.Children.Contains(element) && element is not Path) _canvas.Children.Add(element);
    }

    public void SetState(string state)
    {
        if (_state == state) return;
        _state = state;
        _loop.Opacity = state is "done" or "error" ? 0 : 1;
        _check.Opacity = state == "done" ? 1 : 0;
        _alert.Opacity = state == "error" ? 1 : 0;
        _disc.Opacity = state == "error" ? .55 : 1;
        _rays.Opacity = state is "thinking" or "done" ? 1 : state == "loading" ? .45 : 0;
        _pulse.Opacity = 0;
        _loop.StrokeDashArray = null;
        Restart();
    }

    private void Restart()
    {
        _storyboard?.Stop();
        _storyboard = null;
        if (!IsLoaded) return;
        var board = new Storyboard();
        switch (_state)
        {
            case "listening":
                board.Children.Add(Animate(_pulse, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 1, 1.45, 1.8, repeat: true));
                board.Children.Add(Animate(_pulse, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 1, 1.45, 1.8, repeat: true));
                board.Children.Add(Animate(_pulse, "Opacity", .75, 0, 1.8, repeat: true));
                break;
            case "thinking":
                board.Children.Add(Animate(_rays, "(UIElement.RenderTransform).(CompositeTransform.Rotation)", 0, 360, 2.4, repeat: true, ease: false));
                board.Children.Add(Animate(_disc, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 1, .94, .9, repeat: true, reverse: true));
                board.Children.Add(Animate(_disc, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 1, .94, .9, repeat: true, reverse: true));
                break;
            case "loading":
                // Slow, dim spin: the model is still being prepared.
                board.Children.Add(Animate(_rays, "(UIElement.RenderTransform).(CompositeTransform.Rotation)", 0, 360, 4.5, repeat: true, ease: false));
                board.Children.Add(Animate(_disc, "Opacity", 1, .55, 1.1, repeat: true, reverse: true));
                break;
            case "writing":
                _loop.StrokeDashArray = [60, 60];
                var draw = Animate(_loop, "StrokeDashOffset", 120, 0, 1.6, repeat: true, ease: false);
                draw.EnableDependentAnimation = true;
                board.Children.Add(draw);
                break;
            case "done":
                board.Children.Add(Animate(_rays, "(UIElement.RenderTransform).(CompositeTransform.ScaleX)", .7, 1, .45));
                board.Children.Add(Animate(_rays, "(UIElement.RenderTransform).(CompositeTransform.ScaleY)", .7, 1, .45));
                board.Children.Add(Animate(_check, "Opacity", 0, 1, .3));
                break;
            default:
                return;
        }
        _storyboard = board;
        board.Begin();
    }

    private static DoubleAnimation Animate(DependencyObject target, string property, double from, double to, double seconds,
        bool repeat = false, bool reverse = false, bool ease = true)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(seconds),
            AutoReverse = reverse,
            RepeatBehavior = repeat ? RepeatBehavior.Forever : new RepeatBehavior(1),
            EasingFunction = ease ? new CubicEase { EasingMode = EasingMode.EaseOut } : null,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }
}

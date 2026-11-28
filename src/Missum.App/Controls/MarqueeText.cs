using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Missum.App.Controls;

/// <summary>
/// A single-line text that is trimmed at rest and scrolls through its full length while the
/// pointer rests on (or keyboard focus is in) the owning element. Short texts never move.
/// </summary>
internal static class MarqueeText
{
    private const double PixelsPerSecond = 42;
    private const double StartPause = 0.7;
    private const double EndPause = 1.1;

    public static FrameworkElement Create(string text, double fontSize, Brush foreground, Control trigger)
    {
        var trimmed = new TextBlock
        {
            Text = text, FontSize = fontSize, Foreground = foreground,
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var transform = new TranslateTransform();
        var moving = new TextBlock
        {
            Text = text, FontSize = fontSize, Foreground = foreground,
            TextWrapping = TextWrapping.NoWrap, RenderTransform = transform,
        };
        // A canvas never constrains its child, so the full line exists and the host clips it.
        var track = new Canvas { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        track.Children.Add(moving);
        var host = new Grid();
        host.Children.Add(trimmed);
        host.Children.Add(track);
        host.SizeChanged += (_, e) =>
        {
            host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            track.Height = e.NewSize.Height;
        };
        Storyboard? storyboard = null;

        void Start()
        {
            if (storyboard is not null || host.ActualWidth <= 0) return;
            moving.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var overflow = moving.DesiredSize.Width - host.ActualWidth;
            // Runs even with Windows animation effects off: it only starts on an explicit hover or
            // keyboard focus and is the only way to read the full description.
            if (overflow <= 1) return;
            var travel = overflow / PixelsPerSecond;
            var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            void Key(double seconds, double value, bool jump = false)
            {
                var time = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds));
                animation.KeyFrames.Add(jump
                    ? new DiscreteDoubleKeyFrame { KeyTime = time, Value = value }
                    : new LinearDoubleKeyFrame { KeyTime = time, Value = value });
            }
            Key(0, 0, jump: true);
            Key(StartPause, 0);
            Key(StartPause + travel, -overflow);
            Key(StartPause + travel + EndPause, -overflow);
            Key(StartPause + travel + EndPause, 0, jump: true);
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
            storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            trimmed.Opacity = 0;
            track.Visibility = Visibility.Visible;
            storyboard.Begin();
        }

        void Stop()
        {
            if (storyboard is null) return;
            storyboard.Stop();
            storyboard = null;
            transform.X = 0;
            track.Visibility = Visibility.Collapsed;
            trimmed.Opacity = 1;
        }

        trigger.PointerEntered += (_, _) => Start();
        trigger.PointerExited += (_, _) => { if (trigger.FocusState == FocusState.Unfocused || trigger.FocusState == FocusState.Pointer) Stop(); };
        trigger.PointerCanceled += (_, _) => Stop();
        trigger.GotFocus += (_, _) => { if (trigger.FocusState == FocusState.Keyboard) Start(); };
        trigger.LostFocus += (_, _) => Stop();
        host.Unloaded += (_, _) => Stop();
        return host;
    }
}

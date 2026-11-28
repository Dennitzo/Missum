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
    private const double PixelsPerSecond = 36;
    private const double StartPause = 0.6;
    private const double EndPause = 1.2;
    private const int HoverDelayMilliseconds = 350;

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
            // Rasterized once and moved as a bitmap: no glyph re-rendering (shimmer) per frame.
            CacheMode = new BitmapCache(),
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
            // Calm back-and-forth: pause, glide to the end, pause, glide back. No jump to the start.
            var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            void Key(double seconds, double value) => animation.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = value, EasingFunction = ease,
            });
            Key(0, 0);
            Key(StartPause, 0);
            Key(StartPause + travel, -overflow);
            Key(StartPause + travel + EndPause, -overflow);
            Key(StartPause + travel * 2 + EndPause, 0);
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

        // Only a deliberate hover starts the text: sweeping across the menu leaves every row still.
        var hoverDelay = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        hoverDelay.Interval = TimeSpan.FromMilliseconds(HoverDelayMilliseconds);
        hoverDelay.IsRepeating = false;
        hoverDelay.Tick += (_, _) => Start();

        // PointerEntered/PointerExited bubble up from the row's children (icon, name, text), so an
        // exit is real only once the pointer has left the row itself; otherwise every small move
        // inside the row would restart the animation.
        bool Inside(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(trigger).Position;
            return point.X >= 0 && point.Y >= 0 && point.X < trigger.ActualWidth && point.Y < trigger.ActualHeight;
        }

        var hovered = false;
        var keyboardFocused = false;
        trigger.PointerEntered += (_, _) =>
        {
            if (hovered) return;
            hovered = true;
            if (storyboard is null) hoverDelay.Start();
        };
        trigger.PointerExited += (_, e) =>
        {
            if (!hovered || Inside(e)) return;
            hovered = false;
            hoverDelay.Stop();
            if (!keyboardFocused) Stop();
        };
        trigger.PointerCanceled += (_, _) => { hovered = false; hoverDelay.Stop(); if (!keyboardFocused) Stop(); };
        trigger.GotFocus += (_, _) =>
        {
            keyboardFocused = trigger.FocusState == FocusState.Keyboard;
            if (keyboardFocused) Start();
        };
        trigger.LostFocus += (_, _) => { keyboardFocused = false; if (!hovered) Stop(); };
        host.Unloaded += (_, _) => { hovered = keyboardFocused = false; hoverDelay.Stop(); Stop(); };
        return host;
    }
}

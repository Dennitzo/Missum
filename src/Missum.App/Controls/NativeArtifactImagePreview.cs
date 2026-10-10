using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Missum.App.Controls;

/// <summary>A complete image fitted to the available chat width, without cropping.</summary>
internal sealed class NativeArtifactImagePreview : Grid
{
    private const double MaximumHeight = 560;
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 13,
        Foreground = NativeThemeBrushes.MutedText,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
    };
    private double _aspectRatio;

    internal NativeArtifactImagePreview()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(0, 0, 0, 10);
        MaxHeight = MaximumHeight;
        Height = 144;
        Visibility = Visibility.Collapsed;
        Children.Add(_image);
        Children.Add(_status);
        SizeChanged += (_, args) => FitHeight(args.NewSize.Width);
    }

    internal bool HasImage => _image.Source is not null;
    internal Image Image => _image;

    internal void Reset(bool visible)
    {
        _image.Source = null;
        _aspectRatio = 0;
        Height = 144;
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _status.Text = "Bildvorschau wird geladen …";
        _status.Visibility = Visibility.Visible;
    }

    internal void Show(BitmapImage source, uint width, uint height, string name)
    {
        _image.Source = source;
        _aspectRatio = width > 0 && height > 0 ? (double)width / height : 0;
        AutomationProperties.SetName(_image, name + ", Bildvorschau");
        _status.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        FitHeight(ActualWidth);
    }

    internal void ShowFailure(string message)
    {
        _image.Source = null;
        _aspectRatio = 0;
        Height = 96;
        _status.Text = "Bildvorschau nicht verfügbar: " + message;
        _status.Visibility = Visibility.Visible;
    }

    private void FitHeight(double width)
    {
        if (_aspectRatio <= 0 || !double.IsFinite(width) || width <= 0) return;
        var height = Math.Clamp(width / _aspectRatio, 1, MaximumHeight);
        if (Math.Abs(Height - height) > .5) Height = height;
    }
}
